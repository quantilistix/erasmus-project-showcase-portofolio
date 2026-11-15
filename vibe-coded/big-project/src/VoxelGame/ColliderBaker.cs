using GlmSharp;

using MYT.Kit.Vulkan;

namespace VoxelGame;


/// <summary>
/// Makes the map's chunk colliders ahead of need, on a background thread, for the bodies that
/// move about a map streamed around the player (<see cref="World.FollowPlayer"/>): a
/// collider's mesh, and above all the cooking the physics does on a new body, would otherwise
/// fall on the step a body comes near its chunk, as a hitch. It also keeps the colliders the
/// bodies leave behind, for when they turn back. A collider is good only for the blocks it was
/// made from: any change to its chunk or the chunks beside it throws it away.
/// </summary>
internal sealed class ColliderBaker : IDisposable
{
    /// <summary>A chunk to bake, as a copy of its cells with a border of one, and the request it answers.</summary>
    private readonly record struct BakeJob(ivec3 Chunk, long Request, Voxel[] Cells);

    /// <summary>A baked collider, null when the chunk has no faces.</summary>
    private readonly record struct BakeResult(ivec3 Chunk, long Request, FixedBody? Collider, Voxel[] Cells);

    /// <summary>A chunk's collider as the map would build it now: from its mesh, its faces' materials, seams open.</summary>
    public static FixedBody? Make((Mesh3D Mesh, SurfaceMaterial[] Surfaces)? built)
    {
        if (built is not (Mesh3D mesh, SurfaceMaterial[] surfaces))
            return null;

        // The map's surface runs on from chunk to chunk, so nothing should catch on the seams.
        return new FixedBody(mesh) { SurfaceMaterials = surfaces, OpenEdgesContinue = true };
    }

    private static BakeResult Bake(BakeJob job)
    {
        FixedBody? collider = Make(VoxelMesher.BuildCollisionMesh(job.Cells, job.Chunk, openBelowBedrock: true, mergeFaces: VoxelMesher.MergeCollisionFaces));
        collider?.Cook();
        return new BakeResult(job.Chunk, job.Request, collider, job.Cells);
    }

    public ColliderBaker()
    {
        _workers = new BackgroundWorkers<BakeJob, BakeResult>("Collider baking", 1, Bake);
    }

    private readonly BackgroundWorkers<BakeJob, BakeResult> _workers;
    // The latest request per chunk in flight; a result for any other is out of date.
    private readonly Dictionary<ivec3, long> _requests = [];
    private readonly Dictionary<ivec3, FixedBody?> _baked = [];
    private readonly Stack<Voxel[]> _cellCopies = new();
    private readonly List<ivec3> _dropped = [];
    private long _nextRequest;

    /// <summary>Forgets what was baked or is baking for a chunk whose blocks, or a neighbour's, changed.</summary>
    public void Invalidate(ivec3 chunk)
    {
        _requests.Remove(chunk);
        _baked.Remove(chunk);
    }

    /// <summary>
    /// Starts baking those of these chunks that have neither a collider in the map nor one
    /// waiting or baking, unless they are known to have no faces.
    /// </summary>
    public void Prepare(VoxelMap map, IEnumerable<ivec3> chunks, IReadOnlySet<ivec3> faceless)
    {
        Receive();
        foreach (ivec3 chunk in chunks)
        {
            if (_baked.ContainsKey(chunk) || _requests.ContainsKey(chunk) || map.Colliders.ContainsKey(chunk) || faceless.Contains(chunk) || !map.Grid.TryGetChunk(chunk, out _))
                continue;

            Voxel[] cells = _cellCopies.Count > 0 ? _cellCopies.Pop() : new Voxel[VoxelMesher.PaddedVolume];
            VoxelMesher.CopyPadded(map.Grid, chunk, cells);
            long request = ++_nextRequest;
            _requests[chunk] = request;
            _workers.Post(new BakeJob(chunk, request, cells));
        }
    }

    /// <summary>A chunk's collider baked and still good, if there is one (null for a chunk with no faces).</summary>
    public bool TryTake(ivec3 chunk, out FixedBody? collider)
    {
        Receive();
        return _baked.Remove(chunk, out collider);
    }

    /// <summary>Keeps a collider the bodies left behind, for if they come back.</summary>
    public void Keep(ivec3 chunk, FixedBody collider)
    {
        _requests.Remove(chunk);
        _baked[chunk] = collider;
    }

    /// <summary>Lets go of every chunk but these.</summary>
    public void KeepOnly(IReadOnlySet<ivec3> wanted)
    {
        _dropped.Clear();
        foreach (ivec3 chunk in _baked.Keys)
        {
            if (!wanted.Contains(chunk))
                _dropped.Add(chunk);
        }

        foreach (ivec3 chunk in _requests.Keys)
        {
            if (!wanted.Contains(chunk))
                _dropped.Add(chunk);
        }

        foreach (ivec3 chunk in _dropped)
        {
            _baked.Remove(chunk);
            _requests.Remove(chunk);
        }
    }

    private void Receive()
    {
        while (_workers.TryTake(out BakeResult result))
        {
            _cellCopies.Push(result.Cells);
            if (!_requests.TryGetValue(result.Chunk, out long request) || request != result.Request)
                continue;

            _requests.Remove(result.Chunk);
            _baked[result.Chunk] = result.Collider;
        }
    }

    public void Dispose()
    {
        _workers.Dispose();
    }
}
