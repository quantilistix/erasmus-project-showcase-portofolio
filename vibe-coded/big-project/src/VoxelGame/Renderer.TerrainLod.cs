using System.Runtime.InteropServices;
using GlmSharp;
using MYT.Kit.Vulkan;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VoxelGame;

internal sealed unsafe partial class Renderer
{
    private TerrainLod? _terrainLod;
    private readonly TerrainCover _terrainCover = new();
    private long _coverSelection = -1;
    private long _terrainMeshGeneration;
    private long _terrainCoverageGeneration;
    private (long Selection, long Meshes, long Detail, int Columns, dvec3 Eye) _lastCover;
    private (long Selection, long Terrain, long Results, long Detail, int Columns, dvec3 Eye) _lastRequests;
    private bool _hasCover, _hasRequests;
    private bool _coverReady;
    private const ulong LodStagingBytes = 16UL << 20;
    private readonly HostBuffer?[] _lodStaging = new HostBuffer?[GraphicsContext.FramesInFlight];
    private sealed class LodRecord(ArenaRange range, uint count, long version)
    {
        public bool HasWater;
        public long Version = version;
        public long CheckedAt = -1;
        public bool Current;
        public ArenaRange Range = range;
        public uint Count = count;
    }
    private readonly Dictionary<TerrainLod.Tile, LodRecord> _lodMeshes = [];
    private readonly HashSet<TerrainLod.Tile> _lodMeshing = [];
    private readonly List<TerrainCover.Region> _lodVisible = [];
    private readonly List<TerrainCover.Region> _lodShadows = [];
    public int LodTilesDrawn { get; private set; }
    internal bool TerrainReady { get; private set; }
    internal int CachedTerrainMeshes => _lodMeshes.Count;
    internal int TerrainCoverageGaps => _terrainCover.UncoveredRoots;

    private ChunkStreamer? _lodStreamer;
    private readonly HashSet<ivec2> _lodDirtyColumns = [];

    public void SetTerrain(TerrainGenerator generator, ChunkStreamer? streamer = null)
    {
        ClearTerrain();
        _terrainLod = new TerrainLod(generator, FaceLayers, gpuMeshing: GpuCulledMeshing);
        _lodStreamer = streamer;
        if (streamer != null)
        {
            foreach (MapColumn column in streamer.KeptColumns) RetainTerrain(column);
            streamer.ColumnRetained += RetainTerrain;
        }
    }

    private void ClearTerrain()
    {
        if (_lodStreamer != null) _lodStreamer.ColumnRetained -= RetainTerrain;
        _lodStreamer = null;
        _terrainLod?.Dispose();
        _terrainLod = null;
        _terrainCover.Clear();
        _coverSelection = -1;
        _hasCover = _hasRequests = false;
        foreach (LodRecord record in _lodMeshes.Values) Bury(record.Range);
        _lodMeshes.Clear();
        _lodMeshing.Clear();
        _lodVisible.Clear();
        _lodShadows.Clear();
    }

    private void RetainTerrain(MapColumn column) => _terrainLod?.Publish(column);

    private void CaptureTerrain(World world)
    {
        if (_terrainLod == null) return;
        _lodDirtyColumns.Clear();
        foreach (ivec3 chunk in world.Map.MeshDirty)
            _lodDirtyColumns.Add(new(chunk.x, chunk.y));
        foreach (ivec2 column in _lodDirtyColumns)
            if (world.Map.IsLoaded(column))
                _terrainLod.Publish(world.CopyColumn(column), world.Map.EditedColumns.Contains(column));
    }

    private bool CurrentTerrain(TerrainLod.Tile tile, LodRecord record)
    {
        if (record.CheckedAt != _terrainLod!.Version)
        {
            record.Current = _terrainLod.IsCurrent(tile, record.Version);
            record.CheckedAt = _terrainLod.Version;
        }
        return record.Current;
    }

