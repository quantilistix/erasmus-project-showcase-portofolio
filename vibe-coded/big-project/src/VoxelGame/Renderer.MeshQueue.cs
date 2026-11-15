using System.Diagnostics;
using GlmSharp;
using MYT.Kit.Vulkan;

namespace VoxelGame;

internal sealed unsafe partial class Renderer
{
    private readonly record struct QueuedMesh(ChunkRecord Record, long Ticket);
    private readonly PriorityQueue<QueuedMesh, (int Priority, double Distance, long Order)> _asyncNearest = new();
    private readonly PriorityQueue<QueuedMesh, long> _asyncOldest = new();
    private readonly HashSet<ChunkRecord> _asyncWaiting = [], _asyncEmpty = [];
    private readonly HashSet<ChunkRecord> _blockedEmpty = [];
    private int _asyncQueued, _asyncColumns = -1;
    private long _asyncMeshTurn;
    private dvec3 _asyncEye;
    private Camera? _meshCamera;
    internal double MeshSnapshotBudgetMilliseconds { get; set; } = 1;
    internal ulong MeshUploadBudgetBytes { get; set; } = 2UL << 20;
    internal int MeshesPosted { get; private set; }
    internal int MeshResultsAccepted { get; private set; }
    internal double MeshSnapshotMilliseconds { get; private set; }
    internal ulong MeshBytesUploaded { get; private set; }
    internal double MeshEditLatencyMilliseconds { get; private set; }
    internal double MaxMeshEditLatencyMilliseconds { get; private set; }
    private void RecordMeshLatency(long queuedAt)
    {
        if (queuedAt == 0) return;
        double ms = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
        MeshEditLatencyMilliseconds = Math.Max(MeshEditLatencyMilliseconds, ms);
        MaxMeshEditLatencyMilliseconds = Math.Max(MaxMeshEditLatencyMilliseconds, ms);
    }
    internal uint[]? UploadedMesh(int body, ivec3 chunk) => _chunks.TryGetValue(body, out var records)
        && records.TryGetValue(chunk, out var record) ? record.UploadedVertices : null;
    internal (long Requested, long Displayed) MeshVersions(int body, ivec3 chunk) => _chunks.TryGetValue(body, out var records)
        && records.TryGetValue(chunk, out var record) ? (record.Version, record.DisplayedVersion) : default;

    private void InvalidateAsyncMesh(VoxelBody body, Dictionary<ivec3, ChunkRecord> records, ivec3 chunk)
    {
        bool present = body.Grid.TryGetChunk(chunk, out _);
        if (body is VoxelMap map && !map.IsLoaded(new(chunk.x, chunk.y)))
        {
            if (records.Remove(chunk, out var unloaded)) Free(unloaded);
            return;
        }
        if (!records.TryGetValue(chunk, out ChunkRecord? record))
        {
            if (!present) return;
            records[chunk] = record = new ChunkRecord { Owner = body, Coordinate = chunk };
        }
        record.Owner = body;
        record.Coordinate = chunk;
        record.Version = ++_nextVersion;
        if (!record.PendingEdit) record.PendingSince = Stopwatch.GetTimestamp();
        record.PendingEdit = true;
        PromoteGpuMesh(record);
        record.PendingEmptyVersion = 0;
        _asyncEmpty.Remove(record);
        QueueAsyncMesh(record);
    }

    private void QueueAsyncMesh(ChunkRecord record)
    {
        if (!record.Live || record.MeshInFlight || record.AsyncQueued || record.WaitingForColumns) return;
        record.AsyncQueued = true;
        _asyncQueued++;
        long ticket = ++record.QueueTicket;
        var body = record.Owner!;
        dvec3 center = body.ToWorld(new dvec3(record.Coordinate * 16) + 8);
        bool visible = _meshCamera is not Camera camera || (camera.Position - _asyncEye).LengthSqr > 64 * 64
            || new Frustum(camera).CanSee(center, ChunkRadius);
        int priority = (visible ? 0 : 2) + (record.HasMesh ? 0 : 1);
        QueuedMesh item = new(record, ticket);
        _asyncNearest.Enqueue(item, (priority, (center - _asyncEye).LengthSqr, ticket));
        _asyncOldest.Enqueue(item, record.PendingSince);
    }

