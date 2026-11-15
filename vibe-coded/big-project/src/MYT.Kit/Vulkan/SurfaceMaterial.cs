namespace MYT.Kit.Vulkan;


/// <summary>How a stretch of surface touches others: what it grips with and how it bounces.</summary>
/// <param name="Friction">Coulomb friction coefficient. A contact uses the geometric mean of both sides' values.</param>
/// <param name="Restitution">
/// Bounciness from 0 to 1. A contact mixes both sides' values as
/// <see cref="PhysicsSettings.RestitutionMixing"/> says.
/// </param>
public readonly record struct SurfaceMaterial(double Friction, double Restitution);
