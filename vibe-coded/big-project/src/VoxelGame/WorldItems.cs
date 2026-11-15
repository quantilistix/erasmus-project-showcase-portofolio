using GlmSharp;

using MYT.Kit.Vulkan;

namespace VoxelGame;


/// <summary>
/// The world's dropped items (<see cref="DroppedItem"/>): rigid bodies among the objects that
/// collide like them but whose contacts the damage model leaves out, so they neither break
/// nor break anything. One that touches the player, once its pickup delay is over, goes into
/// the player's inventory as far as there is room. Like objects, they are parked beyond the
/// simulation distance, keeping their state until the player returns.
/// </summary>
internal sealed partial class World
{
    /// <summary>Seconds before an item mined out of a block can be picked up.</summary>
    public const double MinedPickupDelay = 0.25;
    // How an item that is not a block touches others: about wood on stone, a dull bounce.
    private static readonly SurfaceMaterial SpriteSurface = new(0.6, 0.3);

    private readonly List<DroppedItem> _items = [];
    private readonly Dictionary<PhysicsBody, DroppedItem> _itemBodies = [];
    private readonly HashSet<DroppedItem> _touchedItems = [];
    // Shaped blocks and sprites keep their own meshes; full blocks share one cube.
    // Sharing each mesh lets the physics cook it once.
    private readonly Dictionary<ItemType, Mesh3D> _spriteShapes = [];
    private Mesh3D? _blockShape;

    /// <summary>The images the shapes of dropped items that are not blocks are cut from; needed to drop them.</summary>
    public ItemSprites? Sprites { get; init; }

    public IReadOnlyList<DroppedItem> DroppedItems => _items;

    /// <summary>Raised when the player picks some of a dropped item up: what went into its inventory.</summary>
    public event Action<ItemStack>? ItemPickedUp;

    /// <summary>Puts an item in the world, its middle at <paramref name="center"/>, moving.</summary>
    /// <param name="pickupDelay">Seconds before the player can pick it up.</param>
    /// <exception cref="InvalidOperationException">It is not a block and the world has no <see cref="Sprites"/> to shape it.</exception>
    public DroppedItem DropItem
    (
        ItemStack stack,
        dvec3 center,
        dvec3 velocity,
        double pickupDelay = MinedPickupDelay,
        dquat? orientation = null,
        dvec3 spin = default
    )
    {
        if (stack.IsEmpty)
            throw new ArgumentException("An empty stack cannot be dropped", nameof(stack));

        bool block = stack.Info.IsBlock;
        if (!block && Sprites == null)
            throw new InvalidOperationException($"The world has no item sprites to shape a dropped {stack.Info.Name}");

        double radius = block ? 0.5 * Math.Sqrt(3.0) * Items.BlockEdge : 0.5 * Math.Sqrt(2.0) * Items.SpriteEdge;
        DroppedItem item = new(stack, radius)
        {
            PendingPosition = center,
            PendingOrientation = orientation ?? dquat.Identity,
            PendingLinearVelocity = velocity,
            PendingAngularVelocity = spin,
            StepStartPosition = center,
            StepStartOrientation = orientation ?? dquat.Identity,
            PickupDelay = pickupDelay
        };

        _items.Add(item);
        if (MaySimulate(item, 0.0))
            AddItemBody(item);
        else
            item.Parked = true;

        return item;
    }

    /// <summary>Where an item is drawn: between where the last two steps left it, as a body is (<see cref="DrawnPose(CellBody)"/>).</summary>
    public (dvec3 Position, dquat Orientation) DrawnPose(DroppedItem item)
    {
        return Interpolate(item.StepStartPosition, item.StepStartOrientation, item.Position, item.Orientation);
    }

    /// <summary>
    /// Whether a block of a type lies within <paramref name="radius"/> (m) of a point, its
    /// nearest point that near, on the map or on any object.
    /// </summary>
    public bool HasBlockNear(BlockType type, dvec3 point, double radius)
    {
        foreach (VoxelBody body in Bodies)
        {
            if (body is VoxelObject obj && !_topologyChanged.Contains(obj))
            {
                double reach = obj.Bounds.Radius + radius;
                if ((obj.ToWorld(obj.Bounds.Center) - point).LengthSqr > reach * reach)
                    continue;
            }

            dvec3 local = body.ToLocal(point) / VoxelBody.CellSize;
            double cells = radius / VoxelBody.CellSize;
            ivec3 low = (ivec3)dvec3.Floor(local - cells);
            ivec3 high = (ivec3)dvec3.Floor(local + cells);
            for (int z = low.z; z <= high.z; z++)
            for (int y = low.y; y <= high.y; y++)
            for (int x = low.x; x <= high.x; x++)
            {
                ivec3 cell = new(x, y, z);
                dvec3 nearest = dvec3.Clamp(local, new dvec3(cell), new dvec3(cell) + 1.0);
                if ((nearest - local).LengthSqr <= cells * cells && body.Grid[cell].Type == type)
                    return true;
            }
        }

        return false;
    }

    private bool IsItem(PhysicsBody body)
    {
        return _itemBodies.Count > 0 && _itemBodies.ContainsKey(body);
    }

    /// <summary>Notes where each item in the physics is as a step begins, for drawing it between steps.</summary>
    private void RecordItemStarts()
    {
        foreach (DroppedItem item in _items)
        {
            if (item.Body == null)
                continue;

            item.StepStartPosition = item.Body.Position;
            item.StepStartOrientation = item.Body.Orientation;
        }
    }

