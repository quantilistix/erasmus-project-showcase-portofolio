using GlmSharp;

namespace VoxelGame;

internal sealed partial class World
{
    private readonly Func<GpuBlastSimulation> _createGpuBlasts;
    private GpuBlastSimulation? _gpuBlasts;
    internal bool GpuBlastsEnabled { get; set; } = GpuSimulationOptions.Enabled("MYT_GPU_BLASTS");
    internal bool GpuDamageEnabled { get; set; } = GpuSimulationOptions.Enabled("MYT_GPU_DAMAGE");
    internal string GpuBlastStatistics => _gpuBlasts == null ? "" : $" blastGPU {_gpuBlasts.PressureCells} cells {_gpuBlasts.ResidentChunks} chunks kernel {_gpuBlasts.GpuMilliseconds:F2} ms total {_gpuBlasts.AdvanceMilliseconds:F1} ms wait {_gpuBlasts.WaitMilliseconds:F1} ms upload {_gpuBlasts.UploadedBytes / 1024} KB";
    internal void PrepareGpuBlasts()
    {
        if (GpuBlastsEnabled || GpuDamageEnabled) _gpuBlasts ??= _createGpuBlasts();
    }
    // Nominal reach of one TNT in open air; overlapping pulses extend this naturally.
    public const double ExplosionRadius = 16 * VoxelBody.CellSize;
    internal BlastPressure Pressure { get; } = new();
    internal bool SpatialBlastBounds { get; set; } = true;
    private readonly Dictionary<ivec3, bool> _pressureSolids = [];
    private (VoxelObject Body, dvec3 Center, double Radius)[] _pressureBodies = [];

    /// <summary>Consumes this TNT immediately; its traveling pressure can trigger other TNT.</summary>
    public bool Detonate(VoxelBody body, ivec3 cell)
    {
        if (body.Grid[cell].Type != BlockType.Tnt) return false;
        dvec3 center = body.ToWorld(VoxelBody.CellCenter(cell));
        RemoveBlock(body, cell);
        if (GpuBlastsEnabled)
            (_gpuBlasts ??= _createGpuBlasts()).Emit(center);
        else
            Pressure.Emit(center);
        // All effects exist before pressure is applied, including this TNT's own dust.
        Particles.EmitExplosion(center, this);
        return true;
    }

