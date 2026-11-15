using GlmSharp;
using VoxelGame;

namespace VoxelTests;

internal static partial class WorldTests
{
    private static bool ParticleAir()
    {
        dvec3 gravity = new(0, 0, -9.81);
        dvec3 light = new(10, 0, 0), heavy = light;
        BlockParticles.AirMotion(ref light, .9, 1, 1, dvec3.Zero, 1);
        BlockParticles.AirMotion(ref heavy, 2600, 1, 1, dvec3.Zero, 1);
        bool ok = Check("air drag slows light particles more than heavy particles",
            light.x > 0 && light.x < heavy.x && heavy.x < 10 ? 1 : 0, 1, 1);

        dvec3 neutral = dvec3.Zero, dust = dvec3.Zero, chip = dvec3.Zero;
        BlockParticles.AirMotion(ref neutral, BlockParticles.AirDensity, 1, 1, gravity, 1);
        dvec3 rise = BlockParticles.AirMotion(ref dust, .9, 1, 1, gravity, 1);
        dvec3 fall = BlockParticles.AirMotion(ref chip, 2600, 1, 1, gravity, 1);
        ok &= Check("equal air density gives neutral buoyancy", neutral.Length, 0, 0)
            & Check("dust floats upward", rise.z, .5, 1)
            & Check("heavy chips fall", fall.z, -5, -4);

        dvec3 once = new(30, -20, -10), stepped = once;
        dvec3 oneTravel = BlockParticles.AirMotion(ref once, .9, 1, 1, gravity, 1);
        dvec3 stepTravel = dvec3.Zero;
        for (int i = 0; i < 100; i++)
            stepTravel += BlockParticles.AirMotion(ref stepped, .9, 1, 1, gravity, .01);
        ok &= Check("air velocity is independent of frame rate", (once - stepped).Length, 0, 1e-10)
            & Check("air displacement is independent of frame rate", (oneTravel - stepTravel).Length, 0, 1e-10);

        double[] masses = Enumerable.Range(0, 1000).Select(i => BlockParticles.DustMass((i + .5) / 1000)).ToArray();
        ok &= Check("cloud masses remain positive and buoyant", masses.All(m => m > 0 && m < BlockParticles.AirDensity) ? 1 : 0, 1, 1)
            & Check("rare light clouds form a continuous mass tail", masses.Count(m => m < .25), 15, 30)
            & Check("cloud masses vary continuously", masses.Zip(masses.Skip(1)).All(p => p.First < p.Second) ? 1 : 0, 1, 1);
        dvec3 impulse = new BlastPressure.Load(1, dvec3.UnitX).Momentum(.005, World.Step);
        ok &= Check("same pressure gives lighter clouds more velocity", (impulse / masses[0]).x > (impulse / masses[^1]).x ? 1 : 0, 1, 1);

        dvec3 fast = new(1000, 0, 0);
        dvec3 travel = BlockParticles.AirMotion(ref fast, .001, 1, 1, gravity, 5);
        return ok & Check("strong drag stays finite without reversing horizontal velocity",
            double.IsFinite(travel.Length) && double.IsFinite(fast.Length) && fast.x >= 0 && fast.x < 1000 ? 1 : 0, 1, 1);
    }
}
