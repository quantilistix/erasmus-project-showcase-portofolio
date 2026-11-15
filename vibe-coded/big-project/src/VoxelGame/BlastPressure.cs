using GlmSharp;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VoxelGame;

/// <summary>
/// Positive, traveling compression pulses on the air grid. This is an absorbing wavefront
/// approximation, not a signed acoustic solver: incoming directional pressure loads add,
/// while opposing loads cancel momentum without cancelling the damaging compression.
/// </summary>
internal sealed class BlastPressure
{
    internal const double WaveSpeed = 64;
    internal const double PulseDuration = .05;
    internal const double PeakStress = 96e6;
    internal const double Impulse = 6000; // Pa*s at the source, before material coupling.
    internal const double Threshold = 1.0 / 125; // One unobstructed TNT reaches 16 blocks.

    internal readonly record struct Load(double Pressure, dvec3 Difference)
    {
        public static Load operator +(Load a, Load b) => new(a.Pressure + b.Pressure, a.Difference + b.Difference);
        public static Load operator *(Load a, double weight) => new(a.Pressure * weight, a.Difference * weight);
        public dvec3 Momentum(double area, double dt) => Difference * (Impulse * area * dt / PulseDuration);
    }

    private struct Visit
    {
        public double Distance, TimePlusOne;
    }

    // Only the bounded neighborhood of a live blast owns storage. Flat arrays keep the
    // hot neighbor relaxations out of hash tables; the pool reuses them across blasts.
    private sealed class Wave(ivec3 origin, double time)
    {
        public readonly ivec3 Origin = origin;
        public readonly double Start = time;
        public double Strength = 1;
        private Visit[] _visits = [];
        private int _radius, _side;
        public readonly int[] NeighborIndices = new int[26];

