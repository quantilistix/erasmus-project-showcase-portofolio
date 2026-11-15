using System.Diagnostics;

using GlmSharp;

using MYT.Kit.Vulkan;

using PhysicsTests;

using VoxelGame;

namespace VoxelTests;


/// <summary>
/// Checks of survival play: the inventory and how clicks move stacks, the crafting list, what
/// each tool can mine, the dropped items' bodies and how the player picks them up, and the ore
/// the terrain hides.
/// </summary>
internal static class InventoryTests
{
    private const double PacedStepMilliseconds = 8.0;

    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("inventory-add", InventoryAdd),
        ("inventory-clicks", InventoryClicks),
        ("crafting-list", CraftingList),
        ("mining-tools", MiningTools),
        ("item-shapes", ItemShapes),
        ("item-rests", ItemRests),
        ("item-player-support", ItemPlayerSupport),
        ("item-pickup", ItemPickup),
        ("item-indestructible", ItemIndestructible),
        ("no-despawn", NoDespawn),
        ("terrain-ores", TerrainOres),
        ("item-cost", ItemCost),
        ("held-swing", HeldSwingCurve)
    ];

    private static ItemType Dirt => Items.Of(BlockType.Dirt);
    private static ItemType OakPlanks => Items.Of(BlockType.OakPlanks);
    private static ItemType AcaciaPlanks => Items.Of(BlockType.AcaciaPlanks);

    private static bool Check(string what, double value, double min, double max)
    {
        bool ok = value >= min && value <= max;
        Console.WriteLine($"   {(ok ? "ok  " : "BAD ")} {what} = {value:G5}  (expected {min:G4} .. {max:G4})");
        return ok;
    }

    private static bool Expect(string what, bool condition)
    {
        Console.WriteLine($"   {(condition ? "ok  " : "BAD ")} {what}");
        return condition;
    }

    private static World CreateWorld()
    {
        HeadlessVulkan vulkan = TestGpu.Vulkan;
        return new World(vulkan.Vk, vulkan.Device, vulkan.PhysicalDevice, vulkan.CommandPool, vulkan.Queue, TestGpu.Shaders)
        {
            Sprites = TestGpu.Sprites
        };
    }

    /// <summary>A bedrock slab with a stone floor on it, size × size: the floor's top is at z = 2.</summary>
    private static void Floor(World world, int size)
    {
        for (int x = 0; x < size; x++)
        for (int y = 0; y < size; y++)
        {
            world.SetBlock(world.Map, new ivec3(x, y, 0), BlockType.Bedrock);
            world.SetBlock(world.Map, new ivec3(x, y, 1), BlockType.Stone);
        }

        world.ApplyTopologyChanges();
    }

    /// <summary>Steps in paced real time for a while, or until <paramref name="until"/> holds. Returns the simulated time that took, or infinity.</summary>
    private static double Run(World world, double seconds, Func<bool>? until = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        int steps = (int)(seconds / World.Step);
        for (int step = 0; step < steps; step++)
        {
            world.StepOnce();
            world.ApplyTopologyChanges();
            if (until != null && until())
                return (step + 1) * World.Step;

            double wait = (step + 1) * PacedStepMilliseconds - watch.Elapsed.TotalMilliseconds;
            if (wait > 0)
                Thread.Sleep((int)wait);
        }

        return double.PositiveInfinity;
    }

    /// <summary>Stacks fill up before new ones start, the hotbar first; what does not fit comes back; taking comes from the main inventory first.</summary>
    private static bool InventoryAdd()
    {
        Inventory inventory = new();
        bool ok = Expect("100 dirt fill the first slot and spill into the next", inventory.Add(new ItemStack(Dirt, 100)).IsEmpty
            && inventory.Slots[0] == new ItemStack(Dirt, 64) && inventory.Slots[1] == new ItemStack(Dirt, 36));
        inventory.Add(new ItemStack(ItemType.WoodenPickaxe, 1));
        inventory.Add(new ItemStack(ItemType.WoodenPickaxe, 1));
        ok &= Expect("pickaxes do not stack", inventory.Slots[2].Count == 1 && inventory.Slots[3].Count == 1);
        inventory.Add(new ItemStack(Dirt, 30));
        ok &= Expect("more dirt tops up the stack that has room first", inventory.Slots[1] == new ItemStack(Dirt, 64) && inventory.Slots[4] == new ItemStack(Dirt, 2));
        ok &= Check("dirt held", inventory.Count(Dirt), 130, 130);

        inventory.Slots[20] = new ItemStack(Dirt, 10);
        ok &= Expect("taking 15 dirt empties the main inventory's stack first", inventory.Remove(Dirt, 15) && inventory.Slots[20].IsEmpty && inventory.Slots[0].Count == 59);
        ok &= Expect("taking more than there is takes nothing", !inventory.Remove(Dirt, 1000) && inventory.Count(Dirt) == 125);

        for (int slot = 0; slot < Inventory.Size; slot++)
            inventory.Slots[slot] = new ItemStack(ItemType.StonePickaxe, 1);
        ItemStack left = inventory.Add(new ItemStack(Dirt, 5));
        ok &= Expect("a full inventory gives everything back", left == new ItemStack(Dirt, 5));
        return ok;
    }

    /// <summary>Clicks move stacks as Minecraft's do.</summary>
    private static bool InventoryClicks()
    {
        Inventory inventory = new();
        inventory.Slots[10] = new ItemStack(Dirt, 30);
        inventory.Slots[11] = new ItemStack(Dirt, 50);
        inventory.Slots[12] = new ItemStack(ItemType.Stick, 5);

        inventory.Click(10, SlotClick.Primary);
        bool ok = Expect("a left click takes the whole stack", inventory.Cursor == new ItemStack(Dirt, 30) && inventory.Slots[10].IsEmpty);
        inventory.Click(11, SlotClick.Primary);
        ok &= Expect("onto a stack of the same, it tops it up and keeps the rest", inventory.Slots[11].Count == 64 && inventory.Cursor == new ItemStack(Dirt, 16));
        inventory.Click(12, SlotClick.Primary);
        ok &= Expect("onto something else, it swaps", inventory.Slots[12] == new ItemStack(Dirt, 16) && inventory.Cursor == new ItemStack(ItemType.Stick, 5));
        inventory.Click(13, SlotClick.Secondary);
        inventory.Click(14, SlotClick.Secondary);
        ok &= Expect("a right click puts down one at a time", inventory.Slots[13].Count == 1 && inventory.Slots[14].Count == 1 && inventory.Cursor.Count == 3);
        inventory.Click(10, SlotClick.Primary);
        ok &= Expect("a left click on an empty slot puts all down", inventory.Slots[10] == new ItemStack(ItemType.Stick, 3) && inventory.Cursor.IsEmpty);
        inventory.Click(11, SlotClick.Secondary);
        ok &= Expect("a right click with nothing takes half, rounded up", inventory.Cursor.Count == 32 && inventory.Slots[11].Count == 32);
        inventory.Click(11, SlotClick.Primary);

        inventory.Click(11, SlotClick.Quick);
        ok &= Expect("SHIFT moves a stack from the main inventory into the hotbar", inventory.Slots[11].IsEmpty && inventory.Slots[0] == new ItemStack(Dirt, 64));
        inventory.Click(0, SlotClick.Quick);
        ok &= Expect("and back, onto the stack of it there first", inventory.Slots[0].IsEmpty && inventory.Slots[12] == new ItemStack(Dirt, 64) && inventory.Slots[9] == new ItemStack(Dirt, 16));

        inventory.SwapWithHotbar(10, 4);
        ok &= Expect("a number key swaps a slot with that hotbar slot", inventory.Slots[4] == new ItemStack(ItemType.Stick, 3) && inventory.Slots[10].IsEmpty);
        return ok;
    }

    /// <summary>Crafting takes the cost and gives what it makes; planks of either wood count as planks; nothing changes if it cannot be paid for or has no room.</summary>
    private static bool CraftingList()
    {
        Recipe Find(ItemType type) => Crafting.Recipes.First(r => r.Result.Type == type);

        Inventory inventory = new();
        inventory.Add(new ItemStack(Items.Of(BlockType.OakLog), 1));
        bool ok = Expect("a log makes four planks", Crafting.Craft(inventory, Find(OakPlanks), false)
            && inventory.Count(OakPlanks) == 4 && inventory.Count(Items.Of(BlockType.OakLog)) == 0);
        ok &= Expect("with no log left, no more", !Crafting.Craft(inventory, Find(OakPlanks), false) && inventory.Count(OakPlanks) == 4);

        inventory.Add(new ItemStack(AcaciaPlanks, 1));
        inventory.Remove(OakPlanks, 3);
        ok &= Expect("an oak and an acacia plank make sticks", Crafting.Craft(inventory, Find(ItemType.Stick), false)
            && inventory.Count(ItemType.Stick) == 4 && inventory.Count(OakPlanks) + inventory.Count(AcaciaPlanks) == 0);

        Recipe pickaxe = Find(ItemType.WoodenPickaxe);
        ok &= Expect("a pickaxe takes a workbench", pickaxe.NeedsWorkbench && !Find(ItemType.Stick).NeedsWorkbench);
        ok &= Expect("without planks, no pickaxe", !Crafting.Craft(inventory, pickaxe, false) && inventory.Count(ItemType.Stick) == 4);
        inventory.Add(new ItemStack(OakPlanks, 3));
        ok &= Expect("3 planks and 2 sticks make a wooden pickaxe", Crafting.Craft(inventory, pickaxe, false)
            && inventory.Count(ItemType.WoodenPickaxe) == 1 && inventory.Count(OakPlanks) == 0 && inventory.Count(ItemType.Stick) == 2);

        Inventory empty = new();
        ok &= Expect("unlimited resources craft from nothing", Crafting.Craft(empty, Find(ItemType.DiamondPickaxe), true) && empty.Count(ItemType.DiamondPickaxe) == 1);

        Inventory full = new();
        for (int slot = 0; slot < Inventory.Size; slot++)
            full.Slots[slot] = new ItemStack(ItemType.StonePickaxe, 1);
        full.Slots[0] = new ItemStack(Items.Of(BlockType.Stone), 1);
        ok &= Expect("stone makes cobblestone in the slot it frees", Crafting.Craft(full, Find(Items.Of(BlockType.Cobblestone)), false) && full.Slots[0] == new ItemStack(Items.Of(BlockType.Cobblestone), 1));
        full.Slots[1] = new ItemStack(Items.Of(BlockType.OakLog), 2);
        full.Slots[2] = new ItemStack(OakPlanks, 63);
        ok &= Expect("no room for four planks: nothing is crafted", !Crafting.Craft(full, Find(OakPlanks), false) && full.Slots[1].Count == 2 && full.Slots[2].Count == 63);
        return ok;
    }

    /// <summary>
    /// A hand bounces off intact stone and ore and gets through wood and soil; each pickaxe
    /// gets through what the one before it could not, and mines stone faster. Stone worn down
    /// below the hand's strike gives way to it. Instant breaking takes any block in a strike,
    /// bedrock too, which nothing else breaks.
    /// </summary>
    private static bool MiningTools()
    {
        using World world = CreateWorld();
        Floor(world, 6);
        Player player = world.SpawnPlayer(new dvec3(0.5, 0.5, 2));
        player.Flying = false;
        ivec3 cell = new(3, 0, 3);
        Tool Hand = Tool.Hand;
        Tool Pickaxe(ItemType type) => Items.Get(type).Tool!;

        // Strikes a fresh block of a type until it breaks; the number of strikes, or 0 if none of 300 broke it.
        int Strikes(BlockType type, Tool tool, double health = 1.0)
        {
            world.SetBlock(world.Map, cell, type);
            world.Map.SetDamage(cell, 1.0 - health);
            world.ApplyTopologyChanges();
            VoxelHit hit = world.Raycast(player.Eye, (new dvec3(cell) + 0.5 - player.Eye).Normalized, 8)!.Value;
            for (int strike = 1; strike <= 300; strike++)
            {
                if (world.Mine(hit, player.Eye, (new dvec3(cell) + 0.5 - player.Eye).Normalized, tool))
                    return strike;
            }

            world.RemoveBlock(world.Map, cell);
            world.ApplyTopologyChanges();
            return 0;
        }

        bool ok = Check("hand strikes to break stone (none do)", Strikes(BlockType.Stone, Hand), 0, 0);
        ok &= Check("hand strikes to break an oak log", Strikes(BlockType.OakLog, Hand), 10, 30);
        ok &= Check("hand strikes to break dirt", Strikes(BlockType.Dirt, Hand), 3, 5);
        int wooden = Strikes(BlockType.Stone, Pickaxe(ItemType.WoodenPickaxe));
        int stone = Strikes(BlockType.Stone, Pickaxe(ItemType.StonePickaxe));
        int diamond = Strikes(BlockType.Stone, Pickaxe(ItemType.DiamondPickaxe));
        ok &= Check("wooden pickaxe strikes to break stone", wooden, 8, 20);
        ok &= Expect($"better pickaxes break stone in fewer strikes ({wooden}, {stone}, {diamond})", wooden > stone && stone > diamond && diamond > 0);
        ok &= Check("hand strikes to break stone worn to 70%", Strikes(BlockType.Stone, Hand, 0.7), 1, 30);
        ok &= Check("wooden pickaxe on gold ore (none get through)", Strikes(BlockType.GoldOre, Pickaxe(ItemType.WoodenPickaxe)), 0, 0);
        ok &= Check("stone pickaxe strikes to break gold ore", Strikes(BlockType.GoldOre, Pickaxe(ItemType.StonePickaxe)), 1, 100);
        ok &= Check("stone pickaxe on diamond ore (none get through)", Strikes(BlockType.DiamondOre, Pickaxe(ItemType.StonePickaxe)), 0, 0);
        ok &= Check("golden pickaxe strikes to break diamond ore", Strikes(BlockType.DiamondOre, Pickaxe(ItemType.GoldenPickaxe)), 1, 100);

        world.SetBlock(world.Map, cell, BlockType.Stone);
        world.ApplyTopologyChanges();
        dvec3 toward = (new dvec3(cell) + 0.5 - player.Eye).Normalized;
        VoxelHit target = world.Raycast(player.Eye, toward, 8)!.Value;
        ok &= Expect("an instant strike breaks stone by hand", world.Mine(target, player.Eye, toward, Hand, instant: true));
        world.ApplyTopologyChanges();
        world.SetBlock(world.Map, cell, BlockType.Bedrock);
        world.ApplyTopologyChanges();
        target = world.Raycast(player.Eye, toward, 8)!.Value;
        ok &= Expect("a strike does not break bedrock", !world.Mine(target, player.Eye, toward, Pickaxe(ItemType.DiamondPickaxe)) && world.Map.Grid[cell].Type == BlockType.Bedrock);
        ok &= Expect("an instant strike breaks bedrock", world.Mine(target, player.Eye, toward, Hand, instant: true) && world.Map.Grid[cell].Type == BlockType.Air);
        return ok;
    }

    /// <summary>
    /// A tool's body is closed, every edge joining exactly two faces, as thick as a pixel and as
    /// big as its image's pixels that show; its drawn model has faces only for those pixels.
    /// </summary>
    private static bool ItemShapes()
    {
        bool ok = true;
        foreach (ItemType type in Items.NonBlockTypes)
        {
            bool[,] opaque = TestGpu.Sprites.Opaque(type);
            int shown = opaque.Cast<bool>().Count(o => o);
            Mesh3D shape = ItemMesher.SpriteShape(opaque, Items.SpriteEdge);
            Dictionary<(ulong, ulong), int> edges = [];
            for (int t = 0; t < shape.Faces.Length; t += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    ulong a = shape.Faces[t + k];
                    ulong b = shape.Faces[t + (k + 1) % 3];
                    (ulong, ulong) key = (Math.Min(a, b), Math.Max(a, b));
                    edges[key] = edges.GetValueOrDefault(key) + 1;
                }
            }

            double volume = 0.0;
            for (int t = 0; t < shape.Faces.Length; t += 3)
            {
                dvec3 a = shape.Vertices[shape.Faces[t]];
                dvec3 b = shape.Vertices[shape.Faces[t + 1]];
                dvec3 c = shape.Vertices[shape.Faces[t + 2]];
                volume += dvec3.Dot(a, dvec3.Cross(b, c)) / 6.0;
            }

            double pixel = Items.SpriteEdge / ItemSprites.Size;
            string name = Items.Get(type).Name;
            ok &= Expect($"{name}: every edge joins two faces", edges.Values.All(n => n == 2));
            ok &= Check($"{name}: volume in pixels (shown: {shown})", volume / (pixel * pixel * pixel), shown - 1e-6, shown * 1.25);
            ok &= Check($"{name}: thickness (m)", shape.Vertices.Max(v => v.y) - shape.Vertices.Min(v => v.y), pixel, pixel);

            ItemVertex[] model = ItemMesher.SpriteModel(opaque, 0);
            int fronts = model.Count(v => v.Normal == new vec3(0, -1, 0)) / 6;
            ok &= Check($"{name}: front faces drawn", fronts, shown, shown);
        }

        return ok;
    }

    /// <summary>Old drops and bodies below the former void cutoff remain actual world entities.</summary>
    private static bool NoDespawn()
    {
        using World world = CreateWorld();
        VoxelObject small = world.Spawn([(ivec3.Zero, BlockType.Stone)], new dvec3(0, 0, -80), dquat.Identity);
        VoxelObject tall = world.Spawn(Enumerable.Range(0, 128).Select(z => (new ivec3(0, 0, z), BlockType.Stone)), new dvec3(20, 0, -70), dquat.Identity);
        DroppedItem old = world.DropItem(new ItemStack(Dirt, 3), new dvec3(40, 0, 10), dvec3.Zero);
        DroppedItem fallen = world.DropItem(new ItemStack(Dirt, 2), new dvec3(60, 0, -80), dvec3.Zero);
        old.Age = 600;
        world.ApplyTopologyChanges();
        world.StepOnce();
        bool ok = Expect("small object below the former cutoff stays", world.Objects.Contains(small) && small.Grid.Count == 1);
        ok &= Expect("tall object whose origin is below the cutoff stays", world.Objects.Contains(tall) && tall.Grid.Count == 128);
        ok &= Expect("old item stays with its full stack", world.DroppedItems.Contains(old) && old.Stack.Count == 3 && old.Age > 600);
        ok &= Expect("item below the former cutoff stays", world.DroppedItems.Contains(fallen) && fallen.Stack.Count == 2);
        return ok;
    }

    /// <summary>Dropped items fall and come to rest on the floor: a block's cube on a face, a pickaxe lying flat.</summary>
    private static bool ItemRests()
    {
        using World world = CreateWorld();
        Floor(world, 8);
        DroppedItem block = world.DropItem(new ItemStack(Items.Of(BlockType.Glass), 1), new dvec3(2.5, 2.5, 4), new dvec3(0.5, 0, 0), spin: new dvec3(1, 2, 0));
        DroppedItem pickaxe = world.DropItem(new ItemStack(ItemType.DiamondPickaxe, 1), new dvec3(5.5, 5.5, 4), dvec3.Zero, orientation: dquat.FromAxisAngle(0.8, new dvec3(1, 1, 0).Normalized));
        Run(world, 4.0);

        double pixel = Items.SpriteEdge / ItemSprites.Size;
        bool ok = Check("the block's middle over the floor (m)", block.Position.z - 2.0, Items.BlockEdge / 2 - 0.01, Items.BlockEdge / 2 + 0.01);
        ok &= Check("the block at rest (m/s)", block.LinearVelocity.Length, 0, 0.02);
        ok &= Check("the pickaxe's middle over the floor (m)", pickaxe.Position.z - 2.0, 0.0, pixel);
        ok &= Check("the pickaxe at rest (m/s)", pickaxe.LinearVelocity.Length, 0, 0.02);
        ok &= Check("items still in the world", world.DroppedItems.Count, 2, 2);
        return ok;
    }

    /// <summary>Dropped blocks keep their material's friction and support walking and jumping.</summary>
    private static bool ItemPlayerSupport()
    {
        bool ok = true;
        foreach (BlockType type in new[] { BlockType.Stone, BlockType.Glass, BlockType.OakPlanks })
        foreach (bool walking in new[] { true, false })
        {
            using World world = CreateWorld();
            Floor(world, 6);
            // Absorb the item's downward kick. On stone a light, bouncy item can rebound
            // into the rising player and add a second impulse unrelated to jump control.
            for (int x = 2; x <= 3; x++)
            for (int y = 2; y <= 3; y++)
                world.SetBlock(world.Map, new ivec3(x, y, 1), BlockType.Sand);
            world.ApplyTopologyChanges();
            DroppedItem item = world.DropItem(new ItemStack(Items.Of(type), 1), new dvec3(3, 3, 2.25), dvec3.Zero, pickupDelay: 60.0);
            Player player = world.SpawnPlayer(new dvec3(3, 3, 2.55));
            player.Flying = false;
            player.Invulnerable = true;
            Run(world, 0.5);
            double friction = Blocks.Get(type).Friction;
            ok &= Check($"{type}: item friction", item.Body!.Friction, friction, friction);
            ok &= Expect("item supports the player above the floor", player.Grounded && player.Feet.z > 2.4);

            // Check walking separately: its opposite impulse can tip a light item, which
            // changes the surface motion and hence the next jump's world-space trajectory.
            if (walking)
            {
                player.Controls = new PlayerControls(dvec3.UnitX, false);
                world.StepOnce();
                double expected = Player.WalkSpeed * (1.0 - Math.Exp(-Player.WalkAcceleration * friction / Player.WalkSpeed * World.Step));
                ok &= Check("walking grip matches the block (m/s)", player.Body.LinearVelocity.x, expected * 0.9, expected * 1.1);
                continue;
            }
            player.JumpPressed = true;
            world.StepOnce();
            ok &= Check("jumping off the item (m/s)", player.Body.LinearVelocity.z, 9.0, 10.2);
            double start = player.Feet.z;
            double highest = start;
            Run(world, 0.7, () =>
            {
                highest = Math.Max(highest, player.Feet.z);
                return false;
            });
            ok &= Check("jump rises above the item (m)", highest - start, 1.2, 1.65);
            ok &= Expect("jump leaves the item intact", world.DroppedItems.Contains(item) && item.Stack.Count == 1);
            ok &= Check("jump leaves the player healthy", player.Health, 1, 1);

            // Removing a support through pickup must not leave a stale reference next step.
            player.MoveTo(new dvec3(item.Position.x, item.Position.y, item.Position.z + Items.BlockEdge / 2));
            item.PickupDelay = 0.0;
            Run(world, 0.3);
            ok &= Expect("support can still be picked up", !world.DroppedItems.Contains(item));
            world.StepOnce();
        }
        return ok;
    }

    /// <summary>
    /// The player picks up what touches it once its pickup delay is over, by walking into it
    /// or by it falling on it, as far as its inventory has room; what has no room stays.
    /// </summary>
    private static bool ItemPickup()
    {
        using World world = CreateWorld();
        Floor(world, 10);
        Player player = world.SpawnPlayer(new dvec3(2.5, 2.5, 2));
        player.Flying = false;
        player.Invulnerable = true;
        Run(world, 0.5);

        ItemType sand = Items.Of(BlockType.Sand);
        DroppedItem late = world.DropItem(new ItemStack(sand, 3), player.Eye + new dvec3(0, 0, 1), dvec3.Zero, pickupDelay: 1.0);
        Run(world, 0.8);
        bool ok = Expect("an item on the player's head waits out its delay", world.DroppedItems.Contains(late) && player.Inventory.Count(sand) == 0);
        double taken = Run(world, 1.5, () => !world.DroppedItems.Contains(late));
        ok &= Check("then is picked up (s)", taken, 0, 1.0);
        ok &= Check("sand in the inventory", player.Inventory.Count(sand), 3, 3);

        DroppedItem dirt = world.DropItem(new ItemStack(Dirt, 1), new dvec3(6.5, 2.5, 2.5), dvec3.Zero);
        Run(world, 1.0);
        ok &= Expect("an item out of reach lies on the floor", world.DroppedItems.Contains(dirt) && Math.Abs(dirt.Position.z - 2.0 - Items.BlockEdge / 2) < 0.02);
        player.Controls = new PlayerControls(new dvec3(1, 0, 0), false);
        double walked = Run(world, 3.0, () => !world.DroppedItems.Contains(dirt));
        player.Controls = default;
        ok &= Check("walking into it picks it up (s)", walked, 0, 2.0);
        ok &= Check("dirt in the inventory", player.Inventory.Count(Dirt), 1, 1);

        for (int slot = 0; slot < Inventory.Size; slot++)
            player.Inventory.Slots[slot] = new ItemStack(ItemType.StonePickaxe, 1);
        DroppedItem kept = world.DropItem(new ItemStack(Dirt, 2), player.Eye + new dvec3(0, 0, 1), dvec3.Zero);
        Run(world, 1.5);
        ok &= Expect("with no room, it stays in the world", world.DroppedItems.Contains(kept) && kept.Stack.Count == 2);
        return ok;
    }

    /// <summary>A heavy dropped item falling hard onto glass neither breaks it nor breaks, and does not hurt the player.</summary>
    private static bool ItemIndestructible()
    {
        using World world = CreateWorld();
        Floor(world, 6);
        ivec3 glass = new(3, 3, 2);
        world.SetBlock(world.Map, glass, BlockType.Glass);
        world.ApplyTopologyChanges();
        Player player = world.SpawnPlayer(new dvec3(1, 1, 2));
        player.Flying = false;
        DroppedItem stone = world.DropItem(new ItemStack(Items.Of(BlockType.Stone), 1), new dvec3(3.5, 3.5, 25), new dvec3(0, 0, -20));
        DroppedItem onPlayer = world.DropItem(new ItemStack(Items.Of(BlockType.Stone), 1), player.Eye + new dvec3(0, 0, 15), new dvec3(0, 0, -15), pickupDelay: 100.0);
        Run(world, 3.0);

        bool ok = Check("the glass's health", world.Map.Grid[glass].Health, 1, 1);
        ok &= Expect("the glass is there", world.Map.Grid[glass].Type == BlockType.Glass);
        ok &= Expect("the items are there", world.DroppedItems.Contains(stone) && world.DroppedItems.Contains(onPlayer));
        ok &= Check("the player's health", player.Health, 1, 1);
        return ok;
    }

    /// <summary>
    /// What a dropped item costs on the generated spawn area: the step that adds its body and
    /// the one that takes it away (each rebuilds the physics) next to a plain step, and a step
    /// with a hundred items lying about.
    /// </summary>
    private static bool ItemCost()
    {
        using World world = CreateWorld();
        TerrainGenerator.Generate(world);
        world.ApplyTopologyChanges();
        Player player = world.SpawnPlayer(TerrainGenerator.SpawnPoint);
        player.Flying = false;
        Run(world, 2.0);

        double Timed(Action before)
        {
            before();
            Stopwatch watch = Stopwatch.StartNew();
            world.StepOnce();
            world.ApplyTopologyChanges();
            return watch.Elapsed.TotalMilliseconds;
        }

        double Median(Func<double> measure)
        {
            List<double> times = [];
            for (int k = 0; k < 9; k++)
            {
                times.Add(measure());
                Run(world, 0.1);
            }

            times.Sort();
            return times[times.Count / 2];
        }

        dvec3 away = TerrainGenerator.SpawnPoint + new dvec3(6, 0, 3);
        double plain = Median(() => Timed(() => { }));
        DroppedItem? last = null;
        double adding = Median(() => Timed(() => last = world.DropItem(new ItemStack(Dirt, 1), away, dvec3.Zero)));
        double removing = Median(() =>
        {
            DroppedItem item = world.DropItem(new ItemStack(Dirt, 1), player.Eye + new dvec3(0, 0, 0.6), dvec3.Zero, pickupDelay: 0.0);
            Run(world, 1.0, () => !world.DroppedItems.Contains(item));
            return Timed(() => { });
        });
        for (int k = 0; k < 100; k++)
            world.DropItem(new ItemStack(ItemType.StonePickaxe, 1), away + new dvec3(k % 10 * 0.6 - 3, k / 10 * 0.6 - 3, 1), dvec3.Zero);
        Run(world, 2.0);
        double many = Median(() => Timed(() => { }));

        Console.WriteLine($"   steps (median of 9): plain {plain:F1} ms, adding an item {adding:F1} ms, with 100 items lying about {many:F1} ms ({removing:F1} ms in the step after a pickup)");
        bool ok = Check("extra cost of the step that adds an item (ms)", adding - plain, -5, 15);
        ok &= Check("extra cost of the step after a pickup (ms)", removing - plain, -5, 15);
        ok &= Check("step with 100 items lying about (ms)", many, 0, plain + 10);
        return ok;
    }

    /// <summary>
    /// A swing starts and ends at rest, never jumps, and thrusts forward to the strike, then drops
    /// down, turning. Hits
    /// start swings, one each, and land as they strike: a click hits once, and its swing goes on
    /// longer than the time between hits; held, hits come six a second, each catching the swing
    /// before settling, below rest, and swinging again from there without a jump; clicks faster
    /// than a swing strikes still hit once each.
    /// </summary>
    private static bool HeldSwingCurve()
    {
        bool ok = true;
        foreach (SwingStyle style in Enum.GetValues<SwingStyle>())
        {
            ok &= Expect($"{style}: starts and ends at rest", HeldSwing.Sample(0f, style) == default && HeldSwing.Sample(1f, style) == default);
            float jump = 0f, forward = 0f, down = 0f, pitched = 0f, mostForward = 0f, mostDown = 0f;
            HandPose last = default;
            for (int k = 1; k <= 1000; k++)
            {
                HandPose pose = HeldSwing.Sample(k / 1000f, style);
                jump = Math.Max(jump, Math.Max((pose - last).Offset.Length, (pose - last).Turn.Length));
                if (pose.Offset.y > forward)
                    (forward, mostForward) = (pose.Offset.y, k / 1000f);
                if (-pose.Offset.z > down)
                    (down, mostDown) = (-pose.Offset.z, k / 1000f);
                pitched = Math.Max(pitched, -pose.Turn.x);
                last = pose;
            }

            ok &= Check($"{style}: largest change in a thousandth of a swing (m, rad)", jump, 0, 0.04);
            ok &= Check($"{style}: farthest forward (m)", forward, 0.35, 0.5);
            ok &= Check($"{style}: farthest down (m)", down, 0.12, 0.3);
            ok &= Check($"{style}: farthest forward, as it strikes (phase)", mostForward, HeldSwing.StrikePhase - 0.03, HeldSwing.StrikePhase + 0.03);
            ok &= Check($"{style}: farthest down, after (phase)", mostDown, HeldSwing.StrikePhase + 0.1, 0.5);
            ok &= Check($"{style}: most pitched down (rad)", pitched, 0.1, 1.5);
        }

        double hitTime = 1.0 / Player.MiningHitsPerSecond;
        double strikeTime = HeldSwing.StrikePhase * HeldSwing.SwingSeconds;
        ok &= Expect("a swing lasts longer than the time between hits", HeldSwing.SwingSeconds > hitTime);

        // Hits at the given times start swings, as the game's do; played a frame at a time: the
        // hits landed and when, the largest turn in a frame, and the pose's height just before
        // each hit after the first.
        (List<double> Landed, float Jump, List<float> Heights) Play(double[] hits, double seconds, double frame)
        {
            HeldSwing swing = new();
            List<double> landed = [];
            List<float> heights = [];
            float jump = 0f;
            HandPose last = default;
            int next = 0;
            for (double time = 0.0; time < seconds; time += frame)
            {
                int strikes = 0;
                if (next < hits.Length && time >= hits[next] - 1e-9)
                {
                    if (next > 0)
                        heights.Add(swing.Pose(SwingStyle.Chop).Offset.z);
                    strikes += swing.Start(SwingStyle.Chop);
                    next++;
                }

                strikes += swing.Update(frame);
                for (int k = 0; k < strikes; k++)
                    landed.Add(time + frame);

                HandPose pose = swing.Pose(SwingStyle.Chop);
                jump = Math.Max(jump, (pose - last).Turn.Length);
                last = pose;
            }

            return (landed, jump, heights);
        }

        const double Frame = 1.0 / 60.0;
        (List<double> click, _, _) = Play([0.0], 1.0, Frame);
        ok &= Check("hits from a click", click.Count, 1, 1);
        ok &= Check("the click's hit, as its swing strikes (s)", click.FirstOrDefault(), strikeTime, strikeTime + Frame);

        double[] held = [.. Enumerable.Range(0, 6).Select(k => k * hitTime)];
        (List<double> holding, _, List<float> heights) = Play(held, 1.5, Frame);
        ok &= Check("hits in a second held", holding.Count, 6, 6);
        ok &= Expect("each as a swing strikes", holding.Zip(held).All(p => p.First - p.Second >= strikeTime - 1e-9 && p.First - p.Second <= strikeTime + Frame + 1e-9));
        ok &= Check("highest the item is when the next hit catches its swing (m, rest is 0)", heights.Max(), -1, -0.02);
        // Played a millisecond at a time, a jump would stand out from the motion: at its fastest,
        // the chop coming down while a restart blends in, it turns under 0.1 rad a millisecond,
        // where restarting without the blend would jump about a radian.
        ok &= Check("largest turn in a millisecond, mining (rad)", Play(held, 1.5, 0.001).Jump, 0, 0.12);

        double[] fast = [0.0, 0.03, 0.06];
        (List<double> clicks, _, _) = Play(fast, 1.0, Frame);
        ok &= Check("hits from three clicks faster than a swing strikes", clicks.Count, 3, 3);
        ok &= Check("largest turn in a millisecond, clicking fast (rad)", Play(fast, 1.0, 0.001).Jump, 0, 0.12);
        return ok;
    }

    /// <summary>Generated stone holds gold low down and diamonds lower still, the same every time; the spawn area holds none.</summary>
    private static bool TerrainOres()
    {
        TerrainGenerator generator = new(5);
        int gold = 0, diamonds = 0, stone = 0, highestGold = 0, highestDiamond = 0;
        for (int x = 4; x < 12; x++)
        for (int y = -6; y < 2; y++)
        {
            foreach ((int z, Voxel[] cells) in generator.GenerateColumn(new ivec2(x, y)).Chunks)
            {
                for (int i = 0; i < cells.Length; i++)
                {
                    int height = z * Grid3D<Voxel>.ChunkSize + Grid3D<Voxel>.CellOf(ivec3.Zero, i).z;
                    switch (cells[i].Type)
                    {
                        case BlockType.GoldOre:
                            gold++;
                            highestGold = Math.Max(highestGold, height);
                            break;
                        case BlockType.DiamondOre:
                            diamonds++;
                            highestDiamond = Math.Max(highestDiamond, height);
                            break;
                        case BlockType.Stone:
                            stone++;
                            break;
                    }
                }
            }
        }

        Console.WriteLine($"   64 columns: {stone} stone, {gold} gold ore, {diamonds} diamond ore");
        bool ok = Check("gold ore per stone", gold / (double)stone, 0.002, 0.03);
        ok &= Check("diamond ore per stone", diamonds / (double)stone, 0.0005, 0.01);
        ok &= Check("highest gold", highestGold, 20, 57);
        ok &= Check("highest diamond", highestDiamond, 10, 33);
        ok &= Expect("a column comes out the same twice", generator.GenerateColumn(new ivec2(7, -3)).Chunks.Zip(generator.GenerateColumn(new ivec2(7, -3)).Chunks).All(p => p.First.Cells.AsSpan().SequenceEqual(p.Second.Cells)));

        int inSpawn = TerrainGenerator.SpawnColumns()
            .SelectMany(c => generator.GenerateColumn(c).Chunks)
            .Sum(chunk => chunk.Cells.Count(v => v.Type is BlockType.GoldOre or BlockType.DiamondOre));
        ok &= Check("ore in the spawn area", inSpawn, 1, 100000);
        return ok;
    }
}
