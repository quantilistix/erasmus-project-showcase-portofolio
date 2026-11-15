using GlmSharp;
using MYT.Kit.Vulkan;

namespace VoxelGame;

/// <summary>
/// Persistent culled unit faces. A dense face array and its inverse map permit constant
/// time insertion/removal (swap the last face into the hole). Only cells within one
/// cell of an edit need visibility/AO work. Instances have a single owner at a time.
/// </summary>
internal sealed class IncrementalVoxelMesh
{
    private const int Size = VoxelMesher.ChunkSize, Padded = Size + 2;
    private readonly Voxel[] _previous = new Voxel[VoxelMesher.PaddedVolume];
    private readonly int[] _slots = new int[Size * Size * Size * 6];
    private readonly ulong[] _dirty = new ulong[Size * Size * Size / 64];
    private int[] _owners = new int[256];
    private uint[] _vertices = new uint[256 * 8];
    private uint[] _snapshot = [];
    private uint[]? _initialRenderMesh;
    private long _geometryVersion;
    private int _count;
    private bool _initialized, _transparentLeaves;

    internal int RebuiltCells { get; private set; }

    // Streaming can invalidate an unchanged border. Preserve the initial compressed
    // mesh in that case instead of promoting untouched terrain to GPU unit faces.
    internal bool TryReuseRenderMesh(Voxel[] cells, bool transparentLeaves, out uint[] vertices)
    {
        vertices = [];
        if (!_initialized || _transparentLeaves != transparentLeaves) return false;
        for (int i = 0; i < cells.Length; i++)
        {
            Voxel a = cells[i], b = _previous[i];
            if (a.Type != b.Type || a.Orientation != b.Orientation || a.Lit != b.Lit) return false;
        }
        vertices = _initialRenderMesh ?? _snapshot;
        return true;
    }

    public uint[] UpdateRenderMesh(Grid3D<Voxel> grid, ivec3 chunk, int[,] layers, bool transparentLeaves)
    {
        Voxel[] cells = Scratch ??= new Voxel[VoxelMesher.PaddedVolume];
        VoxelMesher.CopyPadded(grid, chunk, cells);
        return UpdateRenderMesh(cells, layers, transparentLeaves);
    }

    /// <summary>
    /// Compress the initial static mesh once. Keep its unit-face state ready for
    /// edits; unchanged invalidations reuse the compressed result, and the first
    /// geometry edit switches to local updates without rerunning greedy meshing.
    /// </summary>
    public uint[] UpdateRenderMesh(Voxel[] cells, int[,] layers, bool transparentLeaves = false)
    {
        long before = _geometryVersion;
        uint[] vertices = Update(cells, layers, transparentLeaves);
        if (before == 0 && _initialized && VoxelMesher.MergeRenderFaces && vertices.Length >= 256)
            _initialRenderMesh = VoxelMesher.BuildMergedRenderMesh(cells, layers, transparentLeaves);
        else if (_geometryVersion != before)
            _initialRenderMesh = null;
        return _initialRenderMesh ?? vertices;
    }

    public uint[] Update(Grid3D<Voxel> grid, ivec3 chunk, int[,] layers, bool transparentLeaves)
    {
        Voxel[] cells = Scratch ??= new Voxel[VoxelMesher.PaddedVolume];
        VoxelMesher.CopyPadded(grid, chunk, cells);
        return Update(cells, layers, transparentLeaves);
    }

    [ThreadStatic] private static Voxel[]? Scratch;

