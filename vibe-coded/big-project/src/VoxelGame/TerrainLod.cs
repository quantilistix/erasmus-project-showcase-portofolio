using System.Collections.Concurrent;
using GlmSharp;
using MYT.Kit.Vulkan;

namespace VoxelGame;

/// <summary>The same two-uint vertex format used by the full-detail voxel renderer.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal struct TerrainVertex(ivec3 point, int layer, int face, int corner, BlockType material, int resolution, bool horizon = false)
{
    public uint PackedPosition = (uint)(point.x | (point.y << 5) | (point.z << 10) | (face << 15) | (3 << 18) | (corner << 20));
    public uint PackedSurface = (uint)layer | ((uint)material << 8) | ((uint)System.Numerics.BitOperations.Log2((uint)resolution) << 24) | (horizon ? 1u << 31 : 0);
    public readonly int TextureLayer => (int)(PackedSurface & 255);
    public readonly int Face => (int)((PackedPosition >> 15) & 7);
    public readonly BlockType Material => (BlockType)((PackedSurface >> 8) & 4095);
    public readonly vec3 Position
    {
        get
        {
            int scale = 1 << (int)((PackedSurface >> 24) & 31);
            return new((PackedPosition & 31) * scale, ((PackedPosition >> 5) & 31) * scale,
                (PackedSurface & (1u << 31)) != 0 ? TerrainGenerator.SeaLevel + 1 : ((PackedPosition >> 10) & 31) * scale);
        }
    }
}

/// <summary>Volume LOD made from downsampled, immutable voxel snapshots.</summary>
internal sealed class TerrainLod : IDisposable
{
    internal readonly record struct Tile(int X, int Y, int Size, int Z = 0)
    {
        public int Height => Math.Min(Size, WorldHeight);
        public int Resolution => Size / Divisions;
        public int VerticalResolution => Resolution;
        public dvec3 Center => new(X + Size / 2.0, Y + Size / 2.0, Z + Height / 2.0);
        public double Radius => Math.Sqrt(Size * (double)Size / 2 + Height * (double)Height / 4);
    }
    internal readonly record struct Mesh(Tile Tile, TerrainVertex[] Vertices, long Version = 0, uint[]? GpuCells = null);
    private readonly record struct Job(Tile Tile, long Version);
    private readonly record struct Change(long Version, ivec2 Column);
    public const int RootSize = 1 << 20;
    public const int Divisions = 16;
    public const int WorldHeight = VoxelMap.HeightInChunks * 16;
    private readonly TerrainGenerator _generator;
    private readonly int[,]? _layers;
    private readonly bool _gpuMeshing;
    private readonly ConcurrentDictionary<ivec2, Source> _sources = new();
    private readonly ConcurrentDictionary<ivec2, int> _heights = new();
    private readonly BackgroundWorkers<Job, Mesh> _workers;
    private readonly HashSet<Tile> _pending = [];
    private readonly List<Tile> _wanted = [];
    private readonly List<Change> _changes = [];
    public IReadOnlyList<Tile> Wanted => _wanted;
    public long Version { get; private set; }
    public long SelectionVersion { get; private set; }

    public TerrainLod(TerrainGenerator generator, int[,]? layers = null, bool gpuMeshing = false)
    {
        _generator = generator;
        _layers = layers;
        _gpuMeshing = gpuMeshing;
        _workers = new("Voxel LOD", Math.Clamp(Environment.ProcessorCount / 4, 1, 4), job => Build(job.Tile) with { Version = job.Version });
    }

    // Published columns own their arrays; workers never read the live simulation grid.
    public void Publish(MapColumn column, bool edited = true)
    {
        ivec2 canonical = WorldTopology.Column(column.Coordinate);
        _sources[canonical] = new Source(column);
        if (edited) _changes.Add(new(++Version, canonical));
    }

    public bool IsCurrent(Tile tile, long version)
    {
        for (int i = _changes.Count - 1; i >= 0 && _changes[i].Version > version; i--)
        {
            ivec2 c = _changes[i].Column * 16;
            // Include the mesher's halo. Translated copies share the same source.
            int margin = Math.Max(16, tile.Resolution);
            if (Overlaps(tile.X - margin, tile.Size + 2 * margin, c.x)
                && Overlaps(tile.Y - margin, tile.Size + 2 * margin, c.y)) return false;
        }
        return true;

        static bool Overlaps(int low, int length, int column)
        {
            if (length >= WorldTopology.Width) return true;
            int delta = (int)(((long)column - low) % WorldTopology.Width + WorldTopology.Width) % WorldTopology.Width;
            return delta < length || delta + 16 > WorldTopology.Width;
        }
    }

