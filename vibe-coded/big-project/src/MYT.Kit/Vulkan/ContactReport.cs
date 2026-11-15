using GlmSharp;

namespace MYT.Kit.Vulkan;


/// <summary>
/// A contact that pushed during one substep of the last <see cref="PhysicsSystem.Update"/>,
/// listed in <see cref="PhysicsSystem.ContactReports"/> when
/// <see cref="PhysicsSettings.ReportContacts"/> is on. The force on
/// <see cref="BodyA"/> is (<see cref="NormalImpulse"/> · <see cref="Normal"/> +
/// <see cref="FrictionImpulse"/>) / <see cref="Duration"/>; <see cref="BodyB"/> feels the opposite.
/// </summary>
/// <param name="BodyA">One side of the contact.</param>
/// <param name="BodyB">The other side.</param>
/// <param name="Point">World point of contact at the start of the substep, on the surface of a side that moves.</param>
/// <param name="Normal">Unit contact normal, pointing from <paramref name="BodyB"/> to <paramref name="BodyA"/>.</param>
/// <param name="NormalImpulse">Impulse pushing <paramref name="BodyA"/> along the normal during the substep, N·s; never negative.</param>
/// <param name="FrictionImpulse">Friction impulse on <paramref name="BodyA"/> during the substep, N·s, perpendicular to the normal.</param>
/// <param name="ApproachSpeed">How fast the sides were closing along the normal when the substep began, m/s; negative when separating. Large for the substep of an impact.</param>
/// <param name="Duration">Length of the substep, s.</param>
public readonly record struct ContactReport
(
    PhysicsBody BodyA,
    PhysicsBody BodyB,
    dvec3 Point,
    dvec3 Normal,
    double NormalImpulse,
    dvec3 FrictionImpulse,
    double ApproachSpeed,
    double Duration
);