    private void PrepareTerrain(CommandBuffer cmd, uint frame, Camera camera, IEnumerable<VoxelBody> bodies)
    {
        if (_terrainLod == null) return;
        long terrainStart = Prof.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        _terrainLod.Select(camera.Position, ViewDistance);
        _lodStaging[frame] ??= _graphics.CreateHostBuffer(LodStagingBytes, BufferUsageFlags.TransferSrcBit);
        HostBuffer staging = _lodStaging[frame]!.Value;
        ulong used = 0;
        // A tile has a fixed, small maximum size. Leave room before taking another result.
        const int tileBytes = TerrainLod.Divisions * TerrainLod.Divisions * TerrainLod.Divisions * 24 * 8;
        while (used + tileBytes <= LodStagingBytes && _terrainLod.TryTake(out TerrainLod.Mesh mesh))
        {
            _terrainMeshGeneration++;
            if (!_terrainLod.IsCurrent(mesh.Tile, mesh.Version)) continue;
            if (mesh.GpuCells != null)
            {
                if (_lodMeshing.Add(mesh.Tile))
                    QueueGpuMesh(new(0, default, mesh.Version, [], [], null, null, null, 0,
                        mesh.GpuCells, TerrainMesh: mesh, TerrainOwner: _terrainLod));
                continue;
            }
            if (_lodMeshes.Remove(mesh.Tile, out LodRecord? old)) Bury(old.Range);
            else _terrainCoverageGeneration++;
            ulong size = (ulong)mesh.Vertices.Length * (ulong)sizeof(TerrainVertex);
            ArenaRange range = default;
            if (size > 0)
            {
                MemoryMarshal.AsBytes(mesh.Vertices.AsSpan()).CopyTo(staging.As<byte>()[(int)used..]);
                range = _arena.Allocate(size);
                BufferCopy copy = new(used, range.Offset, size);
                _vk.CmdCopyBuffer(cmd, staging.Buffer, _arena.BufferOf(range.Page), 1, &copy);
            }
            _lodMeshes.Add(mesh.Tile, new(range, (uint)mesh.Vertices.Length, mesh.Version) { HasWater = mesh.Vertices.Any(v => v.Material == BlockType.Water) });
            _terrainCover.Available(mesh.Tile);
            used += size;
        }
        if (used > 0)
        {
            MemoryBarrier barrier = new()
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.VertexAttributeReadBit
            };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexInputBit, 0, 1, &barrier, 0, null, 0, null);
        }
        VoxelMap? map = bodies.OfType<VoxelMap>().FirstOrDefault();
        Frustum frustum = new(camera);
        _lodVisible.Clear();
        _lodShadows.Clear();
        TerrainReady = true;
        if (_coverSelection != _terrainLod.SelectionVersion)
        {
            _terrainCover.Reset(_terrainLod.Wanted, camera.Position);
            _coverSelection = _terrainLod.SelectionVersion;
        }
        var requestKey = (_terrainLod.SelectionVersion, _terrainLod.Version, _terrainMeshGeneration,
            _meshCoverageGeneration, map?.ColumnVersion ?? -1, camera.Position);
        if (!_hasRequests || _lastRequests != requestKey)
        {
            long requestsStart = Prof.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            foreach (TerrainLod.Tile tile in _terrainCover.Requests)
            {
                if (tile.Size == 16 && map != null && DetailedChunkReady(map, tile, camera.Position)) continue;
                if (!_lodMeshes.TryGetValue(tile, out var existing) || !CurrentTerrain(tile, existing))
                {
                    TerrainReady = false;
                    if (!_lodMeshing.Contains(tile)) _terrainLod.Request(tile);
                }
            }
            if (Prof.On) Prof.Add("lod.requests", System.Diagnostics.Stopwatch.GetElapsedTime(requestsStart).TotalMilliseconds);
            _lastRequests = requestKey;
            _hasRequests = true;
            _coverReady = TerrainReady;
        }
        else TerrainReady = _coverReady;
        // Replacing an already cached tile changes its vertex range, not which
        // regions have coverage. Likewise an edit request is not a loss of detail.
        var coverKey = (_terrainLod.SelectionVersion, _terrainCoverageGeneration,
            _meshCoverageGeneration, map?.ColumnVersion ?? -1, camera.Position);
        if (!_hasCover || _lastCover != coverKey)
        {
            long coverStart = Prof.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            _terrainCover.Select(tile => _lodMeshes.ContainsKey(tile),
                tile => map != null && DetailedChunkReady(map, tile, camera.Position));
            if (Prof.On) Prof.Add("lod.cover", System.Diagnostics.Stopwatch.GetElapsedTime(coverStart).TotalMilliseconds);
            _lastCover = coverKey;
            _hasCover = true;
        }
        mat4 shadowProjection = SunShadowProjection(camera.Position);
        foreach (TerrainCover.Region region in _terrainCover.Regions)
        {
            TerrainLod.Tile tile = region.Source;
            LodRecord record = _lodMeshes[tile];
            if (record.Count == 0) continue;
            if (frustum.CanSee(region.Bounds.Center, region.Bounds.Radius)) _lodVisible.Add(region);
            if (tile.Size <= 128 && Video.Sun && Video.DynamicShadows && Daylight.SunIntensity > 0)
            {
                vec3 p = (shadowProjection * new vec4((vec3)tile.Center, 1)).xyz;
                float margin = (float)(2 * tile.Radius / (Math.Min(ViewDistance, 128) + 16));
                if (Math.Abs(p.x) <= 1 + margin && Math.Abs(p.y) <= 1 + margin) _lodShadows.Add(region);
            }
        }
        _lodVisible.Sort((a, b) => _lodMeshes[a.Source].Range.Page.CompareTo(_lodMeshes[b.Source].Range.Page));
        _lodShadows.Sort((a, b) => _lodMeshes[a.Source].Range.Page.CompareTo(_lodMeshes[b.Source].Range.Page));
        if (Prof.On) Prof.Add("lod.total", System.Diagnostics.Stopwatch.GetElapsedTime(terrainStart).TotalMilliseconds);
        // Keep every generated mesh for this world. Looking away or leaving the
        // region must not discard terrain that has already been generated.
    }

    private void AbandonGpuTerrain(MeshResult result)
    {
        if (result.TerrainMesh is TerrainLod.Mesh terrain && ReferenceEquals(_terrainLod, result.TerrainOwner))
        {
            _lodMeshing.Remove(terrain.Tile);
            _terrainMeshGeneration++;
        }
    }

    private void CompleteGpuTerrain(MeshResult result, ArenaRange range, uint vertices, bool water)
    {
        TerrainLod.Mesh mesh = result.TerrainMesh!.Value;
        _lodMeshing.Remove(mesh.Tile);
        _terrainMeshGeneration++;
        if (_lodMeshes.Remove(mesh.Tile, out LodRecord? old)) Bury(old.Range);
        else _terrainCoverageGeneration++;
        _lodMeshes.Add(mesh.Tile, new(range, vertices, mesh.Version) { HasWater = water });
        _terrainCover.Available(mesh.Tile);
    }

    private bool DetailedChunkReady(VoxelMap map, TerrainLod.Tile tile, dvec3 eye)
    {
        ivec3 chunk = new(tile.X / 16, tile.Y / 16, tile.Z / 16);
        return TerrainLod.UsesFullDetail(chunk, eye, ViewDistance)
            && map.IsLoaded(new(chunk.x, chunk.y))
            && (!map.Grid.TryGetChunk(chunk, out _) || (_chunks.TryGetValue(map.Id, out var records)
                && records.TryGetValue(chunk, out var record) && record.HasMesh));
    }

    private void AppendTerrainDraws(Camera camera)
    {
        LodTilesDrawn = _lodVisible.Count;
        foreach (TerrainCover.Region region in _lodVisible)
        {
            LodRecord record = _lodMeshes[region.Source];
            Add(region, record, _visibleDraws);
            if (record.HasWater) Add(region, record, _waterDraws);
        }
        foreach (TerrainCover.Region region in _lodShadows) Add(region, _lodMeshes[region.Source], _sunDraws);

        void Add(TerrainCover.Region region, LodRecord record, List<VisibleDraw> destination)
        {
            TerrainLod.Tile tile = region.Source;
            float vertical = tile.Resolution >= TerrainLod.WorldHeight ? TerrainGenerator.SeaLevel + 1 : tile.Resolution;
            DrawData data = new()
            {
                Model = mat4.Translate(new vec3(tile.X, tile.Y, tile.Z)) * mat4.Scale(new vec3(tile.Resolution, tile.Resolution, vertical)),
                // Negative slots identify coarse terrain; the shaders use empty damage data.
                Chunk = new ivec4(0, 0, 0, -1),
                ClipMin = region.Bounds != tile ? new vec4(region.Bounds.X, region.Bounds.Y, region.Bounds.Z, 1) : vec4.Zero,
                ClipMax = new vec4(region.Bounds.X + region.Bounds.Size, region.Bounds.Y + region.Bounds.Size,
                    region.Bounds.Z + region.Bounds.Height, 0)
            };
            uint quads = record.Count / 4;
            for (uint first = 0; first < quads; first += MaxQuads)
            {
                uint count = Math.Min(quads - first, MaxQuads);
                destination.Add(new(data, record.Range.Page, count * 6, (int)(record.Range.Offset / 8 + first * 4),
                    (tile.Center - camera.Position).LengthSqr));
            }
        }
    }
}