    private ivec3? _selectionCenter;
    private double _selectionDistance;
    public void Select(dvec3 eye, double detailDistance)
    {
        ivec3 center = (ivec3)dvec3.Floor(eye / 16);
        if (_selectionCenter == center && _selectionDistance == detailDistance) return;
        _selectionCenter = center;
        _selectionDistance = detailDistance;
        RebuildSelection(new dvec3(center * 16) + 8, detailDistance);
        SelectionVersion++;
    }

    internal static bool UsesFullDetail(ivec3 chunk, dvec3 eye, double distance)
        => (new dvec3(chunk * 16) + 8 - eye).LengthSqr <= distance * distance;

    private void RebuildSelection(dvec3 eye, double detailDistance)
    {
        _wanted.Clear();
        int x = (int)Math.Floor(eye.x / RootSize) * RootSize;
        int y = (int)Math.Floor(eye.y / RootSize) * RootSize;
        for (int j = -1; j <= 1; j++)
        for (int i = -1; i <= 1; i++) Visit(new(x + i * RootSize, y + j * RootSize, RootSize));
        // Generation follows distance in every direction, independently of the view.
        _wanted.Sort((a, b) => Distance(a).CompareTo(Distance(b)));
        double Distance(Tile t)
        {
            double dx = Math.Max(Math.Max(t.X - eye.x, eye.x - t.X - t.Size), 0);
            double dy = Math.Max(Math.Max(t.Y - eye.y, eye.y - t.Y - t.Size), 0);
            double dz = Math.Max(Math.Max(t.Z - eye.z, eye.z - t.Z - t.Height), 0);
            return dx * dx + dy * dy + dz * dz;
        }
        void Visit(Tile t)
        {
            double distance = Distance(t);
            double splitDistance = detailDistance * t.Resolution / 2.0;
            if (t.Size > 16 && distance < splitDistance * splitDistance)
            {
                int half = t.Size / 2;
                for (int z = t.Z; z < t.Z + t.Height; z += half)
                {
                    Visit(new(t.X, t.Y, half, z)); Visit(new(t.X + half, t.Y, half, z));
                    Visit(new(t.X, t.Y + half, half, z)); Visit(new(t.X + half, t.Y + half, half, z));
                }
            }
            else _wanted.Add(t);
        }
    }

    public void Request(Tile tile)
    {
        if (_workers.Pending < 64 && _pending.Add(tile)) _workers.Post(new(tile, Version));
    }
    public bool TryTake(out Mesh mesh)
    {
        if (!_workers.TryTake(out mesh)) return false;
        _pending.Remove(mesh.Tile);
        return true;
    }

    private Source SourceAt(ivec2 column)
        => _sources.GetOrAdd(WorldTopology.Column(column), static (p, generator) => new Source(generator.GenerateColumn(p)), _generator);

    internal BlockType CellAt(int x, int y, int z, int resolution, int verticalResolution, bool surface = false)
    {
        if (z < 0 || z >= WorldHeight) return BlockType.Air;
        x = WorldTopology.Wrap(x); y = WorldTopology.Wrap(y);
        ivec2 sourceColumn = new((int)Math.Floor(x / 16.0), (int)Math.Floor(y / 16.0));
        if (resolution <= 16 && _sources.TryGetValue(sourceColumn, out Source? known))
            return known.At(x, y, z, resolution, surface);
        // Unvisited generated terrain cannot extend above 240. Avoid generating whole
        // columns just to establish that high-altitude LOD chunks are empty.
        if (resolution <= 4 && z >= 240) return BlockType.Air;
        if (resolution <= 4)
            return SourceAt(sourceColumn).At(x, y, z, resolution, surface);
        // Stratified volume sampling keeps distant meshes bounded. Edited snapshots always
        // take precedence over procedural terrain, even after their columns are unloaded.
        Span<int> counts = stackalloc int[Blocks.Count];
        counts.Clear();
        for (int j = 0; j < 4; j++)
        for (int i = 0; i < 4; i++)
        {
            int px = WorldTopology.Wrap(x + (2 * i + 1) * resolution / 8);
            int py = WorldTopology.Wrap(y + (2 * j + 1) * resolution / 8);
            ivec2 column = new((int)Math.Floor(px / 16.0), (int)Math.Floor(py / 16.0));
            _sources.TryGetValue(column, out Source? source);
            int top = source == null ? _heights.GetOrAdd(new(px, py), static (p, g) => g.Height(p.x, p.y), _generator) : 0;
            if (surface)
            {
                BlockType material = BlockType.Air;
                if (source != null)
                {
                    for (int pz = Math.Min(z + verticalResolution - 1, WorldHeight - 1); pz >= z; pz--)
                    {
                        material = source.At(px, py, pz, 1);
                        if (material != BlockType.Air) break;
                    }
                }
                else if (z <= Math.Max(top, TerrainGenerator.SeaLevel))
                    material = top < TerrainGenerator.SeaLevel ? BlockType.Water
                        : top < TerrainGenerator.SeaLevel + 2 || _generator.BiomeAt(px, py, top) == TerrainGenerator.Biome.Desert ? BlockType.Sand
                        : _generator.BiomeAt(px, py, top) == TerrainGenerator.Biome.Mountains ? BlockType.Stone : BlockType.Grass;
                counts[(int)material]++;
                continue;
            }
            for (int k = 0; k < 4; k++)
            {
                int pz = z + (2 * k + 1) * verticalResolution / 8;
                BlockType type = source != null ? source.At(px, py, pz, 1)
                    : pz <= top ? (pz < top - 3 ? BlockType.Stone : top < TerrainGenerator.SeaLevel + 2 || _generator.BiomeAt(px, py, top) == TerrainGenerator.Biome.Desert ? BlockType.Sand : BlockType.Grass)
                    : pz <= TerrainGenerator.SeaLevel ? BlockType.Water : BlockType.Air;
                counts[(int)type]++;
            }
        }
        return Dominant(counts, surface ? 16 : 64);
    }