    private static bool Current(QueuedMesh job) => job.Record.Live && job.Record.AsyncQueued
        && job.Record.QueueTicket == job.Ticket;

    private bool PopAsyncMesh(out ChunkRecord record)
    {
        while (_asyncOldest.TryPeek(out var old, out _) && !Current(old)) _asyncOldest.Dequeue();
        // Near edits win normally; old work gets a turn so continuous blasts cannot
        // indefinitely starve initial terrain or another body's edits.
        if (_asyncMeshTurn++ % 4 == 3 && _asyncOldest.TryPeek(out var oldest, out long since)
            && Stopwatch.GetElapsedTime(since).TotalMilliseconds >= 250)
        {
            _asyncOldest.Dequeue();
            record = oldest.Record;
        }
        else
        {
            QueuedMesh next;
            do
            {
                if (!_asyncNearest.TryDequeue(out next, out _)) { record = null!; return false; }
            } while (!Current(next));
            record = next.Record;
        }
        record.AsyncQueued = false;
        _asyncQueued--;
        return true;
    }

    private void PostAsyncMeshes(VoxelMap map)
    {
        MeshesPosted = 0;
        long start = Stopwatch.GetTimestamp();
        if (_asyncColumns != map.ColumnVersion)
        {
            _asyncColumns = map.ColumnVersion;
            foreach (ChunkRecord record in _asyncWaiting)
            {
                record.WaitingForColumns = false;
                QueueAsyncMesh(record);
            }
            _asyncWaiting.Clear();
        }
        while (_meshers.Pending < 32 && MeshesPosted < 32)
        {
            if (MeshesPosted > 0 && Stopwatch.GetElapsedTime(start).TotalMilliseconds >= MeshSnapshotBudgetMilliseconds) break;
            if (!PopAsyncMesh(out ChunkRecord record)) break;
            VoxelBody body = record.Owner!;
            if (body is VoxelMap unloaded && !unloaded.IsLoaded(new(record.Coordinate.x, record.Coordinate.y)))
            {
                if (_chunks.TryGetValue(body.Id, out var expiredRecords)) expiredRecords.Remove(record.Coordinate);
                Free(record);
                continue;
            }
            if (body is VoxelMap terrain && !NeighboursLoaded(terrain, record.Coordinate))
            {
                record.WaitingForColumns = true;
                _asyncWaiting.Add(record);
                continue;
            }
            if (GpuCulledMeshing && (record.HasMesh || GpuMeshInitialChunks))
            {
                // Packing a voxel snapshot is small; avoid a worker round trip
                // before the GPU can even begin an interactive edit.
                record.MeshInFlight = true;
                QueueGpuMesh(CaptureGpuMesh(record));
                MeshesPosted++;
                continue;
            }
            Voxel[] cells = _cellCopies.Count > 0 ? _cellCopies.Pop() : new Voxel[VoxelMesher.PaddedVolume];
            VoxelMesher.CopyPadded(body.Grid, record.Coordinate, cells);
            var dependencies = EmptyMeshDependencies(record);
            IncrementalVoxelMesh? state = record.Mesh;
            record.Mesh = null; // exclusive ownership passes to the worker until completion
            record.MeshInFlight = true;
            record.PendingEdit = false;
            _meshers.Post(new(body.Id, record.Coordinate, record.Version, cells, TransparentLeaves,
                IncrementalMeshing, record, state, dependencies, body.Grid.Count == 1, record.PendingSince,
                GpuCulledMeshing && (record.HasMesh || GpuMeshInitialChunks)));
            MeshesPosted++;
        }
        MeshSnapshotMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Prof.Add("mesh.snapshot", MeshSnapshotMilliseconds);
    }

    private (ChunkRecord Record, long Version)[]? EmptyMeshDependencies(ChunkRecord record)
    {
        VoxelBody body = record.Owner!;
        if (body.Grid.TryGetChunk(record.Coordinate, out _) || !_chunks.TryGetValue(body.Id, out var records)) return null;
        List<(ChunkRecord, long)> neighbors = [];
        for (int z = -1; z <= 1; z++)
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            // Never wait for initial chunks held back by unloaded halo columns.
            if ((x != 0 || y != 0 || z != 0) && records.TryGetValue(record.Coordinate + new ivec3(x, y, z), out var neighbor)
                && neighbor.HasMesh) neighbors.Add((neighbor, neighbor.Version));
        return [.. neighbors];
    }

