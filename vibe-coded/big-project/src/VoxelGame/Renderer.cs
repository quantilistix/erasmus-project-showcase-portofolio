using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using GlmSharp;

using MYT.Kit.Gui;
using MYT.Kit.Vulkan;

using Silk.NET.Vulkan;

using StbImageSharp;

using Buffer = Silk.NET.Vulkan.Buffer;
using Image = Silk.NET.Vulkan.Image;

namespace VoxelGame;


[StructLayout(LayoutKind.Sequential)]
internal struct LineVertex(vec3 position, vec4 color)
{
    public vec3 Position = position;
    public vec4 Color = color;
}

/// <summary>Where the view is from: eye position, unit view direction, vertical field of view (radians), width over height.</summary>
internal readonly record struct Camera(dvec3 Position, dvec3 Forward, double FieldOfView, double Aspect)
{
    public const float Near = 0.05f;
    /// <summary>The near plane of the held item's view: it is drawn closer to the eye than the world.</summary>
    public const float HandNear = 0.01f;

    /// <summary>
    /// View then projection for Vulkan: right-handed, z up in the world, y flipped so that up
    /// is up on screen, and depth reversed without a far plane: 1 at the near plane, falling
    /// toward 0 with distance, which keeps float depth precise however far the view reaches.
    /// </summary>
    public mat4 ViewProjection()
    {
        vec3 eye = (vec3)Position;
        mat4 view = mat4.LookAt(eye, eye + (vec3)Forward, vec3.UnitZ);
        float f = 1f / MathF.Tan((float)FieldOfView / 2f);
        mat4 projection = new
        (
            f / (float)Aspect, 0, 0, 0,
            0, -f, 0, 0,
            0, 0, 0, -1,
            0, 0, Near, 0
        );
        return projection * view;
    }

    /// <summary>
    /// The held item's view and projection: from the eye at the origin, so that what it shows is
    /// placed relative to the eye in world axes, with a field of view of its own, as a game's
    /// hand keeps however wide the view is set; depth reversed as in <see cref="ViewProjection"/>.
    /// </summary>
    public mat4 HandViewProjection(double fieldOfView)
    {
        mat4 view = mat4.LookAt(vec3.Zero, (vec3)Forward, vec3.UnitZ);
        float f = 1f / MathF.Tan((float)fieldOfView / 2f);
        mat4 projection = new
        (
            f / (float)Aspect, 0, 0, 0,
            0, -f, 0, 0,
            0, 0, 0, -1,
            0, 0, HandNear, 0
        );
        return projection * view;
    }
}


/// <summary>A dropped item to draw: what it is and where its middle is.</summary>
internal readonly record struct DrawnItem(ItemType Type, dvec3 Position, dquat Orientation);


/// <summary>
/// What the player holds, drawn over the world at the lower right of the view: an item, or for
/// <see cref="ItemType.None"/> the bare arm. <paramref name="Model"/> places its model (a unit
/// cube, or an image a unit across) relative to the eye, in world axes.
/// </summary>
internal readonly record struct ViewModel(ItemType Type, mat4 Model);


/// <summary>The four sides of a camera's view and the plane through the eye, for telling what cannot be in view.</summary>
internal readonly struct Frustum
{
    public Frustum(Camera camera)
    {
        _eye = camera.Position;
        _forward = camera.Forward;
        _right = dvec3.Cross(camera.Forward, dvec3.UnitZ).Normalized;
        _up = dvec3.Cross(_right, camera.Forward);
        _vertical = Math.Tan(camera.FieldOfView / 2.0);
        _horizontal = _vertical * camera.Aspect;
        _verticalSlant = Math.Sqrt(1.0 + _vertical * _vertical);
        _horizontalSlant = Math.Sqrt(1.0 + _horizontal * _horizontal);
    }

    private readonly dvec3 _eye;
    private readonly dvec3 _forward;
    private readonly dvec3 _right;
    private readonly dvec3 _up;
    // Tangents of the half angles of the view, and the lengths of the sides' unnormalized normals.
    private readonly double _vertical;
    private readonly double _horizontal;
    private readonly double _verticalSlant;
    private readonly double _horizontalSlant;

    /// <summary>Whether any of a sphere can be in view: in front of the eye and within the four sides.</summary>
    public bool CanSee(dvec3 center, double radius)
    {
        dvec3 offset = center - _eye;
        double ahead = dvec3.Dot(offset, _forward);
        if (ahead < -radius)
            return false;

        double up = Math.Abs(dvec3.Dot(offset, _up));
        double across = Math.Abs(dvec3.Dot(offset, _right));
        return up - _vertical * ahead <= radius * _verticalSlant && across - _horizontal * ahead <= radius * _horizontalSlant;
    }
}


/// <summary>
/// Draws the voxel bodies, particles, lines and the interface. Every chunk of every body has a mesh in
/// device memory (<see cref="VertexArena"/>), rebuilt when its blocks change and uploaded
/// through a staging buffer at the start of a frame, and, while any of its cells shows damage
/// or stress, a slot in a per-cell data buffer holding them (rewritten when those change,
/// without remeshing); the others share an empty slot. Block textures and cracks share one
/// texture array; the interface (<see cref="GuiDrawList"/>) draws from its own atlas.
/// <para>
/// The map's chunks near the eye, which the player edits, are meshed on the spot, so an edit
/// shows on the frame it is made; farther ones, which stream in by the thousand, are meshed
/// on background threads, nearest first, and only once the chunk columns around theirs are
/// loaded, so that no faces are made against ground that is not there yet. Chunks beyond the
/// view distance or outside the view are not drawn.
/// </para>
/// </summary>
internal sealed unsafe partial class Renderer : IDisposable
{
    public const int MaxSlots = 1024;
    private const int SlotCells = 4096;
    // Transparent leaves can expose all six faces of every cell.
    // Half-cell shapes can expose eight parts and clip each face into four strips.
    private const int MaxQuads = SlotCells * 6 * 8 * 4;
    private const int VertexBytes = 8;
    private const ulong PageBytes = 64UL << 20;
    // Mesh bytes uploaded per frame at most; what does not fit waits for the next.
    private const ulong StagingBytes = 8UL << 20;
    // The map's chunks this close to the eye's (in chunks, along each axis) are meshed on the spot.
    private const int ImmediateDistance = 2;
    // Map chunks meshing in the background at once.
    private const int MaxMeshing = 128;
    private const int MaxLineVertices = 4096;
    // Enough for the menus' tiled background at the smallest interface scale on a 4K screen.
    private const int MaxGuiVertices = 1 << 18;
    private const int TextureSize = BlockParticles.TexturePixels;
    private const int MipLevels = 5;
    // The held item's own vertical field of view, whatever the view's is set to.
    private const double HandFieldOfView = 70.0 * Math.PI / 180.0;
    // The colors of the bare arm's skin, lit side and shaded edge, sRGB.
    private static readonly byte[] Skin = [0xC9, 0x93, 0x70];
    private static readonly byte[] SkinShade = [0xA8, 0x74, 0x55];
    internal Daylight Daylight { get; set; } = DaylightCycle.Evaluate(0);
    private vec3 SkyColor => Daylight.Horizon;
    private vec3 SunDirection => Daylight.SunDirection;
    // Radius of a sphere around a chunk.
    private static readonly double ChunkRadius = 0.5 * Math.Sqrt(3.0) * Grid3D<Voxel>.ChunkSize * VoxelBody.CellSize;

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameData
    {
        public mat4 ViewProjection;
        public vec4 CameraPosition;
        public vec4 SunDirection;
        public vec4 SkyColor;
        public vec4 Settings;
        public mat4 HandViewProjection;
        public vec4 Lighting;
        public ivec4 LightScene;
        public ivec4 LightCounts;
        public mat4 SunViewProjection;
        public mat4 InverseViewProjection;
        public vec4 SkyZenith;
        public vec4 AmbientSky;
        public vec4 SunColor;
        public vec4 Atmosphere;
        public vec4 StarRotation;
        public vec4 MoonDirection;
        public vec4 Explosion0, Explosion1, Explosion2, Explosion3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DrawData
    {
        public mat4 Model;
        public ivec4 Chunk;
        public vec4 ClipMin, ClipMax;
    }

    private readonly record struct VisibleDraw(DrawData Data, int Page, uint IndexCount, int VertexOffset, double DistanceSquared = 0);
    private readonly List<VisibleDraw> _visibleDraws = [];
    private readonly List<VisibleDraw> _waterDraws = [];
    private readonly HostBuffer[] _drawData = new HostBuffer[GraphicsContext.FramesInFlight];
    private readonly HostBuffer[] _drawCommands = new HostBuffer[GraphicsContext.FramesInFlight];
    private readonly int[] _drawCapacity = new int[GraphicsContext.FramesInFlight];
    internal bool IndirectDraws { get; set; } = Environment.GetEnvironmentVariable("MYT_INDIRECT_DRAWS") != "0";
    public int VoxelDrawCalls { get; private set; }

    private sealed class ChunkRecord
    {
        // The mesh drawn, none until the first arrives.
        public ArenaRange Vertices;
        public IncrementalVoxelMesh? Mesh;
        public uint[] UploadedVertices = [];
        public bool ImmediateQueued;
        public VoxelBody? Owner;
        public ivec3 Coordinate;
        public bool MeshInFlight, PendingEdit, AsyncQueued, WaitingForColumns;
        public LinkedListNode<GpuMeshWork>? GpuMeshWork;
        public long QueueTicket, PendingSince, UploadVersion, DisplayedVersion;
        public long PendingEmptyVersion, EmptyQueuedAt;
        public (ChunkRecord Record, long Version)[] EmptyDependencies = [];
        public uint QuadCount;
        public bool HasWater;
        public bool HasMesh;
        // Its cells' data; 0, the slot left empty, while none shows damage or stress.
        public int Slot;
        // The latest mesh asked for: meshes made for an earlier one are out of date.
        public long Version;
        public bool Live = true;
    }

    /// <summary>A mesh waiting to be uploaded, for the version of its chunk it was made for.</summary>
    private readonly record struct Upload(ChunkRecord Record, long Version, uint[] Vertices, long QueuedAt = 0);

    /// <summary>A map chunk to mesh in the background, as a copy of its cells with a border of one.</summary>
    private readonly record struct MeshJob(int BodyId, ivec3 Chunk, long Version, Voxel[] Cells, bool TransparentLeaves, bool Incremental, ChunkRecord? Record = null, IncrementalVoxelMesh? State = null, (ChunkRecord Record, long Version)[]? Dependencies = null, bool Singleton = false, long QueuedAt = 0, bool Gpu = false);

    private readonly record struct MeshResult(int BodyId, ivec3 Chunk, long Version, uint[] Vertices, Voxel[] Cells, IncrementalVoxelMesh? Mesh, ChunkRecord? Record, (ChunkRecord Record, long Version)[]? Dependencies, long QueuedAt, uint[]? GpuCells = null, bool TransparentLeaves = false, bool GpuShapes = false, TerrainLod.Mesh? TerrainMesh = null, TerrainLod? TerrainOwner = null);

    private readonly record struct ImmediateMesh(VoxelBody Body, Dictionary<ivec3, ChunkRecord> Records, ivec3 Chunk, ChunkRecord Record);
    private readonly List<ImmediateMesh> _immediateMeshes = [];
    private uint[][] _immediateResults = [];
    private readonly ParallelOptions _meshingOptions = new() { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 1, 3) };
    internal bool SynchronousMeshing { get; set; } = Environment.GetEnvironmentVariable("MYT_SYNC_MESH") == "1";
    internal bool ParallelMeshing { get; set; } = true;
    internal bool IncrementalMeshing { get; set; } = Environment.GetEnvironmentVariable("MYT_INCREMENTAL_MESH") != "0";

