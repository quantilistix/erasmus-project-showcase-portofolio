using GlmSharp;
using MYT.Kit.Structural;
using VoxelGame;

namespace VoxelTests;

internal static partial class ImpactTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("impact-contact-patches", ContactPatches),
        ("impact-load-propagation", LoadPropagation),
        ("impact-shared-load", SharedLoad)
        ,("gpu-impact-analysis", GpuAnalysis)
    ];

    private static bool Check(string label, bool passed)
    {
        Console.WriteLine($"   {(passed ? "ok " : "BAD")} {label}");
        return passed;
    }

    // A free cell has no internal lattice strain. Its half-cell compliance on each
    // side gives an exact reference for compression, damage sharing and energy accounting.
    private static ImpactSide Cell(int id, dvec3 inward, double area, double force = 1)
    {
        return new ImpactSide(id, 1, [ivec3.Zero], [new CellMaterial(1000, 400)],
            [1], [false], true, [new ElasticLimits(100, 100, 100)],
            [default], [1000], [false],
            ImpactLoading.Normalize([new ImpactContact(0, force * inward, dvec3.Zero, inward, area)]));
    }

    private static ImpactOutcome Hit(double area, double energy = 10, double forceA = 1, double forceB = 1)
        => ImpactAnalysis.Run(new ImpactJob(0, energy,
            [Cell(1, dvec3.UnitX, area, forceA), Cell(2, -dvec3.UnitX, area, forceB)]));

    private static double Damage(ImpactOutcome hit, int side)
        => hit.Sides[side].Damage.Sum(d => d.Damage);

    private static bool ContactPatches()
    {
        double face = ImpactLoading.AreaFraction(dvec3.UnitX, -dvec3.UnitX);
        double edge = ImpactLoading.AreaFraction(dvec3.UnitX, new dvec3(1, 1, 0));
        double corner = ImpactLoading.AreaFraction(dvec3.UnitX, new dvec3(1, 1, 1));
        bool ok = Check("face > edge > corner footprint", face > edge && edge > corner);
        ok &= Check("footprint is independent of body order",
            corner == ImpactLoading.AreaFraction(new dvec3(1, 1, 1), dvec3.UnitX));
        ok &= Check("orientation changes continuously near a face",
            ImpactLoading.AreaFraction(dvec3.UnitX, new dvec3(1, 1e-6, 0)) > 0.999);
        ImpactOutcome f = Hit(face), e = Hit(edge), c = Hit(corner);
        Console.WriteLine($"   damage face/edge/corner: {Damage(f, 0):F4}/{Damage(e, 0):F4}/{Damage(c, 0):F4}");
        ok &= Check("sharper contacts cause more local damage at equal energy",
            Damage(c, 0) > Damage(e, 0) && Damage(e, 0) > Damage(f, 0));
        ok &= Check("gentle face contact stays elastic", Damage(Hit(face, 1), 0) == 0);
        foreach (ImpactOutcome hit in new[] { f, e, c })
        {
            ok &= Check("both bodies share damage and stay within the energy budget",
                Math.Abs(Damage(hit, 0) - Damage(hit, 1)) < 1e-10 &&
                hit.RetainedEnergy >= 0 && hit.RetainedEnergy <= 10 &&
                Math.Abs(hit.RetainedEnergy + 2.5 * (Damage(hit, 0) + Damage(hit, 1)) - 10) < 1e-10);
        }
        return ok;
    }

    private static bool SharedLoad()
    {
        ImpactOutcome reference = Hit(1);
        ImpactOutcome unequal = Hit(1, forceA: 100, forceB: 0.01);
        bool ok = Check("different incoming weight scales cannot shield either body",
            Math.Abs(Damage(reference, 0) - Damage(unequal, 0)) < 1e-10 &&
            Math.Abs(Damage(reference, 1) - Damage(unequal, 1)) < 1e-10);

        ImpactContact[] contacts = ImpactLoading.Normalize([
            new ImpactContact(0, 2 * dvec3.UnitX, 4 * dvec3.UnitZ, dvec3.UnitX),
            new ImpactContact(1, 6 * dvec3.UnitX, 12 * dvec3.UnitZ, dvec3.UnitX)]);
        ok &= Check("load normalization preserves its distribution and torque",
            Math.Abs(contacts.Sum(c => c.Force.Length) - 1) < 1e-10 &&
            Math.Abs(contacts[1].Force.x / contacts[0].Force.x - 3) < 1e-10 &&
            Math.Abs(contacts.Sum(c => c.Moment.z) - 2) < 1e-10);
        ok &= Check("empty load stays empty", ImpactLoading.Normalize([]).Length == 0);
        return ok;
    }

    private static bool LoadPropagation()
    {
        ImpactOutcome Beam(double area)
        {
            ImpactSide beam = new(1, 1, [ivec3.Zero, new ivec3(1, 0, 0)],
                [new CellMaterial(1000, 400), new CellMaterial(1000, 400)],
                [1, 1], [true, false], false,
                [new ElasticLimits(10, 100, 1000), new ElasticLimits(10, 100, 1000)],
                [default, default], [1000, 1000], [false, false],
                [new ImpactContact(1, dvec3.UnitY, dvec3.Zero, dvec3.UnitY, area)]);
            return ImpactAnalysis.Run(new ImpactJob(0, 1, [beam]));
        }

        double Root(ImpactOutcome hit) => hit.Sides[0].Damage.Where(d => d.Cell == ivec3.Zero).Sum(d => d.Damage);
        double face = Root(Beam(1)), corner = Root(Beam(1.0 / 9.0));
        Console.WriteLine($"   remote root damage face/corner: {face:F4}/{corner:F4}");
        return Check("broad impact transmits more damage to the beam root", face > corner && corner > 0);
    }
}
