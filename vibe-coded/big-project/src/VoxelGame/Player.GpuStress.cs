using GlmSharp;
using MYT.Kit.Structural;

namespace VoxelGame;

internal sealed partial class World
{
    private readonly Func<double, GpuStressLattice> _createGpuStress;
    private GpuStressLattice? _playerGpuStress;
    private double _playerGpuStressHealth = -1;
    private readonly dvec3[] _playerGpuForces = new dvec3[Player.Cells.Length];
    private readonly dvec3[] _playerGpuMoments = new dvec3[Player.Cells.Length];

    private void ApplyGpuPlayerStress(Player player)
    {
        if (player.ContactLoads.Count == 0) { Array.Clear(player.Stresses); return; }
        double cellMass = Player.Mass / Player.Cells.Length;
        bool changed = _playerGpuStress == null || _playerGpuStressHealth != player.Health;
        if (_playerGpuStress == null)
        {
            _playerGpuStress = _createGpuStress(Player.Width);
            _playerGpuStress.Tolerance = PlayerStressTolerance;
            _playerGpuStress.IterationsPerUpdate = 4;
            _playerGpuStress.SetCells(Player.Cells,
                Enumerable.Repeat(Player.Material.Elastic(player.Health), Player.Cells.Length).ToArray(),
                Enumerable.Repeat(cellMass, Player.Cells.Length).ToArray(), new bool[Player.Cells.Length]);
        }
        if (_playerGpuStressHealth != player.Health)
        {
            CellMaterial material = Player.Material.Elastic(player.Health);
            for (int i = 0; i < Player.Cells.Length; i++) _playerGpuStress.SetMaterial(i, material);
            _playerGpuStressHealth = player.Health;
        }
        double[] masses = Enumerable.Repeat(cellMass, Player.Cells.Length).ToArray();
        dvec3[] forces = new dvec3[Player.Cells.Length], moments = new dvec3[Player.Cells.Length];
        for (int i = 0; i < Player.Cells.Length; i++)
        {
            var (force, moment) = player.ContactLoads.GetValueOrDefault(Player.Cells[i]);
            forces[i] = cellMass * player.DirectionToLocal(PlayerGravity) + force;
            moments[i] = moment;
        }
        RigidMass.Of(Player.Cells, masses, Player.Width).Relieve(Player.Cells, masses, forces, moments, dvec3.Zero);
        for (int i = 0; i < forces.Length; i++)
            changed |= (forces[i] - _playerGpuForces[i]).Length > PlayerStressTolerance * (1 + forces[i].Length)
                || (moments[i] - _playerGpuMoments[i]).Length > PlayerStressTolerance * (1 + moments[i].Length);
        if (changed)
        {
            var loads = new (ivec3, dvec3, dvec3)[Player.Cells.Length];
            for (int i = 0; i < loads.Length; i++) loads[i] = (Player.Cells[i], forces[i], moments[i]);
            _playerGpuStress.SetLoads(dvec3.Zero, loads);
            // Health participates in impact loading in this same step. Delaying
            // this tiny solve changes collision damage; unchanged loads reuse it.
            _playerGpuStress.Solve(PlayerStressIterations);
            forces.CopyTo(_playerGpuForces, 0); moments.CopyTo(_playerGpuMoments, 0);
        }
        _playerGpuStress.CopyStresses(player.Stresses);
        double ratio = player.Stresses.Max(Player.Material.ElasticLimit(player.Health).Ratio);
        if (ratio > 1)
            DamagePlayer(Step * StressDamageRate * (ratio * ratio - 1), $"a load at {ratio:F2}x its elastic limit");
    }
}
