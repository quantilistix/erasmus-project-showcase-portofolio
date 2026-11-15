using System.Runtime.InteropServices;

using GlmSharp;

using MYT.IKit;

namespace MYT.Kit.Vulkan;


/// <summary>
/// One 16³ block of a <see cref="Grid3D{T}"/>. Cells are stored x-fastest, then y, then z
/// (<see cref="Grid3D{T}.IndexInChunk"/>).
/// </summary>
public sealed class Grid3DChunk<T> where T : unmanaged, IEquatable<T>
{
    internal Grid3DChunk(ivec3 coordinate, T empty)
    {
        Coordinate = coordinate;
        _cells = new T[Grid3D<T>.ChunkVolume];
        if (!empty.Equals(default))
            Array.Fill(_cells, empty);
    }

    internal readonly T[] _cells;

    /// <summary>Chunk coordinate: the cell coordinate of its first cell divided by the chunk size.</summary>
    public ivec3 Coordinate { get; }

    /// <summary>Changes on every write, including per-cell state updates, for independent GPU mirrors.</summary>
    public long Revision { get; internal set; }

    /// <summary>The cell coordinate of the chunk's first cell.</summary>
    public ivec3 Origin => Coordinate * Grid3D<T>.ChunkSize;

    /// <summary>Cells that are not <see cref="Grid3D{T}.Empty"/>.</summary>
    public int Count { get; internal set; }

    public ReadOnlySpan<T> Cells => _cells;
}

/// <summary>Where a ray first entered an occupied cell of a <see cref="Grid3D{T}"/>.</summary>
/// <param name="Cell">The occupied cell.</param>
/// <param name="Normal">The face the ray entered through, as an outward unit axis; zero if the ray started inside the cell.</param>
/// <param name="Distance">Distance along the normalized ray direction, in cells.</param>
public readonly record struct Grid3DHit(ivec3 Cell, ivec3 Normal, double Distance);


/// <summary>
/// A sparse, unbounded 3D grid of cells (voxels), stored in 16³ chunks that exist only where
/// something is not <see cref="Empty"/>. Cell (x, y, z) covers [x, x + 1) × [y, y + 1) × [z, z + 1)
/// in grid space.
/// <para>
/// Besides storage, it offers what most voxel code needs: change tracking per chunk (for
/// remeshing), fast copies of a region with its border (for meshing and neighbour lookups),
/// face-connected flood fills and components (for splitting and support checks) and ray casts.
/// Single-threaded: readers on other threads need their own copy.
/// </para>
/// </summary>
public sealed class Grid3D<T> : IAsset where T : unmanaged, IEquatable<T>
{
    public const int ChunkBits = 4;
    public const int ChunkSize = 1 << ChunkBits;
    public const int ChunkMask = ChunkSize - 1;
    public const int ChunkVolume = ChunkSize * ChunkSize * ChunkSize;

    private const int KeyBits = 21;
    private const long KeyMask = (1L << KeyBits) - 1;
    private const uint FileMagic = 0x44334447; // "GD3D"
    private const uint FileVersion = 1;

    /// <summary>The six face directions: +x, −x, +y, −y, +z, −z. Face k's opposite is k ^ 1.</summary>
    public static readonly ivec3[] FaceDirections =
    [
        new(1, 0, 0),
        new(-1, 0, 0),
        new(0, 1, 0),
        new(0, -1, 0),
        new(0, 0, 1),
        new(0, 0, -1)
    ];

    public static ivec3 ChunkOf(ivec3 cell)
    {
        return cell >> ChunkBits;
    }

    public static int IndexInChunk(ivec3 cell)
    {
        return (cell.x & ChunkMask) | (cell.y & ChunkMask) << ChunkBits | (cell.z & ChunkMask) << (2 * ChunkBits);
    }

    public static ivec3 CellOf(ivec3 chunk, int index)
    {
        return chunk * ChunkSize + new ivec3(index & ChunkMask, (index >> ChunkBits) & ChunkMask, index >> (2 * ChunkBits));
    }

    /// <summary>Reads a grid written by <see cref="Save"/>.</summary>
    public static Grid3D<T> Load(Stream stream)
    {
        using BinaryReader reader = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != FileMagic || reader.ReadUInt32() != FileVersion)
            throw new InvalidDataException("Not a Grid3D file");
        if (reader.ReadInt32() != Marshal.SizeOf<T>() || reader.ReadInt32() != ChunkSize)
            throw new InvalidDataException("The file was written for a different cell type or chunk size");

