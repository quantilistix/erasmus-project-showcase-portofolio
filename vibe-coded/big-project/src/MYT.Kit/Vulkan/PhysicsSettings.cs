using GlmSharp;

namespace MYT.Kit.Vulkan;


/// <summary>How a contact makes one restitution of both sides' (<see cref="SurfaceMaterial.Restitution"/>).</summary>
public enum RestitutionMixing
{
    /// <summary>The larger: a bouncy body bounces off anything.</summary>
    Maximum,
    /// <summary>
    /// The geometric mean: two surfaces of one material bounce with its restitution, and a
    /// surface of restitution 0, one that soaks up impacts such as soil, stops any bounce.
    /// </summary>
    GeometricMean
}


/// <summary>
/// Tunable simulation parameters of a <see cref="PhysicsSystem"/>. Changes take effect on the next
/// <see cref="PhysicsSystem.Update"/>, except <see cref="MaxContacts"/> and
/// <see cref="MaxContactReports"/>, which are read when the system (re)builds its buffers after
/// bodies are added or removed.
/// </summary>
public sealed class PhysicsSettings
{
    public dvec3 Gravity { get; set; } = new(0, 0, -9.81);

    /// <summary>Solver iterations per substep. More converge further; AVBD stays stable with few.</summary>
    public uint Iterations { get; set; } = 10;

    /// <summary>Each <see cref="PhysicsSystem.Update"/> is split into this many equal steps.</summary>
    public uint Substeps { get; set; } = 1;

    /// <summary>
    /// Continuous collision passes per substep: the first right after the first iteration, the
    /// rest spread evenly through the iterations. Each one sweeps every primitive from the start
    /// of the step to the solver's current positions and adds the contacts that motion makes.
    /// With 0, the initial detection sweeps the inertial prediction instead.
    /// </summary>
    public uint ContinuousCollisionPasses { get; set; } = 1;

    /// <summary>
    /// Distance in m at which contacts are created ahead of an actual touch, on top of how far
    /// the primitives move during the step.
    /// </summary>
    public double ContactMargin { get; set; } = 0.01;

    /// <summary>
    /// Depth in m a vertex may start a step behind a surface and still count as touching it from
    /// the front. Must be smaller than the thinnest wall in the scene.
    /// </summary>
    public double PenetrationTolerance { get; set; } = 1e-3;

    /// <summary>
    /// Deepest penetration in m that an existing contact keeps pushing out of. Beyond it the
    /// contact is dropped, which avoids snagging on far-away faces after a body tunnels through.
    /// </summary>
    public double MaxPenetrationDepth { get; set; } = 0.25;

    /// <summary>Rigid body impacts slower than this (m/s) do not bounce, so resting contact stays at rest.</summary>
    public double MinimumBounceSpeed { get; set; } = 0.5;

    /// <summary>How a contact makes one restitution of both sides'.</summary>
    public RestitutionMixing RestitutionMixing { get; set; } = RestitutionMixing.Maximum;

    /// <summary>
    /// AVBD α (Eq. 18): fraction of a penetration present at the start of a step that the step
    /// leaves alone. 1 - α of it is pushed out per step, gently instead of in one violent jump.
    /// </summary>
    public double Alpha { get; set; } = 0.95;

    /// <summary>AVBD β (Eq. 12): how fast constraint stiffness ramps up with the remaining error, N/m².</summary>
    public double Beta { get; set; } = 1e5;

    /// <summary>AVBD γ (Eq. 19): fraction of last step's stiffness and multipliers a contact starts from.</summary>
    public double Gamma { get; set; } = 0.99;

    /// <summary>AVBD k_start: stiffness in N/m new contacts and hard springs start from.</summary>
    public double StartStiffness { get; set; } = 1e4;

    /// <summary>Upper bound on any constraint stiffness, N/m, for numerical safety.</summary>
    public double MaxStiffness { get; set; } = 1e10;

    /// <summary>Upper bound on any constraint force, N, for numerical safety.</summary>
    public double MaxForce { get; set; } = 1e12;

    /// <summary>Most contacts alive in one step. Contacts beyond it are dropped and counted in <see cref="PhysicsStatistics"/>.</summary>
    public uint MaxContacts { get; set; } = 32768;

    /// <summary>
    /// Whether each update lists the contacts that pushed in <see cref="PhysicsSystem.ContactReports"/>,
    /// with their impulses and impact speeds, for gameplay such as impact damage and load paths.
    /// </summary>
    public bool ReportContacts { get; set; }

    /// <summary>
    /// Updates each rigid body's GPU model matrix after simulation. Keep enabled when rendering
    /// from <see cref="RigidBody.TransformBuffer"/>; CPU-pose renderers can skip these copies.
    /// </summary>
    public bool UpdateRigidTransforms { get; set; } = true;

    /// <summary>
    /// Most contact reports one update keeps, over all its substeps. Reports beyond it are
    /// dropped and counted in <see cref="PhysicsStatistics.DroppedContactReports"/>. Read, like
    /// <see cref="MaxContacts"/>, when the system rebuilds its buffers.
    /// </summary>
    public uint MaxContactReports { get; set; } = 16384;
}
