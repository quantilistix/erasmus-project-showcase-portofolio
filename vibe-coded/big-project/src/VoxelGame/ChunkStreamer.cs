using GlmSharp;

using MYT.Kit.Vulkan;

namespace VoxelGame;


/// <summary>
/// Streams the map in and out around the player, so that the world goes on wherever it goes:
/// generates the chunk columns within the render distance on background threads, nearest
/// first, loads them into the world, and unloads the ones left far behind. A column that was
/// changed is kept as it was left, and comes back so; the others are simply generated again.
/// <para>
/// Columns load one ring past the render distance, so that every column drawn has its
/// neighbours to mesh against (<see cref="Renderer"/> waits for them), and unload two rings
/// further out, so that walking back and forth over a border does not load and unload the same
/// columns over and over.
/// </para>
/// </summary>
internal sealed class ChunkStreamer : IDisposable
{
    public const int MinRenderDistance = 4;
    public const int MaxRenderDistance = 32;
    public const int DefaultRenderDistance = 12;
    private const int UnloadMargin = 2;
    // Columns generating at once: enough to keep the threads busy between frames, few enough
    // that the nearest columns of where the player is now come next, not those of where it was.
    private const int MaxGenerating = 32;
    // Completion bursts must not move all ready terrain onto the render thread at once.
    private const int MaxLoadsPerUpdate = 4;

    /// <summary>Column offsets within the farthest load distance, nearest first.</summary>
    private static readonly ivec2[] Nearest = MakeNearest(MaxRenderDistance + 1);

    private static ivec2[] MakeNearest(int radius)
    {
        List<ivec2> offsets = [];
        for (int x = -radius; x <= radius; x++)
        for (int y = -radius; y <= radius; y++)
        {
            if (x * x + y * y <= radius * radius)
                offsets.Add(new ivec2(x, y));
        }

        return [.. offsets.OrderBy(o => o.x * o.x + o.y * o.y)];
    }

    /// <summary>The chunk column a point lies in.</summary>
    public static ivec2 ColumnAt(dvec3 point)
    {
        int size = Grid3D<Voxel>.ChunkSize;
        return new ivec2((int)Math.Floor(point.x / size), (int)Math.Floor(point.y / size));
    }

    private static int DistanceSquared(ivec2 a, ivec2 b)
    {
        ivec2 d = a - b;
        return d.x * d.x + d.y * d.y;
    }

    public ChunkStreamer(World world, TerrainGenerator generator, int renderDistance)
    {
        _world = world;
        Generator = generator;
        RenderDistance = renderDistance;
        // A streamed map has only the columns loaded, even before any is: one restored from a
        // save starts with none.
        world.Map.LoadedColumns ??= [];
        int threads = Math.Clamp(Environment.ProcessorCount / 4, 1, 3);
        _generators = new BackgroundWorkers<ivec2, MapColumn>("Terrain generation", threads, generator.GenerateColumn);
    }

    private readonly World _world;
    private readonly BackgroundWorkers<ivec2, MapColumn> _generators;
    private readonly HashSet<ivec2> _generating = [];
    // Columns changed and then unloaded, as they were left.
    private readonly Dictionary<ivec2, MapColumn> _kept = [];
    private readonly List<ivec2> _leaving = [];
    private bool _settled;
    private ivec2 _settledCenter;
    private int _settledDistance;
    private int _settledVersion;

    public TerrainGenerator Generator { get; }

    /// <summary>How far (chunk columns) around the player the world is loaded and drawn.</summary>
    public int RenderDistance
    {
        get;
        set => field = Math.Clamp(value, MinRenderDistance, MaxRenderDistance);
    }

    /// <summary>Columns loaded into the map.</summary>
    public int LoadedColumns => _world.Map.LoadedColumns?.Count ?? 0;

    /// <summary>Columns being generated.</summary>
    public int Generating => _generators.Pending;

    /// <summary>The changed columns kept while unloaded.</summary>
    public IEnumerable<MapColumn> KeptColumns => _kept.Values;
    public event Action<MapColumn>? ColumnRetained;

    /// <summary>
    /// Keeps a changed column, unloaded, to load as it is once the player comes near, as a
    /// save restores one.
    /// </summary>
    public void Keep(MapColumn column)
    {
        if (_world.Map.IsLoaded(column.Coordinate))
            throw new InvalidOperationException($"Column {column.Coordinate} is loaded already");

        ivec2 key = WorldTopology.Column(column.Coordinate);
        _kept[key] = column with { Coordinate = key };
        ColumnRetained?.Invoke(_kept[key]);
        _settled = false;
    }

    /// <summary>
    /// Loads the columns generated since the last call that are still wanted, unloads those
    /// left too far from <paramref name="focus"/>, and asks for the nearest ones missing. Call
    /// once a frame.
    /// </summary>
    public void Update(dvec3 focus)
    {
        _world.WrapEntities(focus);
        ivec2 center = ColumnAt(focus);
        // Once every wanted column is loaded, only a changed column, load radius or
        // player column can change this set. Pending results always bypass this cache.
        if (_settled && center == _settledCenter && RenderDistance == _settledDistance
            && _world.Map.ColumnVersion == _settledVersion)
            return;
        int load = RenderDistance + 1;
        int keep = load + UnloadMargin;

        int loads = 0;
        bool waitingForKept = false;
        for (int taken = 0; taken < MaxLoadsPerUpdate && _generators.TryTake(out MapColumn column); taken++)
        {
            _generating.Remove(column.Coordinate);
            if (DistanceSquared(column.Coordinate, center) <= keep * keep && !_world.Map.IsLoaded(column.Coordinate))
            {
                _world.WrapFurnaces(column.Coordinate);
                _world.LoadColumn(column);
                loads++;
            }
        }

        _leaving.Clear();
        if (_world.Map.LoadedColumns is HashSet<ivec2> loaded)
        {
            foreach (ivec2 column in loaded)
            {
                if (DistanceSquared(column, center) > keep * keep)
                    _leaving.Add(column);
            }
        }

        foreach (ivec2 column in _leaving)
        {
            if (_world.UnloadColumn(column) is MapColumn changed)
            {
                ivec2 key = WorldTopology.Column(column);
                _kept[key] = changed with { Coordinate = key };
                ColumnRetained?.Invoke(_kept[key]);
            }
        }

        foreach (ivec2 offset in Nearest)
        {
            if (offset.x * offset.x + offset.y * offset.y > load * load)
                break;

            ivec2 column = center + offset;
            if (_world.Map.IsLoaded(column) || _generating.Contains(column))
                continue;

            if (_kept.TryGetValue(WorldTopology.Column(column), out MapColumn? left))
            {
                if (loads >= MaxLoadsPerUpdate)
                {
                    waitingForKept = true;
                    break;
                }
                loads++;
                _world.WrapFurnaces(column);
                _world.LoadColumn(left with { Coordinate = column }, edited: true);
                continue;
            }

            if (_generators.Pending >= MaxGenerating)
                break;

            _generating.Add(column);
            _generators.Post(column);
        }
        _settled = !waitingForKept && _generating.Count == 0;
        _settledCenter = center;
        _settledDistance = RenderDistance;
        _settledVersion = _world.Map.ColumnVersion;
    }

    public void Dispose()
    {
        _generators.Dispose();
    }
}
