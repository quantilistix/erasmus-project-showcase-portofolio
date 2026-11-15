using GlmSharp;

using MYT.Kit.Vulkan;

namespace VoxelGame;

/// <summary>
/// Unions streamed collider requests. Nearby bodies often request the same integer chunk
/// box; expanding it once preserves coverage and first-insertion order without repeatedly
/// hashing every chunk for every body. Both sets retain their capacity between steps.
/// </summary>
internal sealed class ChunkCoverage
{
    private readonly HashSet<(ivec3 First, ivec3 Last)> _ranges = [];
    public HashSet<ivec3> Chunks { get; } = [];

    public void Clear()
    {
        _ranges.Clear();
        Chunks.Clear();
    }

    public void Add(dvec3 low, dvec3 high)
    {
        int size = Grid3D<Voxel>.ChunkSize;
        ivec3 first = new((int)Math.Floor(low.x / size), (int)Math.Floor(low.y / size), Math.Max((int)Math.Floor(low.z / size), 0));
        ivec3 last = new((int)Math.Floor(high.x / size), (int)Math.Floor(high.y / size), Math.Min((int)Math.Floor(high.z / size), VoxelMap.HeightInChunks - 1));
        if (!_ranges.Add((first, last)))
            return;

        for (int z = first.z; z <= last.z; z++)
        for (int y = first.y; y <= last.y; y++)
        for (int x = first.x; x <= last.x; x++)
            Chunks.Add(new ivec3(x, y, z));
    }
}