    public uint[] Update(Voxel[] cells, int[,] layers, bool transparentLeaves = false)
    {
        RebuiltCells = 0;
        // Exact partial-block geometry has a different face layout. Preserve its proven
        // mesher, and invalidate unit-face state when entering or leaving that layout.
        if (cells.Any(v => BlockShapes.HasShape(v.Type)))
        {
            _initialized = false;
            _geometryVersion++;
            return _snapshot = VoxelMesher.BuildRenderMeshReference(cells, layers, transparentLeaves);
        }

        bool rebuild = !_initialized || _transparentLeaves != transparentLeaves;
        if (rebuild)
        {
            Array.Fill(_slots, -1);
            Array.Fill(_dirty, ulong.MaxValue);
            _count = 0;
        }
        else
        {
            for (int z = -1; z <= Size; z++)
            for (int y = -1; y <= Size; y++)
            for (int x = -1; x <= Size; x++)
            {
                int index = Index(x, y, z);
                Voxel a = cells[index], b = _previous[index];
                if (a.Type == b.Type && a.Orientation == b.Orientation && a.Lit == b.Lit)
                    continue;
                // Includes diagonal AO dependencies, also at edges/corners of chunks.
                int lowX = Math.Max(0, x - 1), highX = Math.Min(Size - 1, x + 1);
                ulong row = (1UL << (highX - lowX + 1)) - 1;
                for (int cz = Math.Max(0, z - 1); cz <= Math.Min(Size - 1, z + 1); cz++)
                for (int cy = Math.Max(0, y - 1); cy <= Math.Min(Size - 1, y + 1); cy++)
                {
                    // A 16-cell row never crosses a 64-bit word. Mark all three
                    // x neighbours with one OR, especially useful for blast batches.
                    int cell = lowX | cy << 4 | cz << 8;
                    _dirty[cell >> 6] |= row << (cell & 63);
                }
            }
        }
        bool changed = rebuild;
        Span<uint> quad = stackalloc uint[8];
        Span<int> ao = stackalloc int[4];
        for (int word = 0; word < _dirty.Length; word++)
        {
            ulong bits = _dirty[word];
            _dirty[word] = 0;
            while (bits != 0)
            {
                int index = word * 64 + System.Numerics.BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                RebuiltCells++;
                ivec3 cell = new(index & 15, (index >> 4) & 15, index >> 8);
                Voxel voxel = cells[Index(cell.x, cell.y, cell.z)];
                for (int face = 0; face < 6; face++)
                {
                    int owner = index * 6 + face, slot = _slots[owner];
                    ivec3 n = Grid3D<Voxel>.FaceDirections[face];
                    if (voxel.Type == BlockType.Air || !VoxelMesher.Visible(voxel,
                        cells[Index(cell.x + n.x, cell.y + n.y, cell.z + n.z)], transparentLeaves))
                    {
                        if (slot < 0) continue;
                        int last = --_count;
                        _vertices.AsSpan(last * 8, 8).CopyTo(_vertices.AsSpan(slot * 8, 8));
                        _owners[slot] = _owners[last];
                        _slots[_owners[slot]] = slot;
                        _slots[owner] = -1;
                        changed = true;
                        continue;
                    }
                    for (int k = 0; k < 4; k++)
                        ao[k] = VoxelMesher.AmbientOcclusion(cells, cell, face, k);
                    int first = ao[0] + ao[2] >= ao[1] + ao[3] ? 0 : 1;
                    uint data = (uint)voxel.TextureLayer(layers, face) | (uint)index << 8;
                    int rotation = voxel.TextureRotation(face);
                    for (int k = 0; k < 4; k++)
                    {
                        int corner = (first + k) & 3;
                        ivec3 p = cell + VoxelMesher.FaceCorners[face][corner];
                        quad[k * 2] = (uint)(p.x | p.y << 5 | p.z << 10 | face << 15 |
                            ao[corner] << 18 | corner << 20 | rotation << 23);
                        quad[k * 2 + 1] = data;
                    }
                    if (slot < 0)
                    {
                        slot = _count++;
                        if (_count > _owners.Length)
                        {
                            Array.Resize(ref _owners, _owners.Length * 2);
                            Array.Resize(ref _vertices, _owners.Length * 8);
                        }
                        _slots[owner] = slot;
                        _owners[slot] = owner;
                    }
                    else if (quad.SequenceEqual(_vertices.AsSpan(slot * 8, 8))) continue;
                    quad.CopyTo(_vertices.AsSpan(slot * 8, 8));
                    changed = true;
                }
            }
        }
        cells.CopyTo(_previous, 0);
        _initialized = true;
        _transparentLeaves = transparentLeaves;
        // Immutable results can wait in the upload queue while the live mesh is edited.
        if (changed)
        {
            _snapshot = _vertices.AsSpan(0, _count * 8).ToArray();
            _geometryVersion++;
        }
        return _snapshot;
    }

    private static int Index(int x, int y, int z) => x + 1 + Padded * (y + 1 + Padded * (z + 1));
}