    /// <param name="atlas">The interface's atlas, uploaded once: every image the interface draws must be in it already.</param>
    /// <param name="sprites">The images of the items that are not blocks, which their models are cut from.</param>
    public Renderer(GraphicsContext graphics, string assetDirectory, GuiAtlas atlas, ItemSprites sprites)
    {
        _graphics = graphics;
        GeometryParticles = graphics.SupportsGeometryShader && Environment.GetEnvironmentVariable("MYT_PARTICLE_GEOMETRY") != "0";
        _vk = graphics.Vk;
        _device = graphics.Device;
        _shaderLoader = new ShaderLoader(_vk, _device);
        _arena = new VertexArena(graphics, PageBytes);

        _lighting = new ComputeLighting(graphics,Path.Combine(assetDirectory,"shaders"))
        { BudgetedDiffusion = Environment.GetEnvironmentVariable("MYT_LIGHT_BUDGET") != "0",
            DeferredDaylight = Environment.GetEnvironmentVariable("MYT_DAYLIGHT_DEFER") != "0" };
        LoadTextures(Path.Combine(assetDirectory, "images"), sprites);
        UploadAtlas(atlas);
        CreateSunShadows();
        CreateDescriptors();
        CreatePipelines(Path.Combine(assetDirectory, "shaders"));
        _itemVertices = BuildItemModels(sprites);
        CreateGpuMesher(Path.Combine(assetDirectory, "shaders"));

        _quadIndices = graphics.CreateHostBuffer(MaxQuads * 6 * sizeof(uint), BufferUsageFlags.IndexBufferBit);
        Span<uint> indices = _quadIndices.As<uint>();
        for (uint q = 0; q < MaxQuads; q++)
        {
            indices[(int)q * 6] = q * 4;
            indices[(int)q * 6 + 1] = q * 4 + 1;
            indices[(int)q * 6 + 2] = q * 4 + 2;
            indices[(int)q * 6 + 3] = q * 4;
            indices[(int)q * 6 + 4] = q * 4 + 2;
            indices[(int)q * 6 + 5] = q * 4 + 3;
        }

        for (int i = MaxSlots - 1; i >= 1; i--)
            _freeSlots.Push(i);

        int[,] layers = FaceLayers;
        int threads = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
        _meshers = new BackgroundWorkers<MeshJob, MeshResult>
        (
            "Chunk meshing", threads,
            job =>
            {
                if (job.Gpu)
                    return new MeshResult(job.BodyId, job.Chunk, job.Version, [], job.Cells, null, job.Record,
                        job.Dependencies, job.QueuedAt, GpuMeshInputs.Pack(job.Cells), job.TransparentLeaves,
                        job.Cells.Any(v => BlockShapes.HasShape(v.Type)));
                IncrementalVoxelMesh? mesh = job.Incremental && !job.Singleton ? job.State ?? new() : null;
                uint[] vertices = job.Singleton ? VoxelMesher.BuildIsolatedRenderMesh(job.Cells, layers, job.TransparentLeaves)
                    : mesh != null ? mesh.UpdateRenderMesh(job.Cells, layers, job.TransparentLeaves)
                    : VoxelMesher.BuildRenderMesh(job.Cells, layers, job.TransparentLeaves);
                return new MeshResult(job.BodyId, job.Chunk, job.Version, vertices, job.Cells, mesh, job.Record, job.Dependencies, job.QueuedAt);
            }
        );
    }

    private readonly GraphicsContext _graphics;
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly ShaderLoader _shaderLoader;
    private readonly VertexArena _arena;
    private readonly BackgroundWorkers<MeshJob, MeshResult> _meshers;
    private readonly Dictionary<string, int> _layers = [];
    private Image _textureImage;
    private DeviceMemory _textureMemory;
    private ImageView _textureView;
    private Sampler _sampler;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _descriptorPool;
    private readonly DescriptorSet[] _sets = new DescriptorSet[GraphicsContext.FramesInFlight];
    private PipelineLayout _pipelineLayout;
    private Pipeline _voxelPipeline;
    private Pipeline _waterPipeline;
    private Pipeline _underwaterPipeline;
    private Pipeline _waterOverlayPipeline;
    private Pipeline _skyPipeline;
    private Pipeline _linePipeline;
    private Pipeline _particlePipeline;
    private Pipeline _particleFallbackPipeline;
    private Pipeline _itemPipeline;
    private Pipeline _waterItemPipeline;
    private Pipeline _guiPipeline;
    private Image _atlasImage;
    private DeviceMemory _atlasMemory;
    private ImageView _atlasView;
    private Sampler _atlasSampler;
    private DescriptorSetLayout _guiSetLayout;
    private DescriptorPool _guiPool;
    private DescriptorSet _guiSet;
    private PipelineLayout _guiLayout;
    private readonly HostBuffer _quadIndices;
    // Every item's model, and the arm's under ItemType.None: where each starts and how many vertices.
    private readonly HostBuffer _itemVertices;
    private readonly Dictionary<ItemType, (int First, int Count)> _itemModels = [];
    // Texture layers of the items' images, and of the arm's skin.
    private readonly Dictionary<ItemType, int> _spriteLayers = [];
    private int _skinLayer;
    private int _dustFirstLayer, _explosionFirstLayer;
    private readonly ComputeLighting _lighting;
    // The lighting allocations each frame slot's set was written for (see ComputeLighting.Allocations).
    private readonly int[] _boundLighting = [-1, -1];
    private readonly HostBuffer[] _frameData = new HostBuffer[GraphicsContext.FramesInFlight];
    private readonly HostBuffer[] _voxelData = new HostBuffer[GraphicsContext.FramesInFlight];
    private readonly HostBuffer[] _staging = new HostBuffer[GraphicsContext.FramesInFlight];
    private readonly HostBuffer[] _lineVertices = new HostBuffer[GraphicsContext.FramesInFlight];
    private readonly HostBuffer[] _particleInstances = new HostBuffer[GraphicsContext.FramesInFlight];
    private readonly HostBuffer[] _guiVertices = new HostBuffer[GraphicsContext.FramesInFlight];
    private readonly uint[] _voxelMirror = new uint[MaxSlots * SlotCells];
    private readonly uint[] _slotScratch = new uint[SlotCells];
    private readonly HashSet<int>[] _dirtySlots = [.. Enumerable.Range(0, (int)GraphicsContext.FramesInFlight).Select(_ => new HashSet<int>())];
    private readonly Stack<int> _freeSlots = new();
    private readonly Dictionary<int, Dictionary<ivec3, ChunkRecord>> _chunks = [];
    private readonly List<(ArenaRange Range, ulong Frame)> _graveyard = [];
    // Meshes of chunks near the eye go first, so an edit shows at once.
    private readonly List<Upload> _urgentUploads = [];
    private readonly List<Upload> _uploads = [];
    private readonly List<List<BufferCopy>> _uploadCopies = [];
    // Map chunks waiting for the columns around theirs to load, and to be meshed in the background.
    private readonly HashSet<ivec3> _waiting = [];
    private readonly List<ivec3> _waitingScratch = [];
    private readonly HashSet<ivec3> _backlog = [];
    private readonly Stack<Voxel[]> _cellCopies = new();
    private readonly PriorityQueue<ivec3, int> _nearest = new();
    private int _seenColumnVersion = -1;
    private long _nextVersion;
    private ulong _frameNumber;

    /// <summary>Texture layer per block type and face, for <see cref="VoxelMesher.BuildRenderMesh(Voxel[], int[,])"/>.</summary>
    public int[,] FaceLayers { get; private set; } = new int[0, 0];

    public int FirstCrackLayer { get; private set; }

    public GameSettings Video { get; set; } = new();
    private bool TransparentLeaves;


    /// <summary>Full voxel detail distance in metres; terrain LOD continues beyond it when configured.</summary>
    public double ViewDistance { get; set; } = 200.0;

    /// <summary>Map chunks waiting to be meshed or meshing.</summary>
    public int ChunksMeshing => _backlog.Count + _meshers.Pending + _asyncQueued + GpuMeshesPending;
    internal int ChunksWaitingForColumns => _waiting.Count + _asyncWaiting.Count;

    /// <summary>Chunks drawn in the last frame.</summary>
    public int ChunksDrawn { get; private set; }

    /// <summary>Device memory the meshes take, bytes.</summary>
    public ulong MeshBytes => _arena.UsedBytes;
    public long QuadsDrawn { get; private set; }
    // Test-only visibility output: 1 = sun, 2 = first point light.
    internal int ShadowProbe { get; set; }

    // ---------------------------------------------------------------------------------------
    // Keeping the GPU copy of the world current
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Remeshes chunks whose blocks changed, takes the meshes finished in the background, and
    /// rewrites the data of chunks whose damage or stress changed.
    /// </summary>
    /// <param name="eye">Where the view is from: chunks near it mesh at once, and the rest nearest first.</param>
    public void Sync(World world, dvec3 eye)
    {
        ivec3 eyeChunk = Grid3D<Voxel>.ChunkOf(new ivec3((int)Math.Floor(eye.x), (int)Math.Floor(eye.y), (int)Math.Floor(eye.z)));
        if (TransparentLeaves != Video.TransparentLeaves)
        {
            TransparentLeaves = Video.TransparentLeaves;
            _lighting.Clear();
            foreach (VoxelBody body in world.Bodies)
                foreach (var chunk in body.Grid.Chunks) body.MeshDirty.Add(chunk.Coordinate);
        }
        CaptureTerrain(world);
        _asyncEye = eye;
        TakeMeshes(world.Map);

        foreach (VoxelBody body in world.Bodies)
        {
            if (!_chunks.TryGetValue(body.Id, out Dictionary<ivec3, ChunkRecord>? records))
                _chunks[body.Id] = records = [];

            foreach (ivec3 chunk in body.MeshDirty)
            {
                _lighting.Invalidate(body, chunk, TransparentLeaves);
                Remesh(body, records, chunk, eyeChunk);
            }
            body.MeshDirty.Clear();

            foreach (ivec3 chunk in body.DataDirty)
            {
                if (records.TryGetValue(chunk, out ChunkRecord? record))
                    WriteData(body, chunk, record);
            }

            body.DataDirty.Clear();
        }

        if (SynchronousMeshing)
        {
            ReleaseWaiting(world.Map, eyeChunk);
            PostMeshing(world.Map, eyeChunk);
            BuildImmediateMeshes();
        }
        else
        {
            ImmediateMeshesBuilt = 0;
            PostAsyncMeshes(world.Map);
        }
    }