    internal void AdvanceExplosions(double dt)
    {
        if (dt > 0 && _gpuBlasts is { Active: true })
            AdvanceGpuExplosions(dt);
        if (!(dt > 0) || (!Pressure.Active && Pressure.Cells.Count == 0)) return;
        Prof.Time("blast.solids", PreparePressureSolids);
        Prof.Time("blast.propagate", () => Pressure.Advance(dt, IsPressureSolid));

        // Snapshot coordinates before damage changes topology or triggers more TNT.
        long damageStart = Prof.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var affected = PressureTargets().ToArray();
        foreach (var (body, cell) in affected)
        {
            Voxel voxel = body.Grid[cell];
            dvec3 point = body.ToWorld(VoxelBody.CellCenter(cell));
            BlastPressure.Load load = Pressure.Sample(point);
            if (load.Pressure < BlastPressure.Threshold) continue;
            if (voxel.Type == BlockType.Tnt)
            {
                Detonate(body, cell);
                continue;
            }
            if (!Blocks.IsSolid(voxel.Type)) continue;
            Tool blast = new(BlastPressure.PeakStress * load.Pressure, 24 * load.Pressure);
            dvec3 direction = load.Difference.LengthSqr > 1e-12 ? load.Difference.Normalized : dvec3.UnitZ;
            dvec3 localDirection = body.Orientation.Conjugate * direction;
            double dominant = Math.Max(Math.Abs(localDirection.x), Math.Max(Math.Abs(localDirection.y), Math.Abs(localDirection.z)));
            // Damage and chips originate on the face exposed to the incoming pressure,
            // not in the block's center or on its shielded far side.
            dvec3 surface = VoxelBody.CellCenter(cell) - localDirection * (.5 * VoxelBody.CellSize / dominant);
            Damage(body, cell, blast.Damage(voxel.Material, voxel.Health, dt / BlastPressure.PulseDuration), "explosion",
                new BlockDamageSite(surface, -localDirection));
        }

        if (Prof.On) Prof.Add("blast.damage", System.Diagnostics.Stopwatch.GetElapsedTime(damageStart).TotalMilliseconds);
        bool topologyChanged = _topologyChanged.Count != 0;
        Prof.Time("blast.topology", ApplyTopologyChanges);
        if (topologyChanged) PreparePressureSolids();
        Pressure.ReleaseOpenCells(IsPressureSolid);
        // Re-sample after detachment: surviving blocks and all new bodies receive the
        // combined load, not a separately prescribed velocity for each TNT.
        foreach (var (body, cell) in PressureTargets())
        {
            if (body is not VoxelObject target) continue;
            Voxel voxel = body.Grid[cell];
            if (!Blocks.IsSolid(voxel.Type)) continue;
            dvec3 point = target.ToWorld(VoxelBody.CellCenter(cell));
            BlastPressure.Load load = Pressure.Sample(point);
            if (load.Pressure < BlastPressure.Threshold) continue;
            double fraction = BlockShapes.HasShape(voxel.Type) ? BlockShapes.Parts(voxel).Count() / 8.0 : 1.0;
            double area = VoxelBody.CellSize * VoxelBody.CellSize * Math.Pow(fraction, 2.0 / 3);
            target.ApplyMiningImpulse(load.Momentum(area, dt), point);
        }
        if (Player is { Dead: false } player)
        {
            BlastPressure.Load load = Pressure.Sample(player.Body.WorldCenterOfMass);
            if (load.Pressure >= BlastPressure.Threshold)
            {
                player.Body.LinearVelocity += load.Momentum(Player.Width * Player.Height, dt) / Player.Mass;
                DamagePlayer(42 * load.Pressure * dt / BlastPressure.PulseDuration, "TNT explosion");
            }
        }
        foreach (DroppedItem item in _items)
        {
            BlastPressure.Load load = Pressure.Sample(item.Position);
            if (load.Pressure < BlastPressure.Threshold) continue;
            double area = Math.PI * item.Radius * item.Radius;
            dvec3 kick = load.Momentum(area, dt) / item.Stack.Info.Mass;
            if (item.Body != null) item.Body.LinearVelocity += kick;
            else item.PendingLinearVelocity += kick;
        }
        // Damage has already spawned chips/drops and chain reactions have spawned effects.
        Prof.Time("blast.particles", () => Particles.ApplyPressure(Pressure, dt));
        if (!Pressure.Active)
        {
            _pressureSolids.Clear();
            _pressureBodies = [];
        }
    }

    private IEnumerable<(VoxelBody Body, ivec3 Cell)> PressureTargets()
    {
        foreach (ivec3 cell in Pressure.Cells.Keys)
            if (Map.Grid.IsOccupied(cell)) yield return (Map, cell);
        foreach (VoxelObject body in _objects)
        {
            if (SpatialBlastBounds && !HasPendingTopology(body) && !Pressure.MayLoad(body.ToWorld(body.Bounds.Center), body.Bounds.Radius)) continue;
            // Objects are sparse; use occupied cells rather than probing an empty cube.
            foreach (var (cell, _) in body.Grid.Occupied())
                if (Pressure.Sample(body.ToWorld(VoxelBody.CellCenter(cell))).Pressure > 0)
                    yield return (body, cell);
        }
    }

    private bool IsPressureSolid(ivec3 cell)
    {
        if (_pressureSolids.TryGetValue(cell, out bool solid)) return solid;
        solid = Blocks.IsSolid(Map.Grid[cell].Type);
        if (!solid)
        {
            dvec3 point = VoxelBody.CellCenter(cell);
            foreach (var (body, center, radius) in _pressureBodies)
            {
                if ((point - center).LengthSqr > radius * radius) continue;
                ivec3 local = (ivec3)dvec3.Floor(body.ToLocal(point) / VoxelBody.CellSize);
                if (Blocks.IsSolid(body.Grid[local].Type)) { solid = true; break; }
            }
        }
        _pressureSolids[cell] = solid;
        return solid;
    }

    private void PreparePressureSolids()
    {
        _pressureSolids.Clear();
        _pressureBodies = _objects.Where(body => !SpatialBlastBounds || HasPendingTopology(body)
            || Pressure.MayReach(body.ToWorld(body.Bounds.Center), body.Bounds.Radius))
            .Select(body => (body, body.ToWorld(body.Bounds.Center),
            HasPendingTopology(body) ? double.PositiveInfinity : body.Bounds.Radius + 1e-7)).ToArray();
    }
}