        T empty = MemoryMarshal.Read<T>(reader.ReadBytes(Marshal.SizeOf<T>()));
        Grid3D<T> grid = new(empty);
        int chunkCount = reader.ReadInt32();
        for (int c = 0; c < chunkCount; c++)
        {
            ivec3 coordinate = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            Grid3DChunk<T> chunk = grid.CreateChunk(coordinate);
            reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(chunk._cells.AsSpan()));

            foreach (T cell in chunk._cells)
            {
                if (!cell.Equals(empty))
                    chunk.Count++;
            }

            grid.Count += chunk.Count;
        }

        return grid;
    }

    private static long Key(ivec3 chunk)
    {
        return (chunk.x & KeyMask) | (chunk.y & KeyMask) << KeyBits | (chunk.z & KeyMask) << (2 * KeyBits);
    }

    /// <param name="empty">The value of every cell never set; cells holding it take no storage.</param>
    public Grid3D(T empty = default)
    {
        Empty = empty;
    }

    private readonly Dictionary<long, Grid3DChunk<T>> _chunks = [];
    private readonly HashSet<ivec3> _changedChunks = [];
    private readonly HashSet<ivec3> _collisionChangedChunks = [];

    public T Empty { get; }

    /// <summary>Include edge/corner neighbours for consumers sampling ambient occlusion.</summary>
    public bool TrackDiagonalChanges { get; set; }

    /// <summary>Cells that are not <see cref="Empty"/>.</summary>
    public int Count { get; private set; }

    /// <summary>Changes whenever cell data changes, including streaming and unmarked updates.</summary>
    public long Revision { get; private set; }

    /// <summary>Changes whenever a chunk is created or freed, but not when cells change within kept chunks.</summary>
    public long ChunkRevision { get; private set; }

    public IReadOnlyCollection<Grid3DChunk<T>> Chunks => _chunks.Values;

    public T this[ivec3 cell]
    {
        get => Get(cell);
        set => Set(cell, value);
    }

    public T this[int x, int y, int z]
    {
        get => Get(new ivec3(x, y, z));
        set => Set(new ivec3(x, y, z), value);
    }

    public T Get(ivec3 cell)
    {
        return _chunks.TryGetValue(Key(ChunkOf(cell)), out Grid3DChunk<T>? chunk) ? chunk._cells[IndexInChunk(cell)] : Empty;
    }

    public bool IsOccupied(ivec3 cell)
    {
        return !Get(cell).Equals(Empty);
    }

    /// <summary>
    /// Sets a cell and returns whether it changed. A change marks its chunk as changed, and
    /// also the chunk across any face the cell touches, since that chunk's exposed faces may
    /// have changed too. A chunk left with no occupied cells is freed.
    /// </summary>
    public bool Set(ivec3 cell, T value)
    {
        ivec3 coordinate = ChunkOf(cell);
        long key = Key(coordinate);
        bool occupied = !value.Equals(Empty);

        if (!_chunks.TryGetValue(key, out Grid3DChunk<T>? chunk))
        {
            if (!occupied)
                return false;

            chunk = CreateChunk(coordinate);
        }

        int index = IndexInChunk(cell);
        T previous = chunk._cells[index];
        if (previous.Equals(value))
            return false;

        bool wasOccupied = !previous.Equals(Empty);
        chunk._cells[index] = value;
        chunk.Revision++;
        Revision++;
        int delta = (occupied ? 1 : 0) - (wasOccupied ? 1 : 0);
        chunk.Count += delta;
        Count += delta;

        MarkChanged(cell);
        if (chunk.Count == 0)
            RemoveChunkEntry(key);

        return true;
    }

    /// <summary>
    /// Replaces a cell's value without marking anything changed, for per-cell state such as
    /// damage that does not alter which cells are occupied. Throws if it would.
    /// </summary>
    public void Update(ivec3 cell, T value)
    {
        if (!_chunks.TryGetValue(Key(ChunkOf(cell)), out Grid3DChunk<T>? chunk))
            throw new InvalidOperationException("Only occupied cells can be updated");

        int index = IndexInChunk(cell);
        if (chunk._cells[index].Equals(Empty) || value.Equals(Empty))
            throw new InvalidOperationException("Update cannot change whether a cell is occupied; use Set");

        chunk._cells[index] = value;
        chunk.Revision++;
        Revision++;
    }

    public bool TryGetChunk(ivec3 coordinate, out Grid3DChunk<T> chunk)
    {
        return _chunks.TryGetValue(Key(coordinate), out chunk!);
    }

    /// <summary>
    /// Replaces a whole chunk's cells at once (in <see cref="IndexInChunk"/> order), for
    /// streaming a world in by chunks. Marks it changed, and the six chunks across its faces,
    /// whose exposed faces may have changed too. A chunk left with no occupied cells is freed.
    /// </summary>
    public void SetChunk(ivec3 coordinate, ReadOnlySpan<T> cells)
    {
        if (cells.Length != ChunkVolume)
            throw new ArgumentException($"A chunk has {ChunkVolume} cells", nameof(cells));

        long key = Key(coordinate);
        if (_chunks.TryGetValue(key, out Grid3DChunk<T>? chunk))
            Count -= chunk.Count;
        else
            chunk = CreateChunk(coordinate);

        cells.CopyTo(chunk._cells);
        chunk.Revision++;
        Revision++;
        int occupied = 0;
        foreach (T cell in cells)
        {
            if (!cell.Equals(Empty))
                occupied++;
        }

        chunk.Count = occupied;
        Count += occupied;
        if (occupied == 0)
            RemoveChunkEntry(key);

        _changedChunks.Add(coordinate);
        _collisionChangedChunks.Add(coordinate);
        foreach (ivec3 direction in FaceDirections) _collisionChangedChunks.Add(coordinate + direction);
        if (TrackDiagonalChanges)
        {
            for (int z = -1; z <= 1; z++)
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                _changedChunks.Add(coordinate + new ivec3(x, y, z));
        }
        else
            foreach (ivec3 direction in FaceDirections)
                _changedChunks.Add(coordinate + direction);
    }

    /// <summary>
    /// Empties a whole chunk at once, for streaming a world out by chunks, and marks it changed;
    /// unlike clearing its cells one by one, not the chunks around it, which are left as they
    /// were. Returns whether it held anything.
    /// </summary>
    public bool RemoveChunk(ivec3 coordinate)
    {
        if (!_chunks.Remove(Key(coordinate), out Grid3DChunk<T>? chunk))
            return false;

        ChunkRevision++;
        Count -= chunk.Count;
        Revision++;
        _changedChunks.Add(coordinate);
        _collisionChangedChunks.Add(coordinate);
        return true;
    }

    /// <summary>Marks the chunk holding a cell, and those across the faces it touches, as changed.</summary>
    public void MarkChanged(ivec3 cell)
    {
        ivec3 coordinate = ChunkOf(cell);
        _changedChunks.Add(coordinate);

        ivec3 local = cell & ChunkMask;
        _collisionChangedChunks.Add(coordinate);
        for (int axis = 0; axis < 3; axis++)
        {
            if (local[axis] == 0) _collisionChangedChunks.Add(coordinate - FaceDirections[2 * axis]);
            else if (local[axis] == ChunkMask) _collisionChangedChunks.Add(coordinate + FaceDirections[2 * axis]);
        }
        if (TrackDiagonalChanges)
        {
            for (int z = local.z == 0 ? -1 : 0; z <= (local.z == ChunkMask ? 1 : 0); z++)
            for (int y = local.y == 0 ? -1 : 0; y <= (local.y == ChunkMask ? 1 : 0); y++)
            for (int x = local.x == 0 ? -1 : 0; x <= (local.x == ChunkMask ? 1 : 0); x++)
                _changedChunks.Add(coordinate + new ivec3(x, y, z));
            return;
        }
        for (int axis = 0; axis < 3; axis++)
        {
            if (local[axis] == 0)
                _changedChunks.Add(coordinate - FaceDirections[2 * axis]);
            else if (local[axis] == ChunkMask)
                _changedChunks.Add(coordinate + FaceDirections[2 * axis]);
        }
    }

    /// <summary>Returns the coordinates of every chunk changed since the last call and forgets them.</summary>
    public ivec3[] TakeChangedChunks()
    {
        ivec3[] changed = [.. _changedChunks];
        _changedChunks.Clear();
        _collisionChangedChunks.Clear();
        return changed;
    }

    /// <summary>Consumes render changes and face-only collision dependencies together.</summary>
    public ivec3[] TakeChangedChunks(out ivec3[] collisionChunks)
    {
        collisionChunks = [.. _collisionChangedChunks];
        return TakeChangedChunks();
    }

    public void Clear()
    {
        foreach (Grid3DChunk<T> chunk in _chunks.Values)
        {
            _changedChunks.Add(chunk.Coordinate);
            _collisionChangedChunks.Add(chunk.Coordinate);
        }

        _chunks.Clear();
        Count = 0;
        Revision++;
        ChunkRevision++;
    }

    /// <summary>Every occupied cell and its value.</summary>
    public IEnumerable<(ivec3 Cell, T Value)> Occupied()
    {
        foreach (Grid3DChunk<T> chunk in _chunks.Values)
        {
            for (int i = 0; i < ChunkVolume; i++)
            {
                T value = chunk._cells[i];
                if (!value.Equals(Empty))
                    yield return (CellOf(chunk.Coordinate, i), value);
            }
        }
    }

    /// <summary>Lowest occupied cell and one past the highest, per axis; both zero when the grid is empty.</summary>
    public (ivec3 Min, ivec3 Max) Bounds()
    {
        ivec3 min = new(int.MaxValue);
        ivec3 max = new(int.MinValue);

        foreach ((ivec3 cell, T _) in Occupied())
        {
            min = ivec3.Min(min, cell);
            max = ivec3.Max(max, cell + 1);
        }

        return Count == 0 ? (ivec3.Zero, ivec3.Zero) : (min, max);
    }

    /// <summary>
    /// Copies the cells of the box [min, min + size) into <paramref name="destination"/>, x
    /// fastest, then y, then z. Much faster than reading cells one by one, so meshers copy a
    /// chunk with a one-cell border and read neighbours from the copy.
    /// </summary>
    public void CopyRegion(ivec3 min, ivec3 size, Span<T> destination)
    {
        if (destination.Length < size.x * size.y * size.z)
            throw new ArgumentException("The destination is smaller than the region", nameof(destination));

        destination[..(size.x * size.y * size.z)].Fill(Empty);
        ivec3 max = min + size;
        ivec3 firstChunk = ChunkOf(min);
        ivec3 lastChunk = ChunkOf(max - 1);

        for (int cz = firstChunk.z; cz <= lastChunk.z; cz++)
        for (int cy = firstChunk.y; cy <= lastChunk.y; cy++)
        for (int cx = firstChunk.x; cx <= lastChunk.x; cx++)
        {
            if (!_chunks.TryGetValue(Key(new ivec3(cx, cy, cz)), out Grid3DChunk<T>? chunk))
                continue;

            ivec3 origin = chunk.Origin;
            ivec3 from = ivec3.Max(min, origin);
            ivec3 to = ivec3.Min(max, origin + ChunkSize);

            for (int z = from.z; z < to.z; z++)
            for (int y = from.y; y < to.y; y++)
            {
                int source = IndexInChunk(new ivec3(from.x, y, z));
                int target = (from.x - min.x) + size.x * ((y - min.y) + size.y * (z - min.z));
                chunk._cells.AsSpan(source, to.x - from.x).CopyTo(destination[target..]);
            }
        }
    }

    /// <summary>
    /// Visits the occupied cells face-connected to <paramref name="start"/> (itself included if
    /// occupied) for which <paramref name="include"/> holds, breadth first. Stops early, returning
    /// true, as soon as <paramref name="stop"/> holds for a visited cell; the cells visited so far
    /// are then in <paramref name="visited"/>.
    /// </summary>
    public bool FloodFill
    (
        ivec3 start,
        HashSet<ivec3> visited,
        Func<ivec3, T, bool>? include = null,
        Func<ivec3, T, bool>? stop = null
    )
    {
        T first = Get(start);
        if (first.Equals(Empty) || (include != null && !include(start, first)) || !visited.Add(start))
            return false;

        if (stop != null && stop(start, first))
            return true;

        Queue<ivec3> frontier = new();
        frontier.Enqueue(start);

        while (frontier.Count > 0)
        {
            ivec3 cell = frontier.Dequeue();
            foreach (ivec3 direction in FaceDirections)
            {
                ivec3 next = cell + direction;
                T value = Get(next);
                if (value.Equals(Empty) || (include != null && !include(next, value)) || !visited.Add(next))
                    continue;

                if (stop != null && stop(next, value))
                    return true;

                frontier.Enqueue(next);
            }
        }

        return false;
    }

    /// <summary>The face-connected groups of occupied cells, largest first.</summary>
    public List<HashSet<ivec3>> ConnectedComponents()
    {
        return ComponentsOf(Occupied().Select(c => c.Cell));
    }

    /// <summary>
    /// The distinct face-connected groups containing the given cells, largest first. After
    /// removing a cell, its six neighbours as seeds tell whether the rest split apart.
    /// </summary>
    public List<HashSet<ivec3>> ComponentsOf(IEnumerable<ivec3> seeds)
    {
        List<HashSet<ivec3>> components = [];
        HashSet<ivec3> seen = [];

        foreach (ivec3 seed in seeds)
        {
            if (seen.Contains(seed) || !IsOccupied(seed))
                continue;

            HashSet<ivec3> component = [];
            FloodFill(seed, component);
            seen.UnionWith(component);
            components.Add(component);
        }

        components.Sort((a, b) => b.Count.CompareTo(a.Count));
        return components;
    }

    /// <summary>
    /// Walks a ray through the grid cell by cell (Amanatides and Woo) and returns the first
    /// occupied cell it enters within <paramref name="maxDistance"/>, in grid units.
    /// </summary>
    public bool Raycast(dvec3 origin, dvec3 direction, double maxDistance, out Grid3DHit hit, Func<T, bool>? include = null)
    {
        hit = default;
        double length = direction.Length;
        if (!(length > 0.0))
            return false;

        dvec3 d = direction / length;
        ivec3 cell = new((int)Math.Floor(origin.x), (int)Math.Floor(origin.y), (int)Math.Floor(origin.z));
        ivec3 step = new(Math.Sign(d.x), Math.Sign(d.y), Math.Sign(d.z));
        dvec3 delta = new
        (
            d.x != 0.0 ? Math.Abs(1.0 / d.x) : double.PositiveInfinity,
            d.y != 0.0 ? Math.Abs(1.0 / d.y) : double.PositiveInfinity,
            d.z != 0.0 ? Math.Abs(1.0 / d.z) : double.PositiveInfinity
        );
        dvec3 next = new
        (
            BoundaryDistance(origin.x, cell.x, d.x, delta.x),
            BoundaryDistance(origin.y, cell.y, d.y, delta.y),
            BoundaryDistance(origin.z, cell.z, d.z, delta.z)
        );

        ivec3 normal = ivec3.Zero;
        double distance = 0.0;

        while (distance <= maxDistance)
        {
            if (IsOccupied(cell) && (include == null || include(this[cell])))
            {
                hit = new Grid3DHit(cell, normal, distance);
                return true;
            }

            int axis = next.x < next.y ? (next.x < next.z ? 0 : 2) : (next.y < next.z ? 1 : 2);
            distance = next[axis];
            next[axis] += delta[axis];
            cell[axis] += step[axis];
            normal = ivec3.Zero;
            normal[axis] = -step[axis];
        }

        return false;
    }

    /// <summary>Writes the grid in a compact binary form that <see cref="Load"/> reads.</summary>
    public void Save(Stream stream)
    {
        using BinaryWriter writer = new(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(FileMagic);
        writer.Write(FileVersion);
        writer.Write(Marshal.SizeOf<T>());
        writer.Write(ChunkSize);

        T empty = Empty;
        writer.Write(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in empty)));
        writer.Write(_chunks.Count);
        foreach (Grid3DChunk<T> chunk in _chunks.Values)
        {
            writer.Write(chunk.Coordinate.x);
            writer.Write(chunk.Coordinate.y);
            writer.Write(chunk.Coordinate.z);
            writer.Write(MemoryMarshal.AsBytes(chunk._cells.AsSpan()));
        }
    }

    private static double BoundaryDistance(double origin, int cell, double direction, double delta)
    {
        if (direction > 0.0)
            return (cell + 1 - origin) * delta;
        if (direction < 0.0)
            return (origin - cell) * delta;
        return double.PositiveInfinity;
    }

    private Grid3DChunk<T> CreateChunk(ivec3 coordinate)
    {
        Grid3DChunk<T> chunk = new(coordinate, Empty);
        _chunks.Add(Key(coordinate), chunk);
        ChunkRevision++;
        return chunk;
    }

    private void RemoveChunkEntry(long key)
    {
        _chunks.Remove(key);
        ChunkRevision++;
    }
}


/// <summary>Loads and saves <see cref="Grid3D{T}"/> files (the format of <see cref="Grid3D{T}.Save"/>).</summary>
public sealed class Grid3DLoader<T> : IAssetLoader<Grid3D<T>>, IAssetSaver<Grid3D<T>> where T : unmanaged, IEquatable<T>
{
    public Grid3D<T> Load(string filename)
    {
        using FileStream stream = File.OpenRead(filename);
        return Grid3D<T>.Load(stream);
    }

    public void Save(Grid3D<T> asset, string filename)
    {
        using FileStream stream = File.Create(filename);
        asset.Save(stream);
    }
}