    private void BuildImmediateMeshes()
    {
        int count = _immediateMeshes.Count;
        ImmediateMeshesBuilt = count;
        if (count == 0) return;
        if (_immediateResults.Length < count)
            Array.Resize(ref _immediateResults, Math.Max(count, _immediateResults.Length * 2));
        // Sync owns the world during this batch. Workers only read grids and immutable
        // texture tables; accepting results, lighting and GPU allocations stay on the caller.
        if (ParallelMeshing && count >= 8 && _meshingOptions.MaxDegreeOfParallelism > 1)
            Parallel.For(0, count, _meshingOptions, BuildImmediateMesh);
        else
            for (int i = 0; i < count; i++) BuildImmediateMesh(i);
        for (int i = 0; i < count; i++)
        {
            ImmediateMesh mesh = _immediateMeshes[i];
            Accept(mesh.Body, mesh.Records, mesh.Chunk, mesh.Record, _immediateResults[i], urgent: true);
            mesh.Record.ImmediateQueued = false;
        }
        _immediateMeshes.Clear();
        Array.Clear(_immediateResults, 0, count);
    }

    internal int ImmediateMeshesBuilt { get; private set; }
    private long _meshCoverageGeneration;

    private void BuildImmediateMesh(int index)
    {
        ImmediateMesh mesh = _immediateMeshes[index];
        // Explosion debris often has one voxel; its six-face fast path needs no
        // persistent chunk-sized face table or padded snapshot.
        if (IncrementalMeshing && mesh.Body.Grid.Count != 1)
            _immediateResults[index] = (mesh.Record.Mesh ??= new IncrementalVoxelMesh()).UpdateRenderMesh(mesh.Body.Grid, mesh.Chunk, FaceLayers, TransparentLeaves);
        else
        {
            mesh.Record.Mesh = null;
            _immediateResults[index] = VoxelMesher.BuildRenderMesh(mesh.Body.Grid, mesh.Chunk, FaceLayers, TransparentLeaves);
        }
    }

    /// <summary>Lets go of everything drawn for a body; its meshes are freed once no frame in flight uses them.</summary>
    internal void WaitForLighting(IEnumerable<VoxelBody> bodies, Func<VoxelBody,(dvec3 Position,dquat Orientation)> poseOf)
    {
        _lighting.Configure(Daylight.Ambient * (Video.AmbientBrightness / 100f) * 1.85f, Video.BlockLighting, Video.Sun, SunDirection, Daylight.SunColor, Daylight.SunIntensity, force: true);
        _lighting.Build(bodies,poseOf,Video.TransparentLeaves);
        _lightingWarmup = true;
    }
    private bool _lightingWarmup;
    public string LightingStatistics => $"light GPU: {_lighting.CellCount:N0} loaded cells, {_lighting.GridBytes/(1<<20)} MB, metadata {_lighting.MetadataBytes/(1<<20)} MB, upload {_lighting.UploadedBytes/1024} KB, diffuse {_lighting.DiffuseMilliseconds:F1} ms ({_lighting.ActiveTiles}/{_lighting.TileBudget} tiles), {_lighting.PointCount} points {_lighting.ShadowMilliseconds:F1} ms";
    internal LightChunkState[] CaptureLighting() => _lighting.Capture();
    internal void RestoreLighting(IEnumerable<LightChunkState> chunks) => _lighting.Restore(chunks);

    public void RemoveBody(VoxelBody body)
    {
        if (!_chunks.Remove(body.Id, out Dictionary<ivec3, ChunkRecord>? records))
            return;

        foreach (ChunkRecord record in records.Values)
            Free(record);
    }

    /// <summary>Forgets every body, for a new world.</summary>
    public void Clear()
    {
        foreach (Dictionary<ivec3, ChunkRecord> records in _chunks.Values)
        {
            foreach (ChunkRecord record in records.Values)
                Free(record);
        }

        ClearTerrain();
        _lighting.Clear();
        _chunks.Clear();
        _waiting.Clear();
        _backlog.Clear();
        _urgentUploads.Clear();
        _uploads.Clear();
        _gpuMeshEdits.Clear(); _gpuMeshChunks.Clear(); _gpuMeshTerrain.Clear();
        _seenColumnVersion = -1;
        _asyncNearest.Clear(); _asyncOldest.Clear(); _asyncWaiting.Clear(); _asyncEmpty.Clear();
        _asyncQueued = 0; _asyncColumns = -1;
        MaxMeshEditLatencyMilliseconds = 0;
        _meshCamera = null;
    }

    /// <summary>
    /// A chunk whose blocks changed: gone, it goes; on the map, it waits for the columns
    /// around it, and away from the eye it waits to be meshed in the background; otherwise
    /// it is meshed now.
    /// </summary>
    private void Remesh(VoxelBody body, Dictionary<ivec3, ChunkRecord> records, ivec3 chunk, ivec3 eyeChunk)
    {
        if (!SynchronousMeshing)
        {
            InvalidateAsyncMesh(body, records, chunk);
            return;
        }
        if (!body.Grid.TryGetChunk(chunk, out _))
        {
            if (records.Remove(chunk, out ChunkRecord? gone))
                Free(gone);
            if (body is VoxelMap)
            {
                _waiting.Remove(chunk);
                _backlog.Remove(chunk);
            }

            return;
        }

        if (records.TryGetValue(chunk, out ChunkRecord? dirtyRecord))
            dirtyRecord.Version = ++_nextVersion;

        if (body is VoxelMap map)
        {
            if (!NeighboursLoaded(map, chunk))
            {
                _waiting.Add(chunk);
                return;
            }

            // Dirty chunks can become ready before ReleaseWaiting runs in the same
            // Sync. Remove them now so that it cannot schedule a second mutable job.
            _waiting.Remove(chunk);

            ivec3 away = chunk - eyeChunk;
            if ((!records.TryGetValue(chunk, out ChunkRecord? existing) || existing.Mesh == null)
                && Math.Max(Math.Abs(away.x), Math.Max(Math.Abs(away.y), Math.Abs(away.z))) > ImmediateDistance)
            {
                _backlog.Add(chunk);
                return;
            }

            _backlog.Remove(chunk);
        }

        if (!records.TryGetValue(chunk, out ChunkRecord? record))
            records[chunk] = record = new ChunkRecord();

        // Persistent mesh state has one owner. Keep this guard even when another
        // scheduling path asks for the chunk again during the current batch.
        if (record.ImmediateQueued) return;
        record.ImmediateQueued = true;
        record.Version = ++_nextVersion;
        _immediateMeshes.Add(new(body, records, chunk, record));
    }

    /// <summary>Whether the map's chunk columns around a chunk's are all loaded, which meshing it needs.</summary>
    private static bool NeighboursLoaded(VoxelMap map, ivec3 chunk)
    {
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            if (!map.IsLoaded(new ivec2(chunk.x + dx, chunk.y + dy)))
                return false;
        }

