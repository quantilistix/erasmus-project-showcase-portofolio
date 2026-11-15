using System.Runtime.InteropServices;

namespace MYT.Kit.Vulkan;


/// <summary>
/// A spring between two nodes of one <see cref="SoftBody"/>. Matches the GLSL Spring struct.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 40)]
public struct Spring
{
    public uint Node1;
    public uint Node2;
    public double RestLength;
    /// <summary>
    /// Stiffness in N/m. <see cref="double.PositiveInfinity"/> makes the spring a hard distance
    /// constraint, enforced exactly by AVBD's augmented Lagrangian instead of by stiffness.
    /// </summary>
    public double Stiffness;
    /// <summary>Rayleigh damping coefficient k_d in seconds (VBD Eq. 10).</summary>
    public double Damping;
    /// <summary>Force in N at which the spring breaks for good. <see cref="double.MaxValue"/> never breaks.</summary>
    public double Durability;
}
