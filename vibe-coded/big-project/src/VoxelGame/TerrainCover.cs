using GlmSharp;

namespace VoxelGame;

/// <summary>Keeps a complete cached LOD cover while asynchronous replacements arrive.</summary>
internal sealed class TerrainCover
{
    private readonly HashSet<TerrainLod.Tile> _leaves = [];
    private readonly HashSet<TerrainLod.Tile> _branches = [];
    private readonly HashSet<TerrainLod.Tile> _availableBranches = [];
    private readonly HashSet<TerrainLod.Tile> _roots = [];
    private readonly HashSet<TerrainLod.Tile> _coarseCover = [];
    internal readonly record struct Region(TerrainLod.Tile Source, TerrainLod.Tile Bounds);
    public List<Region> Regions { get; } = [];
    private readonly List<TerrainLod.Tile> _missing = [];
    private int _detailed;
    public List<TerrainLod.Tile> Requests { get; } = [];
    public int UncoveredRoots { get; private set; }

    internal static TerrainLod.Tile Parent(TerrainLod.Tile tile)
    {
        int size = tile.Size * 2;
        // Bit alignment also rounds negative coordinates down.
        return new(tile.X & -size, tile.Y & -size, size, tile.Z & -size);
    }

    public void Reset(IEnumerable<TerrainLod.Tile> wanted, dvec3 eye = default)
    {
        _leaves.Clear(); _branches.Clear(); _roots.Clear();
        foreach (var leaf in wanted)
        {
            _leaves.Add(leaf);
            var tile = leaf;
            while (tile.Size < TerrainLod.RootSize)
            {
                tile = Parent(tile);
                _branches.Add(tile);
            }
            _roots.Add(tile);
        }
        // Ask for the selected fidelity directly, nearest first. Cached ancestors
        // are temporary gap coverage, never an extra generation/refinement ladder.
        Requests.Clear();
        Requests.AddRange(wanted);
    }

    public void Available(TerrainLod.Tile tile)
    {
        while (tile.Size < TerrainLod.RootSize)
        {
            tile = Parent(tile);
            _availableBranches.Add(tile);
        }
    }

    public void Clear()
    {
        _availableBranches.Clear(); Regions.Clear(); _missing.Clear(); Requests.Clear(); _coarseCover.Clear();
        UncoveredRoots = 0;
        _leaves.Clear(); _branches.Clear(); _roots.Clear();
    }

    public void Select(Func<TerrainLod.Tile, bool> cached, Func<TerrainLod.Tile, bool> detailed)
    {
        Regions.Clear(); _missing.Clear(); _coarseCover.Clear(); UncoveredRoots = 0; _detailed = 0;
        foreach (var root in _roots)
            if (!Visit(root)) UncoveredRoots++;
        foreach (var region in Regions)
            if (region.Source.Size > 16) _coarseCover.Add(region.Bounds);

        bool Visit(TerrainLod.Tile tile)
        {
            if (_leaves.Contains(tile))
            {
                if (tile.Size == 16 && detailed(tile)) { _detailed++; return true; }
                return CachedCover(tile);
            }
            if (!_branches.Contains(tile)) { _missing.Add(tile); return false; }
            int first = Regions.Count, missing = _missing.Count, details = _detailed;
            int half = tile.Size / 2;
            bool complete = true;
            for (int z = tile.Z; z < tile.Z + tile.Height; z += half)
            for (int y = tile.Y; y < tile.Y + tile.Size; y += half)
            for (int x = tile.X; x < tile.X + tile.Size; x += half)
                complete &= Visit(new(x, y, half, z));
            if (complete) return true;
            if (!cached(tile)) return false;
            if (Regions.Count == first && _detailed == details)
                Regions.Add(new(tile, tile));
            else
                // Keep every ready child and detailed chunk. Clip cached parent
                // geometry to the missing regions instead of replacing the children.
                for (int i = missing; i < _missing.Count; i++) Regions.Add(new(tile, _missing[i]));
            _missing.RemoveRange(missing, _missing.Count - missing);
            return true;
        }

        bool CachedCover(TerrainLod.Tile tile)
        {
            if (cached(tile)) { Regions.Add(new(tile, tile)); return true; }
            if (tile.Size <= 16 || !_availableBranches.Contains(tile))
            { _missing.Add(tile); return false; }
            int half = tile.Size / 2;
            bool complete = true;
            for (int z = tile.Z; z < tile.Z + tile.Height; z += half)
            for (int y = tile.Y; y < tile.Y + tile.Size; y += half)
            for (int x = tile.X; x < tile.X + tile.Size; x += half)
                complete &= CachedCover(new(x, y, half, z));
            return complete;
        }
    }

    public bool CoversDetail(ivec3 chunk)
    {
        TerrainLod.Tile tile = new(chunk.x * 16, chunk.y * 16, 16, chunk.z * 16);
        while (true)
        {
            if (_coarseCover.Contains(tile)) return true;
            if (tile.Size >= TerrainLod.RootSize) return false;
            tile = Parent(tile);
        }
    }
}