    private static BlockType Dominant(ReadOnlySpan<int> counts, int samples)
    {
        if (samples - counts[0] < samples / 4) return BlockType.Air;
        int best = 1;
        for (int i = 2; i < counts.Length; i++) if (counts[i] > counts[best]) best = i;
        return (BlockType)best;
    }

    private sealed class Source
    {
        private readonly MapColumn _column;
        private readonly int _highest;
        private sealed record Level(BlockType[] Cells, BlockType[] Tops);
        private readonly Lazy<Level>[] _levels;
        public Source(MapColumn column)
        {
            _column = column;
            _highest = column.Chunks.Length == 0 ? 0 : (column.Chunks.Max(c => c.Z) + 1) * 16;
            _levels = Enumerable.Range(0, 5).Select(level => new Lazy<Level>(() => BuildLevel(1 << level))).ToArray();
        }
        public BlockType At(int x, int y, int z, int resolution, bool surface = false)
        {
            if (z < 0 || z >= _highest || z >= WorldHeight) return BlockType.Air;
            int side = 16 / resolution;
            Level level = _levels[System.Numerics.BitOperations.TrailingZeroCount((uint)resolution)].Value;
            BlockType[] cells = surface ? level.Tops : level.Cells;
            return cells[(x & 15) / resolution + side * ((y & 15) / resolution + side * (z / resolution))];
        }
        private Level BuildLevel(int resolution)
        {
            int side = 16 / resolution;
            BlockType[] cells = new BlockType[side * side * (WorldHeight / resolution)];
            if (resolution == 1)
            {
                foreach (var chunk in _column.Chunks)
                {
                    if (chunk.Z < 0 || chunk.Z >= VoxelMap.HeightInChunks) continue;
                    for (int i = 0; i < chunk.Cells.Length; i++) cells[chunk.Z * 4096 + i] = chunk.Cells[i].Type;
                }
                return new(cells, cells);
            }
            BlockType[] tops = new BlockType[cells.Length];
            int previous = resolution / 2;
            Span<int> counts = stackalloc int[Blocks.Count];
            for (int z = 0; z < WorldHeight; z += resolution)
            for (int y = 0; y < 16; y += resolution)
            for (int x = 0; x < 16; x += resolution)
            {
                counts.Clear();
                for (int k = 0; k < 2; k++)
                for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++) counts[(int)At(x + i * previous, y + j * previous, z + k * previous, previous)]++;
                int index = x / resolution + side * (y / resolution + side * (z / resolution));
                cells[index] = Dominant(counts, 8);
                counts.Clear();
                for (int j = 0; j < 2; j++)
                for (int i = 0; i < 2; i++)
                {
                    BlockType upper = At(x + i * previous, y + j * previous, z + previous, previous, true);
                    if (upper == BlockType.Air) upper = At(x + i * previous, y + j * previous, z, previous, true);
                    counts[(int)upper]++;
                }
                tops[index] = Dominant(counts, 4);
            }
            return new(cells, tops);
        }
    }

    internal Mesh Build(Tile tile)
    {
        const int n = Divisions;
        const int padded = n + 2;
        int step = tile.Resolution, vertical = tile.VerticalResolution;
        if (step >= WorldHeight) return BuildHorizon(tile);
        BlockType[] cells = new BlockType[padded * padded * padded];
        int Index(int x, int y, int z) => x + 1 + padded * (y + 1 + padded * (z + 1));
        for (int z = -1; z <= n; z++)
        for (int y = -1; y <= n; y++)
        for (int x = -1; x <= n; x++)
            cells[Index(x, y, z)] = CellAt(tile.X + x * step, tile.Y + y * step, tile.Z + z * vertical, step, vertical);
        // Fully enclosed chunks need no artificial hull. Only transition chunks need
        // boundary faces to seal different resolutions.
        bool enclosed = cells.All(t => t != BlockType.Air && t != BlockType.Water);
        if (enclosed) return new(tile, [], Version);
        if (_gpuMeshing)
        {
            // Workers downsample world data only. Face selection and geometry
            // emission happen on the GPU, including resolution-transition hulls.
            uint[] input = new uint[GpuMeshInputs.InputWords];
            for (int i = 0; i < cells.Length; i++) input[i] = GpuMeshInputs.Tag(cells[i]);
            for (int z = 0; z < n; z++) for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                BlockType type = cells[Index(x, y, z)];
                BlockType material = type;
                if (type != BlockType.Air && (z == n - 1 || VoxelMesher.Visible(new(type, 0), new(cells[Index(x, y, z + 1)], 0), true)))
                    material = CellAt(tile.X + x * step, tile.Y + y * step, tile.Z + z * vertical, step, vertical, surface: true);
                if (material == BlockType.Air || type == BlockType.Water || material == BlockType.Water
                    || Blocks.Get(type).Transparent != Blocks.Get(material).Transparent) material = type;
                input[VoxelMesher.PaddedVolume + x + n * (y + n * z)] = GpuMeshInputs.Tag(material);
            }
            return new(tile, [], Version, input);
        }
        List<TerrainVertex> vertices = new();
        for (int z = 0; z < n; z++)
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            BlockType type = cells[Index(x, y, z)];
            if (type == BlockType.Air) continue;
            for (int face = 0; face < 6; face++)
            {
                ivec3 direction = Grid3D<Voxel>.FaceDirections[face];
                int nx = x + direction.x, ny = y + direction.y, nz = z + direction.z;
                BlockType neighbor = cells[Index(nx, ny, nz)];
                bool boundary = nx < 0 || nx >= n || ny < 0 || ny >= n || nz < 0 || nz >= n;
                bool visible = VoxelMesher.Visible(new Voxel(type, 0), new Voxel(neighbor, 0), transparentLeaves: true);
                if (type == BlockType.Water && !visible) continue;
                if (!boundary && !visible) continue;
                BlockType material = face == 4 ? CellAt(tile.X + x * step, tile.Y + y * step, tile.Z + z * vertical, step, vertical, surface: true) : type;
                // A surface summary must not turn an opaque seabed into a translucent
                // water face: the coarse occupancy determines its rendering class.
                if (material == BlockType.Air || type == BlockType.Water || material == BlockType.Water
                    || Blocks.Get(type).Transparent != Blocks.Get(material).Transparent) material = type;
                int layer = _layers?[(int)material, face] ?? 0;
                ivec3[] corners = VoxelMesher.FaceCorners[face];
                for (int corner = 0; corner < 4; corner++)
                {
                    ivec3 p = new ivec3(x, y, z) + corners[corner];
                    vertices.Add(new(p, layer, face, corner, material, step));
                }
            }
        }
        return new(tile, vertices.ToArray(), Version);
    }

    // Once a voxel is as tall as the entire world, height detail is sub-pixel.
    // Collapse that terminal level to horizontal textured squares and the horizon.
    private Mesh BuildHorizon(Tile tile)
    {
        List<TerrainVertex> vertices = new(Divisions * Divisions * 4);
        uint[]? input = _gpuMeshing ? new uint[GpuMeshInputs.InputWords] : null;
        int step = tile.Resolution;
        for (int y = 0; y < Divisions; y++)
        for (int x = 0; x < Divisions; x++)
        {
            BlockType material = CellAt(tile.X + x * step, tile.Y + y * step, 0, step, WorldHeight, surface: true);
            if (material == BlockType.Air) material = BlockType.Water;
            if (input != null)
            {
                input[(x + 1) + 18 * (y + 1 + 18)] = GpuMeshInputs.Tag(material);
                input[VoxelMesher.PaddedVolume + x + 16 * y] = GpuMeshInputs.Tag(material);
                continue;
            }
            int layer = _layers?[(int)material, 4] ?? 0;
            ivec3[] corners = VoxelMesher.FaceCorners[4];
            for (int corner = 0; corner < 4; corner++)
            {
                ivec3 p = new ivec3(x, y, 0) + corners[corner];
                vertices.Add(new(p, layer, 4, corner, material, step, horizon: true));
            }
        }
        return new(tile, vertices.ToArray(), Version, input);
    }
    public void Dispose() => _workers.Dispose();
}
