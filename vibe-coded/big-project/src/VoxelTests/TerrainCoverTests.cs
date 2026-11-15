using GlmSharp;
using VoxelGame;

namespace VoxelTests;

internal static class TerrainCoverTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios = [("terrain-cover", Check)];

    private static bool Check()
    {
        TerrainCover cover = new();
        TerrainLod.Tile parent = new(-32, -32, 32);
        TerrainLod.Tile[] children = [.. from z in new[] { 0, 16 }
            from y in new[] { -32, -16 } from x in new[] { -32, -16 }
            select new TerrainLod.Tile(x, y, 16, z)];
        HashSet<TerrainLod.Tile> cached = [parent];
        cover.Available(parent);
        cover.Reset(children);
        foreach (var child in children)
        {
            cover.Select(cached.Contains, _ => false);
            foreach (var ready in cached.Where(t => t.Size == 16))
                if (!cover.Regions.Any(r => r.Source == ready && r.Bounds == ready)) return false;
            if (cover.Regions.Any(r => r.Source == parent && cached.Contains(r.Bounds) && r.Bounds.Size == 16)) return false;
            cached.Add(child); cover.Available(child);
        }
        cover.Select(cached.Contains, _ => false);
        if (cover.Regions.Count != 8 || cover.Regions.Any(r => r.Source == parent)) return false;
        if (cover.CoversDetail(new ivec3(-2, -2, 0))) return false;
        // Coarsening must retain all descendants while the new parent is pending.
        cached.Remove(parent);
        cover.Reset([parent]);
        cover.Select(cached.Contains, _ => false);
        if (cover.Regions.Count != 8) return false;
        cached.Add(parent);
        cover.Select(cached.Contains, _ => false);
        if (cover.Regions.Count != 1 || cover.Regions[0].Source != parent) return false;
        // Ready high-detail chunks must never be replaced by a cached coarse parent.
        // Fallback parent draws are clipped to the seven missing sibling regions.
        cover.Reset(children);
        cached.Clear(); cached.Add(parent);
        cover.Select(cached.Contains, t => t == children[0]);
        if (cover.Regions.Count != 7 || cover.CoversDetail(new ivec3(-2, -2, 0))) return false;
        if (cover.Regions.Any(r => r.Source != parent || r.Bounds == children[0])) return false;
        if (!cover.Requests.SequenceEqual(children)) return false;
        cover.Select(cached.Contains, _ => true);
        if (cover.Regions.Count != 0 || cover.CoversDetail(new ivec3(-2, -2, 0))) return false;
        Console.WriteLine("   refinement, coarsening, negative coordinates and detailed/LOD handoffs retain a complete nonoverlapping cover");
        return true;
    }
}