        public void Grow(int radius)
        {
            if (radius <= _radius) return;
            int side = radius * 2 + 1;
            Visit[] visits = ArrayPool<Visit>.Shared.Rent(checked(side * side * side));
            Array.Clear(visits, 0, side * side * side);
            int offset = radius - _radius;
            for (int z = 0; z < _side; z++)
            for (int y = 0; y < _side; y++)
                Array.Copy(_visits, (z * _side + y) * _side, visits,
                    ((z + offset) * side + y + offset) * side + offset, _side);
            Release();
            _visits = visits;
            _radius = radius;
            _side = side;
            for (int i = 0; i < Neighbors.Length; i++)
            {
                ivec3 p = Neighbors[i].Offset;
                NeighborIndices[i] = (p.z * side + p.y) * side + p.x;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref Visit At(ivec3 cell)
        {
            return ref _visits[Index(cell)];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Index(ivec3 cell) => ((cell.z - Origin.z + _radius) * _side
            + cell.y - Origin.y + _radius) * _side + cell.x - Origin.x + _radius;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref Visit At(int index) => ref _visits[index];

        public void Release()
        {
            if (_visits.Length != 0) ArrayPool<Visit>.Shared.Return(_visits);
            _visits = [];
        }
    }

    private readonly record struct Arrival(Wave Wave, ivec3 Cell, double Distance, ivec3 From, double Duration = PulseDuration);
    private readonly record struct Pulse(Arrival Arrival, double Start, double End);
    private readonly record struct Scheduled(Arrival Arrival, double Time);
    private static readonly (ivec3 Offset, double Distance, int Faces)[] Neighbors = CreateNeighbors();
    // Every edge takes at least one face-travel interval. Arrivals in one time bin
    // cannot reach each other, so they need no per-voxel priority-heap ordering.
    private const double BinDuration = VoxelBody.CellSize / WaveSpeed;
    private readonly PriorityQueue<long, long> _bins = new();
    private readonly Dictionary<long, List<Scheduled>> _pending = [];
    private readonly Stack<List<Scheduled>> _freeBins = [];
    private readonly List<Pulse> _pulses = [];
    private readonly Dictionary<ivec3, List<(Arrival Arrival, double End)>> _blocked = [];
    private readonly Dictionary<ivec3, Load> _field = [];
    private readonly HashSet<ivec3> _opened = [];
    private readonly List<Wave> _waves = [];
    private double _support = 16 * VoxelBody.CellSize;
    private double _time;
    private int _sources;
    private dvec3 _sourceMin, _sourceMax;
    private ivec3 _fieldMin, _fieldMax;

    public IReadOnlyDictionary<ivec3, Load> Cells => _field;
    public bool Active => _pending.Count > 0 || _pulses.Count > 0;

    public void Emit(dvec3 center)
    {
        if (!Active)
        {
            // Nothing live refers to the previous time origin. Restart it so that
            // separate identical detonations do not accumulate floating-point drift.
            _time = 0;
            _sources = 0;
            _blocked.Clear();
            ReleaseWaves();
            _sourceMin = _sourceMax = center;
        }
        _sourceMin = dvec3.Min(_sourceMin, center);
        _sourceMax = dvec3.Max(_sourceMax, center);
        _sources++;
        _support = 4 * (Math.Cbrt(_sources / Threshold) - 1);
        int radius = (int)Math.Ceiling((_support + VoxelBody.CellSize) / VoxelBody.CellSize) + 2;
        // Moving and rotated TNT injects into the containing GLOBAL voxel, just like
        // map TNT. Sampling moving receivers remains interpolated.
        ivec3 cell = (ivec3)dvec3.Floor(center / VoxelBody.CellSize);
        Wave? wave = _waves.Find(w => w.Origin == cell && w.Start == _time);
        if (wave != null) { wave.Strength++; return; }
        wave = new(cell, _time);
        wave.Grow(radius);
        _waves.Add(wave);
        Enqueue(new(wave, cell, 0, cell), _time);
    }

    public void Advance(double dt, Func<ivec3, bool> solid)
    {
        if (!(dt > 0)) return;
        _field.Clear();
        _fieldMin = new(int.MaxValue); _fieldMax = new(int.MinValue);
        double end = _time + dt;
        // Superposition determines the support, rather than clipping every TNT at 16 m.
        int radius = (int)Math.Ceiling((_support + VoxelBody.CellSize) / VoxelBody.CellSize) + 2;
        foreach (Wave wave in _waves) wave.Grow(radius);
        while (_bins.TryPeek(out long bin, out _) && bin * BinDuration < end)
        {
            List<Scheduled> scheduled = _pending[bin];
            int remaining = 0;
            for (int i = 0; i < scheduled.Count; i++)
            {
                Scheduled entry = scheduled[i];
                var (arrival, arrivalTime) = entry;
                if (arrivalTime >= end) { scheduled[remaining++] = entry; continue; }
                if (arrivalTime > arrival.Wave.At(arrival.Cell).TimePlusOne - 1 + 1e-8) continue;
                if (arrival.Distance > _support + VoxelBody.CellSize) continue;
                _pulses.Add(new(arrival, arrivalTime, arrivalTime + arrival.Duration));
                if (solid(arrival.Cell))
                {
                    if (!_blocked.TryGetValue(arrival.Cell, out var blocked)) _blocked[arrival.Cell] = blocked = [];
                    blocked.Add((arrival, arrivalTime + arrival.Duration));
                }
                else
                    Spread(arrival, arrivalTime, solid);
            }
            scheduled.RemoveRange(remaining, scheduled.Count - remaining);
            if (remaining != 0) break;
            _bins.Dequeue();
            _pending.Remove(bin);
            _freeBins.Push(scheduled);
        }

        int retained = 0;
        for (int i = 0; i < _pulses.Count; i++)
        {
            Pulse pulse = _pulses[i];
            double overlap = Math.Max(0, Math.Min(end, pulse.End) - Math.Max(_time, pulse.Start));
            if (overlap > 0)
            {
                Arrival arrival = pulse.Arrival;
                double attenuation = 1 / (1 + arrival.Distance / 4);
                double pressure = arrival.Wave.Strength * attenuation * attenuation * attenuation * overlap / dt;
                // The first cell is isotropic; nearby samples acquire their outward load
                // from the neighboring cells as the front expands.
                dvec3 direction = TravelDirection(arrival);
                ref Load load = ref CollectionsMarshal.GetValueRefOrAddDefault(_field, arrival.Cell, out _);
                load += new Load(pressure, pressure * direction);
                _fieldMin = ivec3.Min(_fieldMin, arrival.Cell);
                _fieldMax = ivec3.Max(_fieldMax, arrival.Cell);
            }
            if (pulse.End > end) _pulses[retained++] = pulse;
        }
        _pulses.RemoveRange(retained, _pulses.Count - retained);
        _time = end;
        if (!Active)
        {
            _blocked.Clear();
            ReleaseWaves();
        }
    }

    private void ReleaseWaves()
    {
        foreach (Wave wave in _waves) wave.Release();
        _waves.Clear();
    }

    /// <summary>A broken barrier releases the remaining pulse through the newly opened air.</summary>
    public void ReleaseOpenCells(Func<ivec3, bool> solid)
    {
        // A chain reaction can enlarge the support after Advance and before releasing
        // broken barriers. Grow before those releases touch the new outer cells.
        int radius = (int)Math.Ceiling((_support + VoxelBody.CellSize) / VoxelBody.CellSize) + 2;
        foreach (Wave wave in _waves) wave.Grow(radius);
        _opened.Clear();
        foreach (var (cell, arrivals) in _blocked)
        {
            if (solid(cell)) continue;
            foreach (var (arrival, end) in arrivals)
                // A wall broken long after the pulse passed does not create fresh energy.
                if (end > _time)
                    Spread(arrival with { Duration = end - _time }, _time, solid);
            _opened.Add(cell);
        }
        foreach (ivec3 cell in _opened) _blocked.Remove(cell);
        if (!Active) _blocked.Clear();
    }

    /// <summary>Conservative support bound, including interpolation and source injection.</summary>
    internal bool MayReach(dvec3 center, double radius)
    {
        dvec3 closest = dvec3.Clamp(center, _sourceMin, _sourceMax);
        double reach = _support + 3 * VoxelBody.CellSize + radius;
        if ((center - closest).LengthSqr <= reach * reach) return true;
        // A final outgoing pulse can trigger another TNT after its queue has drained.
        // Its current-step loads still apply even though Emit starts a new source set.
        return MayLoad(center, radius);
    }

    /// <summary>Current-step loads only; avoids scanning objects ahead of the front.</summary>
    internal bool MayLoad(dvec3 center, double radius)
    {
        if (_field.Count == 0) return false;
        // Trilinear sampling has a one-cell support in EACH axis, including corners.
        dvec3 closest = dvec3.Clamp(center, (new dvec3(_fieldMin) - .5) * VoxelBody.CellSize,
            (new dvec3(_fieldMax) + 1.5) * VoxelBody.CellSize);
        return (center - closest).LengthSqr <= radius * radius;
    }

    public Load Sample(dvec3 position)
    {
        dvec3 p = position / VoxelBody.CellSize - .5;
        ivec3 low = (ivec3)dvec3.Floor(p);
        dvec3 f = p - new dvec3(low);
        if (f == dvec3.Zero) return _field.GetValueOrDefault(low);
        Load result = default;
        for (int z = 0; z <= 1; z++)
        for (int y = 0; y <= 1; y++)
        for (int x = 0; x <= 1; x++)
        {
            double weight = (x == 0 ? 1 - f.x : f.x) * (y == 0 ? 1 - f.y : f.y) * (z == 0 ? 1 - f.z : f.z);
            if (weight != 0) result += _field.GetValueOrDefault(low + new ivec3(x, y, z)) * weight;
        }
        return result;
    }

    private static dvec3 TravelDirection(Arrival arrival)
    {
        if (arrival.Distance == 0) return dvec3.Zero;
        dvec3 incoming = new dvec3(arrival.Cell - arrival.From).Normalized;
        double Distance(ivec3 offset)
        {
            ref Visit visit = ref arrival.Wave.At(arrival.Cell + offset);
            return visit.TimePlusOne != 0 ? visit.Distance
                : arrival.Distance + VoxelBody.CellSize * dvec3.Dot(incoming, new dvec3(offset));
        }
        // The travel-time gradient combines equal-cost routes. Picking the last edge of
        // Dijkstra's path instead would bias momentum toward the queue's tie-break order.
        dvec3 gradient = new(Distance(ivec3.UnitX) - Distance(-ivec3.UnitX),
            Distance(ivec3.UnitY) - Distance(-ivec3.UnitY),
            Distance(ivec3.UnitZ) - Distance(-ivec3.UnitZ));
        return gradient.LengthSqr > 1e-12 ? gradient.Normalized : incoming;
    }

    private void Spread(Arrival arrival, double time, Func<ivec3, bool> solid)
    {
        // Six face probes serve all 26 edges. Previously each diagonal repeated the
        // same dictionary/solid tests, including tests for already visited neighbors.
        int blockedFaces = 0;
        if (solid(arrival.Cell - ivec3.UnitX)) blockedFaces |= 1;
        if (solid(arrival.Cell + ivec3.UnitX)) blockedFaces |= 2;
        if (solid(arrival.Cell - ivec3.UnitY)) blockedFaces |= 4;
        if (solid(arrival.Cell + ivec3.UnitY)) blockedFaces |= 8;
        if (solid(arrival.Cell - ivec3.UnitZ)) blockedFaces |= 16;
        if (solid(arrival.Cell + ivec3.UnitZ)) blockedFaces |= 32;
        int index = arrival.Wave.Index(arrival.Cell);
        for (int i = 0; i < Neighbors.Length; i++)
        {
            var (offset, length, faces) = Neighbors[i];
            // Do not cut diagonally through the edge/corner of an intact wall.
            if ((blockedFaces & faces) != 0) continue;
            Arrival next = new(arrival.Wave, arrival.Cell + offset, arrival.Distance + length, arrival.Cell, arrival.Duration);
            Enqueue(next, time + length / WaveSpeed, index + arrival.Wave.NeighborIndices[i]);
        }
    }

    private void Enqueue(Arrival arrival, double time, int index = -1)
    {
        // Avoid inserting the discarded outer shell into either visits or the heap.
        if (arrival.Distance > _support + VoxelBody.CellSize) return;
        // A shorter route opened late must not replace a wave already arriving sooner
        // around the obstacle. Attenuation still uses path length, not waiting time.
        ref Visit visit = ref arrival.Wave.At(index < 0 ? arrival.Wave.Index(arrival.Cell) : index);
        if (visit.TimePlusOne != 0 && visit.TimePlusOne - 1 <= time + 1e-8) return;
        visit = new() { Distance = arrival.Distance, TimePlusOne = time + 1 };
        long bin = (long)Math.Floor(time / BinDuration);
        if (!_pending.TryGetValue(bin, out var scheduled))
        {
            scheduled = _freeBins.TryPop(out var reused) ? reused : [];
            _pending.Add(bin, scheduled);
            _bins.Enqueue(bin, bin);
        }
        scheduled.Add(new(arrival, time));
    }

    private static (ivec3, double, int)[] CreateNeighbors()
    {
        List<(ivec3, double, int)> result = [];
        for (int z = -1; z <= 1; z++)
        for (int y = -1; y <= 1; y++)
        for (int x = -1; x <= 1; x++)
            if (x != 0 || y != 0 || z != 0)
            {
                int faces = Math.Abs(x) + Math.Abs(y) + Math.Abs(z) == 1 ? 0
                    : (x < 0 ? 1 : x > 0 ? 2 : 0) | (y < 0 ? 4 : y > 0 ? 8 : 0) | (z < 0 ? 16 : z > 0 ? 32 : 0);
                result.Add((new(x, y, z), Math.Sqrt(x * x + y * y + z * z) * VoxelBody.CellSize, faces));
            }
        return [.. result];
    }
}