    /// <summary>
    /// After a step: ages the items in the physics and gives the player those that touched
    /// it, once their pickup delay has passed, as far as its
    /// inventory has room.
    /// </summary>
    private void UpdateItems()
    {
        NoteItemTouches();
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            DroppedItem item = _items[i];
            if (item.Body == null)
                continue;

            item.Age += Step;
            item.PickupDelay = Math.Max(0.0, item.PickupDelay - Step);
            if (_touchedItems.Contains(item) && item.PickupDelay <= 0.0 && Player is { Dead: false } player)
                PickUp(player, item);
        }

        _touchedItems.Clear();
    }

    /// <summary>The items whose contacts pushed on the player in the last step.</summary>
    private void NoteItemTouches()
    {
        if (_itemBodies.Count == 0 || Player == null)
            return;

        RigidBody player = Player.Body;
        IReadOnlyList<ContactReport> contacts = Physics.ContactReports;
        for (int i = 0; i < contacts.Count; i++)
        {
            ContactReport contact = contacts[i];
            PhysicsBody? other = contact.BodyA == player ? contact.BodyB : contact.BodyB == player ? contact.BodyA : null;
            if (other != null && _itemBodies.TryGetValue(other, out DroppedItem? item))
                _touchedItems.Add(item);
        }
    }

    /// <summary>Items under the feet give walking grip and take the opposite push of a jump.</summary>
    private void NoteItemSupport(ContactReport contact)
    {
        if (Player == null || _itemBodies.Count == 0)
            return;

        bool playerIsA = contact.BodyA == Player.Body;
        PhysicsBody? other = playerIsA ? contact.BodyB : contact.BodyB == Player.Body ? contact.BodyA : null;
        dvec3 up = playerIsA ? contact.Normal : -contact.Normal;
        if (other != null && up.z >= Player.SupportSlope
            && contact.Point.z <= StepStartPosition(Player).z + Player.SupportReach
            && _itemBodies.TryGetValue(other, out DroppedItem? item))
            _supports.Add(new Support(null, default, contact.Point, up, contact.NormalImpulse, item));
    }

    private void PickUp(Player player, DroppedItem item)
    {
        ItemStack left = player.Inventory.Add(item.Stack);
        int taken = item.Stack.Count - left.Count;
        if (taken == 0)
            return;

        ItemPickedUp?.Invoke(item.Stack.WithCount(taken));
        if (left.IsEmpty)
            RemoveItem(item);
        else
            item.Stack = left;
    }

    /// <summary>Parks the items beyond the simulation distance, as objects are, and brings back the parked ones well within it.</summary>
    private void ParkItems()
    {
        foreach (DroppedItem item in _items)
        {
            if (item.Body != null && !MaySimulate(item, 0.0))
                ParkItem(item);
            else if (item.Parked && MaySimulate(item, UnparkMargin))
                AddItemBody(item);
        }
    }

    /// <summary>Whether an item is to be simulated: as for an object (<see cref="MaySimulate(VoxelObject, double)"/>).</summary>
    private bool MaySimulate(DroppedItem item, double margin)
    {
        if (!FollowPlayer)
            return true;
        if (Player == null)
            return false;

        dvec3 away = item.Position - Player.Feet;
        double reach = SimulationDistance * Grid3D<Voxel>.ChunkSize * VoxelBody.CellSize - margin;
        return away.x * away.x + away.y * away.y <= reach * reach && GroundLoaded(item.Bounds, ColliderReach);
    }

    private void AddItemBody(DroppedItem item)
    {
        ItemInfo info = item.Stack.Info;
        SurfaceMaterial surface = info.IsBlock ? Blocks.Get(info.Block).Surface : SpriteSurface;
        RigidBody body = new(ShapeOf(item.Stack.Type), info.Mass)
        {
            Position = item.PendingPosition,
            Orientation = item.PendingOrientation,
            LinearVelocity = item.PendingLinearVelocity,
            AngularVelocity = item.PendingAngularVelocity,
            Friction = surface.Friction,
            Restitution = surface.Restitution
        };

        Physics.Add(body);
        _itemBodies[body] = item;
        item.Body = body;
        item.Parked = false;
    }

    private void ParkItem(DroppedItem item)
    {
        RigidBody body = item.Body!;
        item.PendingPosition = body.Position;
        item.PendingOrientation = body.Orientation;
        item.PendingLinearVelocity = body.LinearVelocity;
        item.PendingAngularVelocity = body.AngularVelocity;
        Physics.Remove(body);
        _itemBodies.Remove(body);
        item.Body = null;
        item.Parked = true;
    }

    private void RemoveItem(DroppedItem item)
    {
        if (item.Body != null)
        {
            Physics.Remove(item.Body);
            _itemBodies.Remove(item.Body);
            item.Body = null;
        }

        _items.Remove(item);
        _touchedItems.Remove(item);
    }

    /// <summary>The shape of a dropped item: its block geometry, or its image a pixel thick.</summary>
    private Mesh3D ShapeOf(ItemType type)
    {
        ItemInfo info = Items.Get(type);
        if (info.IsBlock && !BlockShapes.HasShape(info.Block))
            return _blockShape ??= ItemMesher.BlockShape(Items.BlockEdge);

        if (!_spriteShapes.TryGetValue(type, out Mesh3D? shape))
            _spriteShapes[type] = shape = info.IsBlock ? ItemMesher.BlockShape(info.Block, Items.BlockEdge)
                : ItemMesher.SpriteShape(Sprites!.Opaque(type), Items.SpriteEdge);

        return shape;
    }
}