        return true;
    }

    /// <summary>Once columns have come or gone, sends on the waiting chunks whose neighbours are now loaded, and drops those unloaded.</summary>
    private void ReleaseWaiting(VoxelMap map, ivec3 eyeChunk)
    {
        if (map.ColumnVersion == _seenColumnVersion || _waiting.Count == 0)
            return;

        _seenColumnVersion = map.ColumnVersion;
        if (!_chunks.TryGetValue(map.Id, out Dictionary<ivec3, ChunkRecord>? records))
            return;

        _waitingScratch.Clear();
        _waitingScratch.AddRange(_waiting);
        foreach (ivec3 chunk in _waitingScratch)
        {
            if (!map.IsLoaded(VoxelMap.ColumnOfChunk(chunk)))
            {
                _waiting.Remove(chunk);
            }
            else if (NeighboursLoaded(map, chunk))
            {
                _waiting.Remove(chunk);
                Remesh(map, records, chunk, eyeChunk);
            }
        }
    }

    /// <summary>Hands the background the nearest map chunks waiting, as many as it has room for.</summary>
    private void PostMeshing(VoxelMap map, ivec3 eyeChunk)
    {
        int room = MaxMeshing - _meshers.Pending;
        if (room <= 0 || _backlog.Count == 0)
            return;

        // The farthest of those kept so far comes out first, leaving the nearest.
        _nearest.Clear();
        foreach (ivec3 chunk in _backlog)
        {
            ivec3 away = chunk - eyeChunk;
            int distance = away.x * away.x + away.y * away.y + away.z * away.z;
            if (_nearest.Count < room)
                _nearest.Enqueue(chunk, -distance);
            else
                _nearest.EnqueueDequeue(chunk, -distance);
        }

        Dictionary<ivec3, ChunkRecord> records = _chunks[map.Id];
        while (_nearest.TryDequeue(out ivec3 chunk, out _))
        {
            _backlog.Remove(chunk);
            if (!map.Grid.TryGetChunk(chunk, out _))
                continue;

            if (!records.TryGetValue(chunk, out ChunkRecord? record))
                records[chunk] = record = new ChunkRecord();

            record.Version = ++_nextVersion;
            Voxel[] cells = _cellCopies.Count > 0 ? _cellCopies.Pop() : new Voxel[VoxelMesher.PaddedVolume];
            VoxelMesher.CopyPadded(map.Grid, chunk, cells);
            _meshers.Post(new MeshJob(map.Id, chunk, record.Version, cells, TransparentLeaves, IncrementalMeshing));
        }
    }

    /// <summary>Takes the meshes the background finished, those still the latest asked for.</summary>
    private void TakeMeshes(VoxelMap map)
    {
        while (_meshers.TryTake(out MeshResult result))
        {
            _cellCopies.Push(result.Cells);
            if (result.Record != null)
            {
                TakeAsyncMesh(result);
                continue;
            }
            if (result.BodyId != map.Id || !_chunks.TryGetValue(map.Id, out Dictionary<ivec3, ChunkRecord>? records))
                continue;
            if (!records.TryGetValue(result.Chunk, out ChunkRecord? record) || record.Version != result.Version)
                continue;

            record.Mesh = result.Mesh;
            Accept(map, records, result.Chunk, record, result.Vertices, urgent: false);
        }
    }

    /// <summary>A chunk's new mesh: none means the chunk shows nothing and goes; otherwise it waits to be uploaded.</summary>
    private void Accept(VoxelBody body, Dictionary<ivec3, ChunkRecord> records, ivec3 chunk, ChunkRecord record, uint[] vertices, bool urgent, long? version = null, long queuedAt = 0)
    {
        record.UploadVersion = version ?? record.Version;
        if (vertices.Length == 0)
        {
            record.DisplayedVersion = record.UploadVersion;
            Bury(record.Vertices);
            record.Vertices = default;
            record.QuadCount = 0;
            record.UploadedVertices = [];
            record.HasWater = false;
            if (!record.HasMesh) _meshCoverageGeneration++;
            record.HasMesh = true;
            ReleaseSlot(record);
            return;
        }

        (urgent ? _urgentUploads : _uploads).Add(new Upload(record, record.UploadVersion, vertices, queuedAt));
        WriteData(body, chunk, record);
    }

    /// <summary>
    /// Damage (0-255) and stress over strength (× 64) of each cell of a chunk, into its slot;
    /// a chunk with neither anywhere gives its slot back and uses the empty one.
    /// </summary>
    private void WriteData(VoxelBody body, ivec3 chunk, ChunkRecord record)
    {
        Span<uint> data = _slotScratch;
        data.Clear();
        bool shown = false;
        if (body.Grid.TryGetChunk(chunk, out Grid3DChunk<Voxel> cells))
        {
            ReadOnlySpan<Voxel> voxels = cells.Cells;
            int remaining = cells.Count;
            for (int i = 0; i < SlotCells; i++)
            {
                Voxel voxel = voxels[i];
                if (voxel.Type == BlockType.Air)
                    continue;

                uint damage = voxel.Damage == 0 ? 0u : (uint)Math.Clamp(voxel.Wear * 255.0, 1.0, 255.0);
                double ratio = body.StressRatio(Grid3D<Voxel>.CellOf(chunk, i), voxel);
                uint stress = (uint)Math.Clamp(ratio * 64.0, 0.0, 255.0);
                data[i] = damage | stress << 8;
                shown |= data[i] != 0;
                if (--remaining == 0)
                    break;
            }
        }

        if (!shown)
        {
            ReleaseSlot(record);
            return;
        }

        if (record.Slot == 0)
        {
            if (_freeSlots.Count == 0)
                return;

            record.Slot = _freeSlots.Pop();
        }

        Span<uint> mirror = _voxelMirror.AsSpan(record.Slot * SlotCells, SlotCells);
        if (data.SequenceEqual(mirror))
            return;
        data.CopyTo(mirror);
        foreach (HashSet<int> dirty in _dirtySlots)
            dirty.Add(record.Slot);
    }

    private void ReleaseSlot(ChunkRecord record)
    {
        if (record.Slot > 0)
            _freeSlots.Push(record.Slot);
        record.Slot = 0;
    }

    private void Free(ChunkRecord record)
    {
        record.Live = false;
        if (record.GpuMeshWork is { List: not null } queued) queued.List.Remove(queued);
        record.GpuMeshWork = null;
        if (record.AsyncQueued) { record.AsyncQueued = false; _asyncQueued--; }
        _asyncWaiting.Remove(record); _asyncEmpty.Remove(record);
        _meshCoverageGeneration++;
        Bury(record.Vertices);
        record.Vertices = default;
        record.QuadCount = 0;
        record.Mesh = null;
        record.UploadedVertices = [];
        ReleaseSlot(record);
    }

    /// <summary>Gives a mesh's memory back once every frame that may still draw from it has finished.</summary>
    private void Bury(ArenaRange range)
    {
        if (!range.IsEmpty)
            _graveyard.Add((range, _frameNumber));
    }

    // ---------------------------------------------------------------------------------------
    // Drawing
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Uploads the meshes waiting, then begins rendering and draws the bodies' chunks in view,
    /// each body where <paramref name="poseOf"/> puts it, then the lines and the interface.
    /// </summary>
    public void Draw
    (
        Frame frame,
        Camera camera,
        IEnumerable<VoxelBody> bodies,
        Func<VoxelBody, (dvec3 Position, dquat Orientation)> poseOf,
        BlockParticles particles,
        IReadOnlyList<DrawnItem> items,
        ViewModel? held,
        ReadOnlySpan<LineVertex> lines,
        ReadOnlySpan<GuiVertex> gui,
        bool showStress
    )
    {
        _meshCamera = camera;
        _frameNumber++;
        int kept = 0;
        for (int i = 0; i < _graveyard.Count; i++)
        {
            var grave = _graveyard[i];
            if (grave.Frame + GraphicsContext.FramesInFlight + 1 > _frameNumber)
                _graveyard[kept++] = grave;
            else
                _arena.Free(grave.Range);
        }
        _graveyard.RemoveRange(kept, _graveyard.Count - kept);

        uint f = frame.FrameIndex;
        CommandBuffer cmd = frame.Commands;
        MeshEditLatencyMilliseconds = 0;
        CompleteGpuMeshes(f);
        for (uint previous = 0; previous < GraphicsContext.FramesInFlight; previous++)
            if (previous != f && _graphics.IsFrameComplete(previous)) CompleteGpuMeshes(previous);
        _lighting.Configure(Daylight.Ambient * (Video.AmbientBrightness / 100f) * 1.85f, Video.BlockLighting, Video.Sun, SunDirection, Daylight.SunColor, Daylight.SunIntensity);
        long lightingStart = Prof.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        _lighting.Build(bodies, poseOf, Video.TransparentLeaves);
        _lighting.FocusPosition = (vec3)camera.Position;
        if (Prof.On) Prof.Add("lighting.prepare", System.Diagnostics.Stopwatch.GetElapsedTime(lightingStart).TotalMilliseconds);
        _lighting.Prepare(f);
        BindComputeLighting(f);
        _graphics.BeginRenderTiming(frame);
        RecordUploads(cmd, f);
        RecordGpuMeshes(cmd, f);
        PrepareTerrain(cmd, f, camera, bodies);

        bool underwater = false;
        foreach (VoxelBody body in bodies)
            if (body is VoxelMap map && map.Grid[(ivec3)dvec3.Floor(camera.Position)].Type == BlockType.Water)
            {
                underwater = true;
                break;
            }
        // Snap to water's apparent magnification on entry, and restore the original
        // projection immediately on exit. Frustum culling and raymarch rays use this too.
        const double waterRefraction = 1.333;
        if (underwater)
            camera = camera with { FieldOfView = 2 * Math.Atan(Math.Tan(camera.FieldOfView / 2) / waterRefraction) };

        float fogEnd = _terrainLod == null ? (float)ViewDistance : 1e20f;
        float fogStart = _terrainLod == null ? fogEnd - Math.Min(0.5f * fogEnd, 128f) : fogEnd * 0.5f;
        Span<vec4> flashes = stackalloc vec4[4];
        particles.ReadFlashes(flashes);
        FrameData data = new()
        {
            ViewProjection = camera.ViewProjection(),
            CameraPosition = new vec4((vec3)camera.Position, (float)camera.Aspect),
            SunDirection = new vec4(SunDirection, 0),
            SkyColor = new vec4(SkyColor, 1),
            Settings = new vec4(fogStart, fogEnd, showStress ? 1 : 0, FirstCrackLayer),
            HandViewProjection = camera.HandViewProjection(underwater
                ? 2 * Math.Atan(Math.Tan(HandFieldOfView / 2) / waterRefraction) : HandFieldOfView),
            Lighting = new vec4(Video.AmbientOcclusion ? 1 : 0, Video.Sun ? Daylight.SunIntensity : 0,
                Video.SpecularLighting ? 1 : 0, Video.AmbientBrightness / 100f),
            SunViewProjection = SunShadowProjection(camera.Position),
            // Invert at the origin to keep sky directions independent of world-position roundoff.
            InverseViewProjection = (camera with { Position = dvec3.Zero }).ViewProjection().Inverse,
            SkyZenith = new vec4(Daylight.Zenith, 0),
            AmbientSky = new vec4(Daylight.Ambient, 0),
            SunColor = new vec4(Daylight.SunColor, 0),
            Atmosphere = new vec4(Daylight.Twilight, Daylight.Stars, Daylight.Warmth, _layers["water"]),
            StarRotation = Daylight.StarRotation,
            MoonDirection = new vec4(Daylight.MoonDirection, 0),
            Explosion0 = flashes[0], Explosion1 = flashes[1], Explosion2 = flashes[2], Explosion3 = flashes[3],
            LightScene = new ivec4(ShadowProbe,underwater ? 1 : 0,(int)BlockType.Water * 2,_lighting.Ready ? 1 : 0),
            LightCounts = new ivec4(Video.BlockLighting ? 1 : 0,
                Video.DynamicShadows && Daylight.SunIntensity > 0 ? 1 : 0, _layers["oak_leaves"],
                _layers[Video.TransparentLeaves ? "oak_leaves_transparent" : "oak_leaves"])
        };
        _frameData[f].As<FrameData>()[0] = data;

        Span<uint> voxelData = _voxelData[f].As<uint>();
        foreach (int slot in _dirtySlots[f])
            _voxelMirror.AsSpan(slot * SlotCells, SlotCells).CopyTo(voxelData[(slot * SlotCells)..]);
        _dirtySlots[f].Clear();

        _visibleDraws.Clear();
        _sunDraws.Clear();
        _waterDraws.Clear();
        Frustum frustum = new(camera);
        double reach = ViewDistance + ChunkRadius;
        int drawn = 0;
        long quads = 0;
        double half = 0.5 * Grid3D<Voxel>.ChunkSize * VoxelBody.CellSize;
        foreach (VoxelBody body in bodies)
        {
            if (!_chunks.TryGetValue(body.Id, out Dictionary<ivec3, ChunkRecord>? records))
                continue;

            (dvec3 position, dquat orientation) = poseOf(body);
            bool bodyVisible = true;
            if (body is VoxelObject obj)
            {
                double radius = obj.Bounds.Radius;
                dvec3 tightCenter = position+orientation*obj.Bounds.Center;
                double distance = ViewDistance+radius;
                bodyVisible = (tightCenter-camera.Position).LengthSqr <= distance*distance && frustum.CanSee(tightCenter,radius);
            }
            mat4 model = ModelMatrix(position, orientation);
            foreach ((ivec3 chunk, ChunkRecord record) in records)
            {
                if (record.QuadCount == 0)
                    continue;

                DrawData drawData = new() { Model = model, Chunk = new ivec4(chunk * Grid3D<Voxel>.ChunkSize, record.Slot) };
                VisibleDraw draw = new(drawData, record.Vertices.Page, record.QuadCount * 6, (int)(record.Vertices.Offset / VertexBytes));
                dvec3 center = position + orientation * (new dvec3(chunk) * (2.0 * half) + half);
                draw = draw with { DistanceSquared = (center - camera.Position).LengthSqr };
                bool fullDetail = body is not VoxelMap || _terrainLod == null || (TerrainLod.UsesFullDetail(chunk, camera.Position, ViewDistance) && !_terrainCover.CoversDetail(chunk));
                if (fullDetail && Video.Sun && Video.DynamicShadows && Daylight.SunIntensity > 0)
                {
                    vec3 projected = (data.SunViewProjection*new vec4((vec3)center,1)).xyz;
                    float margin = (float)(2*ChunkRadius/(Math.Min(ViewDistance,128)+16));
                    if (Math.Abs(projected.x) <= 1+margin && Math.Abs(projected.y) <= 1+margin) _sunDraws.Add(draw);
                }
                if (!bodyVisible) continue;
                dvec3 distanceOffset = center - camera.Position;
                if (!fullDetail) continue;
                if (distanceOffset.LengthSqr > reach * reach || !frustum.CanSee(center, ChunkRadius))
                    continue;

                _visibleDraws.Add(draw);
                if (record.HasWater) _waterDraws.Add(draw);
                drawn++;
                quads += record.QuadCount;
            }
        }

        AppendTerrainDraws(camera);
        // Solid geometry fills depth first. Blend water back to front without depth writes.
        _waterDraws.Sort(static (a, b) => b.DistanceSquared.CompareTo(a.DistanceSquared));
        PrepareVoxelDraws(f);
        _lighting.Record(frame,_lightingWarmup ? 128 : 2);
        _lightingWarmup = false;
        RecordSunShadow(frame);
        _graphics.BeginRendering(frame, new ClearColorValue(SkyColor.x, SkyColor.y, SkyColor.z, 1f), startTiming:false);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _skyPipeline);
        DescriptorSet skySet = _sets[f];
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &skySet, 0, null);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);
        RecordVoxelDraws(cmd, f, _visibleDraws, _sunDraws.Count, _voxelPipeline);
        ChunksDrawn = drawn;
        QuadsDrawn = quads;
        DrawItems(cmd, camera, frustum, items);
        RecordVoxelDraws(cmd, f, _waterDraws, _sunDraws.Count + _visibleDraws.Count, _waterPipeline);
        ReadOnlySpan<ParticleInstance> chips = particles.BuildInstances(camera, FaceLayers, ViewDistance, _dustFirstLayer, _explosionFirstLayer);
        DrawParticles(cmd, f, chips);
        DrawVertices(cmd, _linePipeline, _lineVertices[f], lines, Math.Min(lines.Length, MaxLineVertices), default);
        // Integrate the eye's water volume once, independently of boundary geometry.
        // This also covers sealed pools and views whose exit is beyond the draw distance.
        if (underwater && ShadowProbe == 0)
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _underwaterPipeline);
            _vk.CmdDraw(cmd, 3, 1, 0, 0);
        }
        if (held is ViewModel shown)
            DrawHeld(cmd, shown);

        // The water texture covers the world and the hand; interface text stays clear.
        if (underwater && ShadowProbe == 0)
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _waterOverlayPipeline);
            _vk.CmdDraw(cmd, 3, 1, 0, 0);
        }
        DrawGui(cmd, f, gui);
    }

    /// <summary>Submit consecutive arena-page draws together without reordering surfaces.</summary>
    private void PrepareVoxelDraws(uint frame)
    {
        int count = _sunDraws.Count+_visibleDraws.Count+_waterDraws.Count;
        EnsureDrawCapacity(frame,Math.Max(1,count));
        Span<DrawData> data = _drawData[frame].As<DrawData>();
        bool indirect = IndirectDraws && _graphics.SupportsMultiDrawIndirect;
        Span<DrawIndexedIndirectCommand> commands = indirect ? _drawCommands[frame].As<DrawIndexedIndirectCommand>() : default;
        for (int i = 0; i < count; i++)
        {
            VisibleDraw draw = i < _sunDraws.Count ? _sunDraws[i]
                : i < _sunDraws.Count + _visibleDraws.Count ? _visibleDraws[i-_sunDraws.Count]
                : _waterDraws[i-_sunDraws.Count-_visibleDraws.Count];
            data[i] = draw.Data;
            if (indirect) commands[i] = new DrawIndexedIndirectCommand(draw.IndexCount,1,0,draw.VertexOffset,(uint)i);
        }

    }
    private void RecordVoxelDraws(CommandBuffer cmd,uint frame,List<VisibleDraw> draws,int baseDraw,Pipeline pipeline)
    {
        int count = draws.Count;
        DescriptorSet set = _sets[frame];
        _vk.CmdBindDescriptorSets(cmd,PipelineBindPoint.Graphics,_pipelineLayout,0,1,&set,0,null);
        _vk.CmdBindPipeline(cmd,PipelineBindPoint.Graphics,pipeline);
        _vk.CmdBindIndexBuffer(cmd,_quadIndices.Buffer,0,IndexType.Uint32);
        bool voxel = pipeline.Handle == _voxelPipeline.Handle || pipeline.Handle == _waterPipeline.Handle;
        if (pipeline.Handle == _voxelPipeline.Handle) VoxelDrawCalls = 0;
        for (int first = 0; first < count;)
        {
            int pageIndex = draws[first].Page;
            Buffer page = _arena.BufferOf(pageIndex); ulong offset = 0;
            _vk.CmdBindVertexBuffers(cmd,0,1,&page,&offset);
            int end = first+1;
            while (end < count && draws[end].Page == pageIndex) end++;
            if (IndirectDraws && _graphics.SupportsMultiDrawIndirect)
            {
                while (first < end)
                {
                    uint batch = (uint)Math.Min(end-first,_graphics.MaxIndirectDrawCount);
                    _vk.CmdDrawIndexedIndirect(cmd,_drawCommands[frame].Buffer,(ulong)(baseDraw+first)*(ulong)sizeof(DrawIndexedIndirectCommand),batch,(uint)sizeof(DrawIndexedIndirectCommand));
                    first += (int)batch;
                    if (voxel) VoxelDrawCalls++;
                }
            }
            else for (; first < end; first++)
            {
                VisibleDraw draw = draws[first];
                _vk.CmdDrawIndexed(cmd,draw.IndexCount,1,0,draw.VertexOffset,(uint)(baseDraw+first));
                if (voxel) VoxelDrawCalls++;
            }
        }
    }

    private void EnsureDrawCapacity(uint frame, int required)
    {
        if (_drawCapacity[frame] >= required) return;
        // BeginFrame has waited for this frame slot's fence; its buffers and descriptor
        // set are no longer in use. Other in-flight slots retain their own resources.
        int capacity = Math.Max(required, Math.Max(256, _drawCapacity[frame] * 2));
        if (_drawCapacity[frame] > 0)
        {
            _graphics.Destroy(_drawData[frame]);
            _graphics.Destroy(_drawCommands[frame]);
        }
        _drawData[frame] = _graphics.CreateHostBuffer((ulong)capacity * (ulong)sizeof(DrawData), BufferUsageFlags.StorageBufferBit);
        _drawCommands[frame] = _graphics.CreateHostBuffer((ulong)capacity * (ulong)sizeof(DrawIndexedIndirectCommand), BufferUsageFlags.IndirectBufferBit);
        _drawCapacity[frame] = capacity;
        DescriptorBufferInfo buffer = new() { Buffer = _drawData[frame].Buffer, Range = Vk.WholeSize };
        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet, DstSet = _sets[frame], DstBinding = 3,
            DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &buffer
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
    }

    /// <summary>The dropped items in view, each its model scaled to its size where it lies.</summary>
    private void DrawItems(CommandBuffer cmd, Camera camera, Frustum frustum, IReadOnlyList<DrawnItem> items)
    {
        if (items.Count == 0)
            return;

        BindItems(cmd);
        for (int i = 0; i < items.Count; i++)
        {
            DrawnItem item = items[i];
            double size = Items.Get(item.Type).IsBlock ? Items.BlockEdge : Items.SpriteEdge;
            if ((item.Position - camera.Position).LengthSqr > ViewDistance * ViewDistance || !frustum.CanSee(item.Position, size))
                continue;

            DrawItem(cmd, item.Type, ModelMatrix(item.Position, item.Orientation) * mat4.Scale((float)size), held: false);
        }
    }

    /// <summary>The held item over everything drawn so far: the depth is cleared first, so it never goes into a wall.</summary>
    private void DrawHeld(CommandBuffer cmd, ViewModel held)
    {
        ClearAttachment depth = new()
        {
            AspectMask = ImageAspectFlags.DepthBit,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0))
        };
        ClearRect whole = new(new Rect2D(new Offset2D(0, 0), _graphics.Extent), 0, 1);
        _vk.CmdClearAttachments(cmd, 1, &depth, 1, &whole);

        BindItems(cmd);
        DrawItem(cmd, held.Type, held.Model, held: true);
    }

    private void BindItems(CommandBuffer cmd)
    {
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _itemPipeline);
        Buffer handle = _itemVertices.Buffer;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(cmd, 0, 1, &handle, &offset);
    }

    private void DrawItem(CommandBuffer cmd, ItemType type, mat4 model, bool held)
    {
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics,
            type == Items.Of(BlockType.Water) ? _waterItemPipeline : _itemPipeline);
        (int first, int count) = _itemModels[type];
        DrawData push = new() { Model = model, Chunk = new ivec4(held ? 1 : 0, 0, 0, 0) };
        _vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(DrawData), &push);
        _vk.CmdDraw(cmd, (uint)count, 1, (uint)first, 0);
    }

    /// <summary>Draws only the interface, over black, as the menus are drawn while no world is.</summary>
    public void DrawInterface(Frame frame, ReadOnlySpan<GuiVertex> gui)
    {
        _frameNumber++;
        _graphics.BeginRendering(frame, new ClearColorValue(0f, 0f, 0f, 1f));
        DrawGui(frame.Commands, frame.FrameIndex, gui);
    }

    /// <summary>The interface's quads, as many whole ones as the frame's buffer holds, in one draw.</summary>
    private void DrawGui(CommandBuffer cmd, uint f, ReadOnlySpan<GuiVertex> vertices)
    {
        int count = Math.Min(vertices.Length, MaxGuiVertices) / 6 * 6;
        if (count == 0)
            return;

        vertices[..count].CopyTo(_guiVertices[f].As<GuiVertex>());
        vec2 screen = new(_graphics.Extent.Width, _graphics.Extent.Height);
        DescriptorSet set = _guiSet;
        Buffer handle = _guiVertices[f].Buffer;
        ulong offset = 0;
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _guiPipeline);
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _guiLayout, 0, 1, &set, 0, null);
        _vk.CmdPushConstants(cmd, _guiLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(vec2), &screen);
        _vk.CmdBindVertexBuffers(cmd, 0, 1, &handle, &offset);
        _vk.CmdDraw(cmd, (uint)count, 1, 0, 0);
    }

    /// <summary>
    /// Copies the meshes waiting into device memory, the urgent first, as many as the frame's
    /// staging buffer holds, and swaps them in for the chunks' old ones, which go once no frame
    /// in flight draws them. Outside rendering, which transfers cannot be in.
    /// </summary>
    private void RecordUploads(CommandBuffer cmd, uint f)
    {
        HostBuffer staging = _staging[f];
        Span<byte> bytes = staging.As<byte>();
        ulong used = 0;
        int committed = 0;
        long uploadStart = System.Diagnostics.Stopwatch.GetTimestamp();
        MeshBytesUploaded = 0;
        foreach (List<BufferCopy> copies in _uploadCopies) copies.Clear();
        for (int priority = 0; priority < 2; priority++)
        {
            List<Upload> queue = priority == 0 ? _urgentUploads : _uploads;
            int done = 0;
            for (; done < queue.Count; done++)
            {
                if (!SynchronousMeshing && committed > 0 &&
                    System.Diagnostics.Stopwatch.GetElapsedTime(uploadStart).TotalMilliseconds >= 1) break;
                Upload upload = queue[done];
                if (!upload.Record.Live || upload.Record.UploadVersion != upload.Version)
                    continue;

                uint[] vertices = upload.Vertices;
                ChunkRecord record = upload.Record;
                if (ReferenceEquals(vertices, record.UploadedVertices))
                {
                    record.DisplayedVersion = upload.Version;
                    continue;
                }
                ulong size = (ulong)vertices.Length * sizeof(uint);
                bool replace = record.Vertices.IsEmpty || record.Vertices.Size < size;
                uint[] previous = record.UploadedVertices;
                // Count changed quads against what is actually on the GPU, not the
                // previous CPU edit: queued/stale updates may have been skipped.
                ulong transferBytes = 0;
                for (int q = 0; q < vertices.Length; q += 8)
                    if (replace || q + 8 > previous.Length ||
                        !vertices.AsSpan(q, 8).SequenceEqual(previous.AsSpan(q, 8)))
                        transferBytes += 8 * sizeof(uint);
                if (used + transferBytes > StagingBytes || (!SynchronousMeshing && used > 0
                    && used + transferBytes > MeshUploadBudgetBytes)) break;

                ArenaRange range = record.Vertices;
                if (replace)
                {
                    // Spare capacity avoids allocation churn as an explosion exposes faces.
                    range = _arena.Allocate(Math.Max(4096UL, size + size / 2));
                    Bury(record.Vertices);
                    record.Vertices = range;
                }
                while (_uploadCopies.Count <= range.Page) _uploadCopies.Add([]);
                int run = -1;
                for (int q = 0; q <= vertices.Length; q += 8)
                {
                    bool different = q < vertices.Length && (replace || q + 8 > previous.Length ||
                        !vertices.AsSpan(q, 8).SequenceEqual(previous.AsSpan(q, 8)));
                    if (different && run < 0) run = q;
                    if (different || run < 0) continue;
                    ulong length = (ulong)(q - run) * sizeof(uint);
                    MemoryMarshal.AsBytes(vertices.AsSpan(run, q - run)).CopyTo(bytes[(int)used..]);
                    _uploadCopies[range.Page].Add(new BufferCopy(used, range.Offset + (ulong)run * sizeof(uint), length));
                    used += length;
                    run = -1;
                }
                committed++;
                if (record.HasMesh) RecordMeshLatency(upload.QueuedAt);
                record.UploadedVertices = vertices;
                record.DisplayedVersion = upload.Version;
                if (!upload.Record.HasMesh) _meshCoverageGeneration++;
                upload.Record.HasMesh = true;
                upload.Record.QuadCount = (uint)(upload.Vertices.Length * sizeof(uint) / (4 * VertexBytes));
                upload.Record.HasWater = false;
                for (int vertex = 1; vertex < upload.Vertices.Length; vertex += 8)
                    if ((upload.Vertices[vertex] & 255u) == (uint)_layers["water"])
                    {
                        upload.Record.HasWater = true;
                        break;
                    }
            }

            queue.RemoveRange(0, done);
        }

        MeshBytesUploaded = used;
        CommitEmptyMeshes();
        if (used == 0)
            return;

        // Reused ranges may still be read by an earlier frame on this queue. Finish
        // those vertex reads before overwriting, without waiting on the CPU.
        MemoryBarrier before = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.VertexAttributeReadBit | AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.TransferWriteBit
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.VertexInputBit | PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            0, 1, &before, 0, null, 0, null);
        // Coalesce adjacent changed faces into transfers, grouped by arena page.
        for (int page = 0; page < _uploadCopies.Count; page++)
        {
            List<BufferCopy> copies = _uploadCopies[page];
            if (copies.Count == 0) continue;
            fixed (BufferCopy* regions = CollectionsMarshal.AsSpan(copies))
                _vk.CmdCopyBuffer(cmd, staging.Buffer, _arena.BufferOf(page), (uint)copies.Count, regions);
        }

        MemoryBarrier barrier = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.VertexAttributeReadBit
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.VertexInputBit, 0, 1, &barrier, 0, null, 0, null);
    }

    internal bool GeometryParticles { get; set; }
    public int ParticleDrawCalls { get; private set; }
    public int ParticlesDrawn { get; private set; }

    private void DrawParticles(CommandBuffer cmd, uint frame, ReadOnlySpan<ParticleInstance> particles)
    {
        ParticleDrawCalls = 0;
        ParticlesDrawn = particles.Length;
        if (particles.IsEmpty) return;
        HostBuffer buffer = _particleInstances[frame];
        particles.CopyTo(buffer.As<ParticleInstance>());
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, GeometryParticles ? _particlePipeline : _particleFallbackPipeline);
        Buffer handle = buffer.Buffer;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(cmd, 0, 1, &handle, &offset);
        if (GeometryParticles) _vk.CmdDraw(cmd, (uint)particles.Length, 1, 0, 0);
        else _vk.CmdDraw(cmd, 4, (uint)particles.Length, 0, 0);
        ParticleDrawCalls = 1;
    }

    private void DrawVertices<T>(CommandBuffer cmd, Pipeline pipeline, HostBuffer buffer, ReadOnlySpan<T> vertices, int count, DrawData push) where T : unmanaged
    {
        if (count == 0)
            return;

        vertices[..count].CopyTo(buffer.As<T>());
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
        _vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(DrawData), &push);
        Buffer handle = buffer.Buffer;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(cmd, 0, 1, &handle, &offset);
        _vk.CmdDraw(cmd, (uint)count, 1, 0, 0);
    }

    private static mat4 ModelMatrix(dvec3 p, dquat orientation)
    {
        dmat3 r = orientation.ToMat3;
        return new mat4
        (
            (float)r.m00, (float)r.m01, (float)r.m02, 0f,
            (float)r.m10, (float)r.m11, (float)r.m12, 0f,
            (float)r.m20, (float)r.m21, (float)r.m22, 0f,
            (float)p.x, (float)p.y, (float)p.z, 1f
        );
    }

    // ---------------------------------------------------------------------------------------
    // Textures
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Every block texture, the six crack stages, the items' images and the arm's skin, 16×16
    /// each, as layers of one sRGB texture array with a full mip chain.
    /// </summary>
    private void LoadTextures(string imageDirectory, ItemSprites sprites)
    {
        List<byte[]> layers = [];

        int Add(byte[] pixels)
        {
            layers.Add(pixels);
            return layers.Count - 1;
        }

        foreach (string name in Blocks.TextureNames())
            _layers[name] = Add(LoadImage(Path.Combine(imageDirectory, name + ".png")));

        _layers["oak_leaves_transparent"] = Add(LeafCutout(LoadImage(Path.Combine(imageDirectory, "oak_leaves_transparent.png"))));

        FirstCrackLayer = layers.Count;
        for (int stage = 0; stage < 6; stage++)
            Add(LoadImage(Path.Combine(imageDirectory, $"crack{stage}.png")));

        FaceLayers = new int[Blocks.Count, 7];
        for (int type = 1; type < Blocks.Count; type++)
        {
            BlockMaterial material = Blocks.Get((BlockType)type);
            for (int face = 0; face < 6; face++)
                FaceLayers[type, face] = _layers[material.Texture(face)];
        }

        for (int type = 1; type < Blocks.Count; type++)
        {
            vec3 average = vec3.Zero;
            for (int face = 0; face < 6; face++)
                average += LightingScene.AverageReflectance(layers[FaceLayers[type, face]]) / 6f;
            _lighting.SetReflectance((BlockType)type, 0, average);
        }
        _lighting.SetReflectance(BlockType.Leaves, 1,
            LightingScene.AverageReflectance(layers[_layers["oak_leaves_transparent"]]));

        FaceLayers[(int)BlockType.Furnace, 6] = _layers["furnace_front_lit"];

        foreach (ItemType type in Items.NonBlockTypes)
            _spriteLayers[type] = Add(sprites.Pixels(type));
        _skinLayer = Add(SkinTexture());
        _dustFirstLayer = layers.Count;
        for (int frame = 0; frame < BlockParticles.DustFrames; frame++)
            Add(LoadImage(Path.Combine(imageDirectory, $"dust{frame}.png")));
        _explosionFirstLayer = layers.Count;
        for (int frame = 0; frame < BlockParticles.ExplosionFrames; frame++)
            Add(LoadImage(Path.Combine(imageDirectory, $"explosion{frame}.png")));

        UploadTextureArray(layers);
    }

    /// <summary>The bare arm's skin: one tone, a little darker along its edges.</summary>
    private static byte[] SkinTexture()
    {
        byte[] pixels = new byte[TextureSize * TextureSize * 4];
        for (int y = 0; y < TextureSize; y++)
        for (int x = 0; x < TextureSize; x++)
        {
            bool edge = x == 0 || y == 0 || x == TextureSize - 1 || y == TextureSize - 1;
            byte[] tone = edge ? SkinShade : Skin;
            int i = (y * TextureSize + x) * 4;
            pixels[i] = tone[0];
            pixels[i + 1] = tone[1];
            pixels[i + 2] = tone[2];
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    /// <summary>
    /// Every item's model in one vertex buffer: the blocks as cubes, the other items cut from
    /// their images (<see cref="ItemMesher"/>), and the arm, a cube of skin, under <see cref="ItemType.None"/>.
    /// </summary>
    private HostBuffer BuildItemModels(ItemSprites sprites)
    {
        List<ItemVertex> vertices = [];

        void Add(ItemType type, ItemVertex[] model)
        {
            _itemModels[type] = (vertices.Count, model.Length);
            vertices.AddRange(model);
        }

        for (int type = 1; type < Blocks.Count; type++)
            Add(Items.Of((BlockType)type), ItemMesher.BlockModel((BlockType)type, FaceLayers));
        foreach (ItemType type in Items.NonBlockTypes)
            Add(type, ItemMesher.SpriteModel(sprites.Opaque(type), _spriteLayers[type]));

        int[,] skin = new int[1, 6];
        for (int face = 0; face < 6; face++)
            skin[0, face] = _skinLayer;
        Add(ItemType.None, ItemMesher.BlockModel(BlockType.Air, skin));

        HostBuffer buffer = _graphics.CreateHostBuffer((ulong)(vertices.Count * sizeof(ItemVertex)), BufferUsageFlags.VertexBufferBit);
        CollectionsMarshal.AsSpan(vertices).CopyTo(buffer.As<ItemVertex>());
        return buffer;
    }

    private static byte[] LoadImage(string path)
    {
        using FileStream stream = File.OpenRead(path);
        ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        if (image.Width != TextureSize || image.Height != TextureSize)
            throw new InvalidDataException($"{path} is {image.Width}×{image.Height}; block textures must be {TextureSize}×{TextureSize}");

        return image.Data;
    }

    /// <summary>The leaf artwork uses neutral gray as its cutout mask; other textures retain their authored alpha.</summary>
    internal static byte[] LeafCutout(byte[] pixels)
    {
        byte[] cutout = (byte[])pixels.Clone();
        for (int i = 0; i < cutout.Length; i += 4)
            if (cutout[i] == cutout[i+1] && cutout[i+1] == cutout[i+2]) cutout[i+3] = 0;
        return cutout;
    }

    /// <summary>Halves a layer: alpha-weighted average of each 2×2 block, so transparent pixels do not darken edges.</summary>
    private static byte[] Downsample(byte[] pixels, int size)
    {
        int half = size / 2;
        byte[] result = new byte[half * half * 4];
        for (int y = 0; y < half; y++)
        for (int x = 0; x < half; x++)
        {
            double r = 0, g = 0, b = 0, a = 0;
            for (int k = 0; k < 4; k++)
            {
                int source = ((2 * y + (k >> 1)) * size + 2 * x + (k & 1)) * 4;
                double alpha = pixels[source + 3] / 255.0;
                r += pixels[source] * alpha;
                g += pixels[source + 1] * alpha;
                b += pixels[source + 2] * alpha;
                a += alpha;
            }

            int target = (y * half + x) * 4;
            double weight = a > 0 ? 1.0 / a : 0.0;
            result[target] = (byte)Math.Round(r * weight);
            result[target + 1] = (byte)Math.Round(g * weight);
            result[target + 2] = (byte)Math.Round(b * weight);
            result[target + 3] = (byte)Math.Round(a / 4.0 * 255.0);
        }

        return result;
    }

    private void UploadTextureArray(List<byte[]> layers)
    {
        uint layerCount = (uint)layers.Count;
        ImageCreateInfo info = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(TextureSize, TextureSize, 1),
            MipLevels = MipLevels,
            ArrayLayers = layerCount,
            Format = Format.R8G8B8A8Srgb,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            Samples = SampleCountFlags.Count1Bit
        };
        _vk.CreateImage(_device, &info, null, out _textureImage);
        _vk.GetImageMemoryRequirements(_device, _textureImage, out MemoryRequirements requirements);
        MemoryAllocateInfo allocation = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _graphics.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };
        _vk.AllocateMemory(_device, &allocation, null, out _textureMemory);
        _vk.BindImageMemory(_device, _textureImage, _textureMemory, 0);

        // Level by level, every layer of a level after the other.
        List<byte> staged = [];
        List<BufferImageCopy> regions = [];
        List<byte[]> level = layers;
        for (int mip = 0, size = TextureSize; mip < MipLevels; mip++, size /= 2)
        {
            regions.Add(new BufferImageCopy
            {
                BufferOffset = (ulong)staged.Count,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, (uint)mip, 0, layerCount),
                ImageExtent = new Extent3D((uint)size, (uint)size, 1)
            });

            foreach (byte[] layer in level)
                staged.AddRange(layer);

            int current = size;
            level = [.. level.Select(l => Downsample(l, current))];
        }

        HostBuffer staging = _graphics.CreateHostBuffer((ulong)staged.Count, BufferUsageFlags.TransferSrcBit);
        CollectionsMarshal.AsSpan(staged).CopyTo(staging.As<byte>());

        BufferImageCopy[] copies = [.. regions];
        _graphics.Submit(cmd =>
        {
            _graphics.ImageBarrier
            (
                cmd, _textureImage, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                PipelineStageFlags.TopOfPipeBit, AccessFlags.None, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit,
                layerCount, MipLevels
            );
            fixed (BufferImageCopy* pointer = copies)
                _vk.CmdCopyBufferToImage(cmd, staging.Buffer, _textureImage, ImageLayout.TransferDstOptimal, (uint)copies.Length, pointer);
            _graphics.ImageBarrier
            (
                cmd, _textureImage, ImageAspectFlags.ColorBit, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, PipelineStageFlags.FragmentShaderBit, AccessFlags.ShaderReadBit,
                layerCount, MipLevels
            );
        });
        _graphics.Destroy(staging);

        _textureView = _graphics.CreateView(_textureImage, Format.R8G8B8A8Srgb, ImageAspectFlags.ColorBit, ImageViewType.Type2DArray, layerCount, MipLevels);

        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = MipLevels
        };
        _vk.CreateSampler(_device, &samplerInfo, null, out _sampler);
    }

    /// <summary>The interface's atlas as an sRGB texture without mipmaps, sampled nearest so its pixel art stays sharp.</summary>
    private void UploadAtlas(GuiAtlas atlas)
    {
        ImageCreateInfo info = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D((uint)atlas.Width, (uint)atlas.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = Format.R8G8B8A8Srgb,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            Samples = SampleCountFlags.Count1Bit
        };
        _vk.CreateImage(_device, &info, null, out _atlasImage);
        _vk.GetImageMemoryRequirements(_device, _atlasImage, out MemoryRequirements requirements);
        MemoryAllocateInfo allocation = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _graphics.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };
        _vk.AllocateMemory(_device, &allocation, null, out _atlasMemory);
        _vk.BindImageMemory(_device, _atlasImage, _atlasMemory, 0);

        HostBuffer staging = _graphics.CreateHostBuffer((ulong)atlas.Pixels.Length, BufferUsageFlags.TransferSrcBit);
        atlas.Pixels.CopyTo(staging.As<byte>());
        Extent3D extent = info.Extent;
        _graphics.Submit(cmd =>
        {
            BufferImageCopy copy = new()
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = extent
            };
            _graphics.ImageBarrier
            (
                cmd, _atlasImage, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                PipelineStageFlags.TopOfPipeBit, AccessFlags.None, PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit
            );
            _vk.CmdCopyBufferToImage(cmd, staging.Buffer, _atlasImage, ImageLayout.TransferDstOptimal, 1, &copy);
            _graphics.ImageBarrier
            (
                cmd, _atlasImage, ImageAspectFlags.ColorBit, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit, PipelineStageFlags.FragmentShaderBit, AccessFlags.ShaderReadBit
            );
        });
        _graphics.Destroy(staging);

        _atlasView = _graphics.CreateView(_atlasImage, Format.R8G8B8A8Srgb, ImageAspectFlags.ColorBit);
        SamplerCreateInfo samplerInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge
        };
        _vk.CreateSampler(_device, &samplerInfo, null, out _atlasSampler);
    }

    // ---------------------------------------------------------------------------------------
    // Pipelines
    // ---------------------------------------------------------------------------------------

    private void BindComputeLighting(uint frame)
    {
        // This slot's fence has passed, so its set may change. The lighting replaces its buffers
        // rather than resizing them, so the same allocation count means the same buffers.
        if (_boundLighting[frame] == _lighting.Allocations)
            return;
        _boundLighting[frame] = _lighting.Allocations;
        DescriptorBufferInfo* buffers = stackalloc DescriptorBufferInfo[3];
        buffers[0] = new() { Buffer = _lighting.LightBuffer, Range = _lighting.GridBytes/2 };
        buffers[1] = new() { Buffer = _lighting.PointBuffer, Range = Vk.WholeSize };
        buffers[2] = new() { Buffer = _lighting.MetadataBuffer, Range = Vk.WholeSize };
        WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[3];
        for (int i = 0; i < 3; i++) writes[i] = new()
        {
            SType = StructureType.WriteDescriptorSet, DstSet = _sets[frame], DstBinding = i == 2 ? 4u : (uint)(6+i),
            DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = buffers+i
        };
        _vk.UpdateDescriptorSets(_device,3,writes,0,null);
    }

    private void CreateDescriptors()
    {
        DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | (_graphics.SupportsGeometryShader ? ShaderStageFlags.GeometryBit : 0) },
            new() { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 2, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 3, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit },
            new() { Binding = 4, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 5, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 6, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 7, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }
        };
        DescriptorSetLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 8,
            PBindings = bindings
        };
        _vk.CreateDescriptorSetLayout(_device, &layoutInfo, null, out _setLayout);

        DescriptorPoolSize* sizes = stackalloc DescriptorPoolSize[]
        {
            new() { Type = DescriptorType.UniformBuffer, DescriptorCount = GraphicsContext.FramesInFlight },
            new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = GraphicsContext.FramesInFlight * 2 },
            new() { Type = DescriptorType.StorageBuffer, DescriptorCount = GraphicsContext.FramesInFlight * 5 }
        };
        DescriptorPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = GraphicsContext.FramesInFlight,
            PoolSizeCount = 3,
            PPoolSizes = sizes
        };
        _vk.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool);

        for (int f = 0; f < GraphicsContext.FramesInFlight; f++)
        {
            _frameData[f] = _graphics.CreateHostBuffer((ulong)sizeof(FrameData), BufferUsageFlags.UniformBufferBit);
            _voxelData[f] = _graphics.CreateHostBuffer(MaxSlots * SlotCells * sizeof(uint), BufferUsageFlags.StorageBufferBit);
            _voxelData[f].As<uint>().Clear();
            _staging[f] = _graphics.CreateHostBuffer(StagingBytes, BufferUsageFlags.TransferSrcBit);
            _lineVertices[f] = _graphics.CreateHostBuffer(MaxLineVertices * (ulong)sizeof(LineVertex), BufferUsageFlags.VertexBufferBit);
            _particleInstances[f] = _graphics.CreateHostBuffer(BlockParticles.MaxParticles * (ulong)sizeof(ParticleInstance), BufferUsageFlags.VertexBufferBit);
            _guiVertices[f] = _graphics.CreateHostBuffer(MaxGuiVertices * (ulong)sizeof(GuiVertex), BufferUsageFlags.VertexBufferBit);

            DescriptorSetLayout layout = _setLayout;
            DescriptorSetAllocateInfo allocation = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout
            };
            _vk.AllocateDescriptorSets(_device, &allocation, out _sets[f]);

            DescriptorBufferInfo frameInfo = new() { Buffer = _frameData[f].Buffer, Range = Vk.WholeSize };
            DescriptorImageInfo imageInfo = new() { Sampler = _sampler, ImageView = _textureView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
            DescriptorBufferInfo voxelInfo = new() { Buffer = _voxelData[f].Buffer, Range = Vk.WholeSize };
            WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[]
            {
                new() { SType = StructureType.WriteDescriptorSet, DstSet = _sets[f], DstBinding = 0, DescriptorCount = 1, DescriptorType = DescriptorType.UniformBuffer, PBufferInfo = &frameInfo },
                new() { SType = StructureType.WriteDescriptorSet, DstSet = _sets[f], DstBinding = 1, DescriptorCount = 1, DescriptorType = DescriptorType.CombinedImageSampler, PImageInfo = &imageInfo },
                new() { SType = StructureType.WriteDescriptorSet, DstSet = _sets[f], DstBinding = 2, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &voxelInfo }
            };
            _vk.UpdateDescriptorSets(_device, 3, writes, 0, null);
            BindSunShadow((uint)f);
        }

        // The interface's own set: just its atlas, the same for every frame.
        DescriptorSetLayoutBinding atlasBinding = new()
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit
        };
        DescriptorSetLayoutCreateInfo guiLayoutInfo = new()
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &atlasBinding
        };
        _vk.CreateDescriptorSetLayout(_device, &guiLayoutInfo, null, out _guiSetLayout);

        DescriptorPoolSize atlasSize = new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        DescriptorPoolCreateInfo guiPoolInfo = new()
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &atlasSize
        };
        _vk.CreateDescriptorPool(_device, &guiPoolInfo, null, out _guiPool);

        DescriptorSetLayout guiSetLayout = _guiSetLayout;
        DescriptorSetAllocateInfo guiAllocation = new()
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _guiPool,
            DescriptorSetCount = 1,
            PSetLayouts = &guiSetLayout
        };
        _vk.AllocateDescriptorSets(_device, &guiAllocation, out _guiSet);

        DescriptorImageInfo atlasInfo = new() { Sampler = _atlasSampler, ImageView = _atlasView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        WriteDescriptorSet atlasWrite = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _guiSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &atlasInfo
        };
        _vk.UpdateDescriptorSets(_device, 1, &atlasWrite, 0, null);
    }

    /// <param name="shaderDirectory">assets/shaders: the world's shaders are in voxel/, the interface's in gui/.</param>
    private void CreatePipelines(string shaderDirectory)
    {
        DescriptorSetLayout setLayout = _setLayout;
        PushConstantRange push = new()
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Size = (uint)sizeof(DrawData)
        };
        PipelineLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &push
        };
        _vk.CreatePipelineLayout(_device, &layoutInfo, null, out _pipelineLayout);

        DescriptorSetLayout guiSetLayout = _guiSetLayout;
        PushConstantRange screen = new() { StageFlags = ShaderStageFlags.VertexBit, Size = (uint)sizeof(vec2) };
        PipelineLayoutCreateInfo guiLayoutInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &guiSetLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &screen
        };
        _vk.CreatePipelineLayout(_device, &guiLayoutInfo, null, out _guiLayout);

        ShaderModule Load(string name, bool vertex, string directory = "voxel") => vertex
            ? _shaderLoader.LoadVertex(Path.Combine(shaderDirectory, directory, name))
            : _shaderLoader.LoadFragment(Path.Combine(shaderDirectory, directory, name));

        _skyPipeline = CreatePipeline
        (
            Load("sky.vert", true), Load("sky.frag", false), 0, [],
            PrimitiveTopology.TriangleList, CullModeFlags.None, depth: false, blend: false
        );
        _voxelPipeline = CreatePipeline
        (
            Load("voxel.vert", true), Load("voxel.frag", false), 8,
            [(Format.R32G32Uint, 0)],
            PrimitiveTopology.TriangleList, CullModeFlags.BackBit, depth: true, blend: false
        );
        _waterPipeline = CreatePipeline
        (
            Load("voxel.vert", true), Load("voxel.frag", false), 8,
            [(Format.R32G32Uint, 0)],
            PrimitiveTopology.TriangleList, CullModeFlags.None, depth: true, blend: true, waterPass: true
        );
        _underwaterPipeline = CreatePipeline
        (
            Load("sky.vert", true), Load("underwater.frag", false), 0, [],
            PrimitiveTopology.TriangleList, CullModeFlags.None, depth: false, blend: true
        );
        _waterOverlayPipeline = CreatePipeline
        (
            Load("water_overlay.vert", true), Load("water_overlay.frag", false), 0, [],
            PrimitiveTopology.TriangleList, CullModeFlags.None, depth: false, blend: true
        );
        _sunPipeline = CreatePipeline
        (
            Load("sun_shadow.vert",true),Load("sun_shadow.frag",false),8,
            [(Format.R32G32Uint,0)],PrimitiveTopology.TriangleList,CullModeFlags.None,
            depth:true,blend:false,shadow:true
        );
        _linePipeline = CreatePipeline
        (
            Load("lines.vert", true), Load("lines.frag", false), (uint)sizeof(LineVertex),
            [(Format.R32G32B32Sfloat, 0), (Format.R32G32B32A32Sfloat, 12)],
            PrimitiveTopology.LineList, CullModeFlags.None, depth: true, blend: true
        );
        _guiPipeline = CreatePipeline
        (
            Load("gui.vert", true, "gui"), Load("gui.frag", false, "gui"), (uint)sizeof(GuiVertex),
            [(Format.R32G32Sfloat, 0), (Format.R32G32Sfloat, 8), (Format.R8G8B8A8Unorm, 16)],
            PrimitiveTopology.TriangleList, CullModeFlags.None, depth: false, blend: true, _guiLayout
        );
        _particleFallbackPipeline = CreatePipeline
        (
            Load("particles.vert", true), Load("particles.frag", false), (uint)sizeof(ParticleInstance),
            [(Format.R32G32B32A32Sfloat, 0), (Format.R32G32B32A32Sfloat, 16), (Format.R32Sfloat, 32)],
            PrimitiveTopology.TriangleStrip, CullModeFlags.None, depth: true, blend: false, instanced: true
        );
        if (_graphics.SupportsGeometryShader)
            _particlePipeline = CreatePipeline
            (
                Load("particle_points.vert", true), Load("particles.frag", false), (uint)sizeof(ParticleInstance),
                [(Format.R32G32B32A32Sfloat, 0), (Format.R32G32B32A32Sfloat, 16), (Format.R32Sfloat, 32)],
                PrimitiveTopology.PointList, CullModeFlags.None, depth: true, blend: false,
                geometryShader: _shaderLoader.LoadGeometry(Path.Combine(shaderDirectory, "voxel", "particles.geom"))
            );
        _itemPipeline = CreatePipeline
        (
            Load("item.vert", true), Load("item.frag", false), (uint)sizeof(ItemVertex),
            [(Format.R32G32B32Sfloat, 0), (Format.R32G32B32Sfloat, 12), (Format.R32G32B32Sfloat, 24)],
            PrimitiveTopology.TriangleList, CullModeFlags.BackBit, depth: true, blend: false
        );
        _waterItemPipeline = CreatePipeline
        (
            Load("item.vert", true), Load("item.frag", false), (uint)sizeof(ItemVertex),
            [(Format.R32G32B32Sfloat, 0), (Format.R32G32B32Sfloat, 12), (Format.R32G32B32Sfloat, 24)],
            PrimitiveTopology.TriangleList, CullModeFlags.BackBit, depth: true, blend: true
        );
    }

    private Pipeline CreatePipeline
    (
        ShaderModule vertexShader,
        ShaderModule fragmentShader,
        uint stride,
        (Format Format, uint Offset)[] attributes,
        PrimitiveTopology topology,
        CullModeFlags cull,
        bool depth,
        bool blend,
        PipelineLayout? layout = null,
        bool shadow = false,
        bool waterPass = false,
        ShaderModule? geometryShader = null,
        bool instanced = false
    )
    {
        nint entry = Marshal.StringToHGlobalAnsi("main");
        PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[3];
        stages[0] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertexShader, PName = (byte*)entry };
        stages[1] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragmentShader, PName = (byte*)entry };
        if (geometryShader.HasValue)
            stages[2] = new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.GeometryBit, Module = geometryShader.Value, PName = (byte*)entry };
        uint waterPassValue = waterPass ? 1u : 0u;
        SpecializationMapEntry waterPassEntry = new() { ConstantID = 0, Offset = 0, Size = sizeof(uint) };
        SpecializationInfo waterSpecialization = new()
        {
            MapEntryCount = 1, PMapEntries = &waterPassEntry,
            DataSize = sizeof(uint), PData = &waterPassValue
        };
        if (waterPass) stages[1].PSpecializationInfo = &waterSpecialization;

        VertexInputBindingDescription binding = new() { Binding = 0, Stride = stride, InputRate = instanced ? VertexInputRate.Instance : VertexInputRate.Vertex };
        VertexInputAttributeDescription* attributeDescriptions = stackalloc VertexInputAttributeDescription[attributes.Length];
        for (int i = 0; i < attributes.Length; i++)
            attributeDescriptions[i] = new VertexInputAttributeDescription((uint)i, 0, attributes[i].Format, attributes[i].Offset);

        PipelineVertexInputStateCreateInfo vertexInput = new()
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = attributes.Length == 0 ? 0u : 1u,
            PVertexBindingDescriptions = &binding,
            VertexAttributeDescriptionCount = (uint)attributes.Length,
            PVertexAttributeDescriptions = attributeDescriptions
        };
        PipelineInputAssemblyStateCreateInfo inputAssembly = new() { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = topology };
        PipelineViewportStateCreateInfo viewport = new() { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        PipelineRasterizationStateCreateInfo rasterization = new()
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = cull,
            FrontFace = FrontFace.CounterClockwise,
            LineWidth = 1f
        };
        PipelineMultisampleStateCreateInfo multisample = new() { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
        PipelineDepthStencilStateCreateInfo depthStencil = new()
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = depth,
            DepthWriteEnable = depth && !blend,
            DepthCompareOp = CompareOp.GreaterOrEqual
        };
        PipelineColorBlendAttachmentState blendAttachment = new()
        {
            BlendEnable = blend,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.Zero,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
        };
        PipelineColorBlendStateCreateInfo colorBlend = new() { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = shadow ? 0u : 1u, PAttachments = shadow ? null : &blendAttachment };
        DynamicState* dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        PipelineDynamicStateCreateInfo dynamic = new() { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };

        Format colorFormat = GraphicsContext.ColorFormat;
        PipelineRenderingCreateInfo rendering = new()
        {
            SType = StructureType.PipelineRenderingCreateInfo,
            ColorAttachmentCount = shadow ? 0u : 1u,
            PColorAttachmentFormats = shadow ? null : &colorFormat,
            DepthAttachmentFormat = GraphicsContext.DepthFormat
        };

        GraphicsPipelineCreateInfo info = new()
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            PNext = &rendering,
            StageCount = geometryShader.HasValue ? 3u : 2u,
            PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewport,
            PRasterizationState = &rasterization,
            PMultisampleState = &multisample,
            PDepthStencilState = &depthStencil,
            PColorBlendState = &colorBlend,
            PDynamicState = &dynamic,
            Layout = layout ?? _pipelineLayout
        };

        if (_vk.CreateGraphicsPipelines(_device, default, 1, &info, null, out Pipeline pipeline) != Result.Success)
            throw new InvalidOperationException("Failed to create a graphics pipeline");

        Marshal.FreeHGlobal(entry);
        return pipeline;
    }

    public void Dispose()
    {
        _meshers.Dispose();
        _vk.DeviceWaitIdle(_device);
        Clear();
        _graveyard.Clear();
        _lighting.Dispose();
        DestroyGpuMesher();
        _arena.Dispose();
        DestroySunShadows();

        for (int f = 0; f < GraphicsContext.FramesInFlight; f++)
        {
            if (_drawCapacity[f] > 0)
            {
                _graphics.Destroy(_drawData[f]);
                _graphics.Destroy(_drawCommands[f]);
            }
            _graphics.Destroy(_frameData[f]);
            _graphics.Destroy(_voxelData[f]);
            _graphics.Destroy(_staging[f]);
            _graphics.Destroy(_lineVertices[f]);
            _graphics.Destroy(_particleInstances[f]);
            _graphics.Destroy(_guiVertices[f]);
        }

        _graphics.Destroy(_quadIndices);
        _graphics.Destroy(_itemVertices);
        _vk.DestroyPipeline(_device, _voxelPipeline, null);
        _vk.DestroyPipeline(_device, _waterPipeline, null);
        _vk.DestroyPipeline(_device, _underwaterPipeline, null);
        _vk.DestroyPipeline(_device, _waterOverlayPipeline, null);
        _vk.DestroyPipeline(_device, _skyPipeline, null);
        _vk.DestroyPipeline(_device, _itemPipeline, null);
        _vk.DestroyPipeline(_device, _waterItemPipeline, null);
        _vk.DestroyPipeline(_device, _linePipeline, null);
        if (_graphics.SupportsGeometryShader) _vk.DestroyPipeline(_device, _particlePipeline, null);
        _vk.DestroyPipeline(_device, _particleFallbackPipeline, null);
        _terrainLod?.Dispose();
        foreach (HostBuffer? staging in _lodStaging)
            if (staging.HasValue) _graphics.Destroy(staging.Value);
        _vk.DestroyPipeline(_device, _guiPipeline, null);
        _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        _vk.DestroyPipelineLayout(_device, _guiLayout, null);
        _vk.DestroyDescriptorPool(_device, _guiPool, null);
        _vk.DestroyDescriptorSetLayout(_device, _guiSetLayout, null);
        _vk.DestroySampler(_device, _atlasSampler, null);
        _vk.DestroyImageView(_device, _atlasView, null);
        _vk.DestroyImage(_device, _atlasImage, null);
        _vk.FreeMemory(_device, _atlasMemory, null);
        _vk.DestroyDescriptorPool(_device, _descriptorPool, null);
        _vk.DestroyDescriptorSetLayout(_device, _setLayout, null);
        _vk.DestroySampler(_device, _sampler, null);
        _vk.DestroyImageView(_device, _textureView, null);
        _vk.DestroyImage(_device, _textureImage, null);
        _vk.FreeMemory(_device, _textureMemory, null);
        _shaderLoader.Dispose();
    }
}