    private MeshResult CaptureGpuMesh(ChunkRecord record)
    {
        Voxel[] cells = _cellCopies.Count > 0 ? _cellCopies.Pop() : new Voxel[VoxelMesher.PaddedVolume];
        VoxelBody body = record.Owner!;
        VoxelMesher.CopyPadded(body.Grid, record.Coordinate, cells);
        MeshResult result = new(body.Id, record.Coordinate, record.Version, [], [], null, record,
            EmptyMeshDependencies(record), record.PendingSince, GpuMeshInputs.Pack(cells), TransparentLeaves,
            cells.Any(v => BlockShapes.HasShape(v.Type)));
        _cellCopies.Push(cells);
        record.PendingEdit = false;
        record.Mesh = null;
        return result;
    }

    private void TakeAsyncMesh(MeshResult result)
    {
        ChunkRecord record = result.Record!;
        if (!record.Live) return;
        if (result.GpuCells != null)
        {
            QueueGpuMesh(result);
            return;
        }
        record.MeshInFlight = false;
        record.Mesh = result.Mesh;
        if (record.PendingEdit) QueueAsyncMesh(record);
        VoxelBody body = record.Owner!;
        if (!_chunks.TryGetValue(body.Id, out var records) || !records.TryGetValue(result.Chunk, out var current)
            || !ReferenceEquals(current, record)) return;
        // A completed snapshot is useful progress even when another edit is pending.
        // Rejecting all intermediate results would starve continuously changing chunks.
        if (result.Vertices.Length == 0)
        {
            if (record.Version > result.Version && body.Grid.TryGetChunk(result.Chunk, out _)) return;
            record.PendingEmptyVersion = result.Version;
            record.EmptyQueuedAt = result.QueuedAt;
            record.EmptyDependencies = result.Dependencies ?? [];
            _asyncEmpty.Add(record);
        }
        else
        {
            record.PendingEmptyVersion = 0;
            _asyncEmpty.Remove(record);
            Accept(body, records, result.Chunk, record, result.Vertices, urgent: record.HasMesh, version: result.Version, queuedAt: result.QueuedAt);
        }
        MeshResultsAccepted++;
    }

    private void CommitEmptyMeshes()
    {
        if (_asyncEmpty.Count == 0) return;
        // Connected empty neighbours may depend on each other, but an unrelated
        // deletion must not wait for this entire world's pending empty meshes.
        _blockedEmpty.Clear();
        foreach (ChunkRecord record in _asyncEmpty)
            foreach (var dependency in record.EmptyDependencies)
                if (dependency.Record.Live && dependency.Record.DisplayedVersion < dependency.Version
                    && dependency.Record.PendingEmptyVersion < dependency.Version) _blockedEmpty.Add(record);
        bool changed;
        do
        {
            changed = false;
            foreach (ChunkRecord record in _asyncEmpty)
            {
                if (_blockedEmpty.Contains(record)) continue;
                foreach (var dependency in record.EmptyDependencies)
                    if (dependency.Record.Live && dependency.Record.DisplayedVersion < dependency.Version
                        && _blockedEmpty.Contains(dependency.Record))
                    { changed |= _blockedEmpty.Add(record); break; }
            }
        } while (changed);
        foreach (ChunkRecord record in _asyncEmpty)
        {
            if (!record.Live || _blockedEmpty.Contains(record)) continue;
            if (record.HasMesh && record.QuadCount != 0) RecordMeshLatency(record.EmptyQueuedAt);
            Bury(record.Vertices);
            record.Vertices = default;
            record.QuadCount = 0;
            record.HasWater = false;
            record.UploadedVertices = [];
            record.UploadVersion = record.DisplayedVersion = record.PendingEmptyVersion;
            record.PendingEmptyVersion = 0;
            record.EmptyDependencies = [];
            if (!record.HasMesh) _meshCoverageGeneration++;
            record.HasMesh = true;
            ReleaseSlot(record);
        }
        _asyncEmpty.RemoveWhere(record => !record.Live || !_blockedEmpty.Contains(record));
        _blockedEmpty.Clear();
    }
}
