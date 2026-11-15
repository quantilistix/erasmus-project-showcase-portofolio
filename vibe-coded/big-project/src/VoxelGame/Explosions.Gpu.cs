using System.Runtime.InteropServices;
using GlmSharp;

namespace VoxelGame;

internal sealed partial class World
{
    private readonly List<(VoxelObject Body, ivec3 Cell)> _gpuBlastReceivers = [];
    private readonly List<DroppedItem> _gpuBlastItems = [];
    private readonly List<dvec3> _gpuBlastPositions = [];

    private void AdvanceGpuExplosions(double dt)
    {
        GpuBlastSimulation simulation = _gpuBlasts!;
        var changes = Prof.Time("blast.gpu", () => simulation.Advance(this, dt));
        Prof.Time("blast.mirror", () =>
        {
            foreach (GpuBlastSimulation.VoxelChange change in changes)
            {
                if (change.Body.Grid[change.Cell] != change.Before) continue;
                if (change.Ignite) { Detonate(change.Body, change.Cell); continue; }
                dvec3 direction = change.Load.Difference.LengthSqr > 1e-12 ? change.Load.Difference.Normalized : dvec3.UnitZ;
                dvec3 local = change.Body.DirectionToLocal(direction);
                double dominant = Math.Max(Math.Abs(local.x), Math.Max(Math.Abs(local.y), Math.Abs(local.z)));
                dvec3 surface = VoxelBody.CellCenter(change.Cell) - local * (.5 * VoxelBody.CellSize / dominant);
                ApplyDamageState(change.Body, change.Cell, change.Before, change.Damage, 1.0 - change.Damage / (double)ushort.MaxValue,
                    "explosion", new(surface, -local));
            }
        });
        Prof.Time("blast.topology", ApplyTopologyChanges);

        _gpuBlastReceivers.Clear(); _gpuBlastPositions.Clear(); _gpuBlastItems.Clear();
        foreach (VoxelObject body in _objects)
        {
            if (!simulation.MayReach(body.ToWorld(body.Bounds.Center), body.Bounds.Radius)) continue;
            foreach (var (cell, voxel) in body.Grid.Occupied())
            {
                if (!Blocks.IsSolid(voxel.Type)) continue;
                _gpuBlastReceivers.Add((body, cell));
                _gpuBlastPositions.Add(body.ToWorld(VoxelBody.CellCenter(cell)));
            }
        }
        Player? player = Player is { Dead: false } live ? live : null;
        if (player != null) _gpuBlastPositions.Add(player.Body.WorldCenterOfMass);
        foreach (DroppedItem item in _items)
        {
            if (!simulation.MayReach(item.Position, item.Radius)) continue;
            _gpuBlastItems.Add(item);
            _gpuBlastPositions.Add(item.Position);
        }
        int particleStart = _gpuBlastPositions.Count;
        Particles.AppendPressurePositions(_gpuBlastPositions);
        var loads = Prof.Time("blast.sample", () => simulation.Sample(CollectionsMarshal.AsSpan(_gpuBlastPositions)));
        int index = 0;
        foreach (var (body, cell) in _gpuBlastReceivers)
        {
            BlastPressure.Load load = loads[index++];
            if (load.Pressure < BlastPressure.Threshold) continue;
            Voxel voxel = body.Grid[cell];
            double fraction = BlockShapes.HasShape(voxel.Type) ? BlockShapes.Parts(voxel).Count() / 8.0 : 1.0;
            double area = VoxelBody.CellSize * VoxelBody.CellSize * Math.Pow(fraction, 2.0 / 3);
            body.ApplyMiningImpulse(load.Momentum(area, dt), body.ToWorld(VoxelBody.CellCenter(cell)));
        }
        if (player != null)
        {
            BlastPressure.Load load = loads[index++];
            if (load.Pressure >= BlastPressure.Threshold)
            {
                player.Body.LinearVelocity += load.Momentum(Player.Width * Player.Height, dt) / Player.Mass;
                DamagePlayer(42 * load.Pressure * dt / BlastPressure.PulseDuration, "TNT explosion");
            }
        }
        foreach (DroppedItem item in _gpuBlastItems)
        {
            BlastPressure.Load load = loads[index++];
            if (load.Pressure < BlastPressure.Threshold) continue;
            dvec3 kick = load.Momentum(Math.PI * item.Radius * item.Radius, dt) / item.Stack.Info.Mass;
            if (item.Body != null) item.Body.LinearVelocity += kick;
            else item.PendingLinearVelocity += kick;
        }
        Particles.ApplyPressure(loads.AsSpan(particleStart), dt);
    }
}
