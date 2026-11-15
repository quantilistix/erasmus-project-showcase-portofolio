using GlmSharp;

using MYT.Kit.Vulkan;

namespace VoxelGame;


/// <summary>
/// An item lying in the world, mined out of a block or dropped by the player: a small rigid
/// body of the item's shape that cannot break and breaks nothing, and goes into the player's
/// inventory when it touches the player (<see cref="World"/> moves it and picks it up).
/// It stays in the world until collected; distance only parks its simulation.
/// </summary>
internal sealed class DroppedItem(ItemStack stack, double radius)
{
    public ItemStack Stack { get; set; } = stack;

    /// <summary>How far (m) its shape reaches from its origin, at most.</summary>
    public double Radius { get; } = radius;

    /// <summary>Its body in the physics; null while it is parked, out of the simulation.</summary>
    public RigidBody? Body { get; set; }

    public bool Parked { get; set; }

    /// <summary>Its pose and motion while it has no body.</summary>
    public dvec3 PendingPosition { get; set; }
    public dquat PendingOrientation { get; set; } = dquat.Identity;
    public dvec3 PendingLinearVelocity { get; set; }
    public dvec3 PendingAngularVelocity { get; set; }

    /// <summary>Its pose when the last step began, which it is drawn between.</summary>
    public dvec3 StepStartPosition { get; set; }
    public dquat StepStartOrientation { get; set; } = dquat.Identity;

    /// <summary>Seconds it has been simulated: the clock stops while it is parked.</summary>
    public double Age { get; set; }

    /// <summary>Seconds left before the player can pick it up, so one it drops does not come straight back.</summary>
    public double PickupDelay { get; set; }

    /// <summary>The middle of its shape.</summary>
    public dvec3 Position => Body?.Position ?? PendingPosition;
    public dquat Orientation => Body?.Orientation ?? PendingOrientation;
    public dvec3 LinearVelocity => Body?.LinearVelocity ?? PendingLinearVelocity;
    public dvec3 AngularVelocity => Body?.AngularVelocity ?? PendingAngularVelocity;

    /// <summary>A box around it in the world, m.</summary>
    public (dvec3 Low, dvec3 High) Bounds => (Position - Radius, Position + Radius);
}
