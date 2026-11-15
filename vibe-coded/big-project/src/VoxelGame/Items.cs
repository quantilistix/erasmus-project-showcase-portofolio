using MYT.Kit.Structural;

namespace VoxelGame;


/// <summary>
/// What an inventory slot or a dropped item holds. Every block is an item under its own number
/// (<see cref="Items.Of(BlockType)"/>), so the blocks are not named here; the items that are not
/// blocks are numbered from <see cref="Items.FirstNonBlock"/> on, so that a new block never
/// renumbers them. Saves store items by these numbers: new ones go last.
/// </summary>
internal enum ItemType : ushort
{
    None = 0,
    Stick = Items.FirstNonBlock,
    WoodenPickaxe,
    StonePickaxe,
    GoldenPickaxe,
    DiamondPickaxe,
    IronPickaxe, EmeraldPickaxe, Coal, IronIngot, GoldIngot, Diamond, Emerald,
    Ruby, Soul, Cocoa, Cookie, RubyPickaxe
}


/// <summary>Some of an item: a slot's contents. Empty when there are none.</summary>
internal readonly record struct ItemStack(ItemType Type, int Count)
{
    public static readonly ItemStack Empty = default;

    public bool IsEmpty => Count <= 0 || Type == ItemType.None;

    public ItemInfo Info => Items.Get(Type);

    /// <summary>The same item, so many of it; empty for none.</summary>
    public ItemStack WithCount(int count)
    {
        return count <= 0 ? Empty : this with { Count = count };
    }
}


/// <summary>
/// What a strike with something in hand does to a block. It crushes the block under its edge
/// at <see cref="Strike"/>; a strike that does not get past the block's elastic limit
/// (<see cref="BlockMaterial.StrikeRatio"/>) bounces off and does nothing, so a bare hand never
/// gets through intact stone, and a strike that does deals the more of the block's mining rate
/// the farther past the limit it gets, all of it times <see cref="Speed"/>.
/// </summary>
/// <param name="Strike">The stress (Pa) a strike crushes a block with under the tool's edge.</param>
/// <param name="Speed">How much a strike that gets through deals, next to a hand's.</param>
internal sealed record Tool(double Strike, double Speed)
{
    /// <summary>
    /// A bare hand, or a hand holding something that is not a tool: through wood, glass,
    /// cobblestone and soil, not stone, whose limit is 16 MPa under a strike.
    /// </summary>
    public static readonly Tool Hand = new(12e6, 1.0);

    /// <summary>
    /// Damage (HP) mining a block of a health with this for <paramref name="seconds"/> deals:
    /// none if the sustained stress plus the strike stays within its elastic limit,
    /// otherwise its mining rate times
    /// <see cref="Speed"/> times 1 − 1/ρ², the share of the strike's energy past the limit, where
    /// ρ is how far past it the strike gets. A tool far stronger than the block mines it at the
    /// full rate, one barely stronger only slowly.
    /// </summary>
    public double Damage(BlockMaterial material, double health, double seconds, CellStress sustained = default)
    {
        if (material.Unbreakable) return 0;
        double ratio = material.ElasticLimit(health).Ratio(new CellStress(sustained.Tension,
            sustained.Compression + Strike, sustained.Shear + Strike / 2));
        if (!(ratio > 1.0))
            return 0.0;

        return material.MiningRate * Speed * (1.0 - 1.0 / (ratio * ratio)) * seconds;
    }
}


/// <summary>An item as the game knows it.</summary>
/// <param name="Block">The block it places; Air for an item that is not a block.</param>
/// <param name="Sprite">For an item that is not a block, its 16 × 16 image, which its dropped and held models are cut from.</param>
/// <param name="Tool">What it mines with; null for an item that mines as a bare hand does.</param>
/// <param name="Mass">The mass (kg) of the item dropped in the world.</param>
internal sealed record ItemInfo(string Name, int MaxStack, BlockType Block, string? Sprite, Tool? Tool, double Mass)
{
    public bool IsBlock => Block != BlockType.Air;
}


/// <summary>
/// Every item: the blocks, and sticks and pickaxes, which mine stone and ore that a hand
/// cannot (<see cref="Tool"/>). A dropped block is a small cube of it, <see cref="BlockEdge"/>
/// on a side; a dropped item that is not a block is its image a pixel thick,
/// <see cref="SpriteEdge"/> across.
/// </summary>
internal static class Items
{
    public const int FirstNonBlock = 1024;
    public const int StackSize = 64;
    /// <summary>The edge (m) of a dropped block.</summary>
    public const double BlockEdge = 0.5;
    /// <summary>The width (m) of a dropped item that is not a block, its image's 16 pixels.</summary>
    public const double SpriteEdge = 0.5;

    private static readonly Dictionary<ItemType, ItemInfo> NonBlocks = new()
    {
        [ItemType.Stick] = new("Stick", StackSize, BlockType.Air, "stick", null, 0.1),
        [ItemType.WoodenPickaxe] = new("Wooden Pickaxe", 1, BlockType.Air, "wooden_pickaxe", new Tool(24e6, 1.5), 1.2),
        [ItemType.StonePickaxe] = new("Stone Pickaxe", 1, BlockType.Air, "stone_pickaxe", new Tool(40e6, 2.0), 2.0),
        [ItemType.GoldenPickaxe] = new("Golden Pickaxe", 1, BlockType.Air, "golden_pickaxe", new Tool(64e6, 3.0), 3.0),
        [ItemType.DiamondPickaxe] = new("Diamond Pickaxe", 1, BlockType.Air, "diamond_pickaxe", new Tool(120e6, 4.0), 2.0),
        [ItemType.IronPickaxe] = new("Iron Pickaxe", 1, BlockType.Air, "iron_pickaxe", new Tool(80e6, 2.8), 2.5),
        [ItemType.EmeraldPickaxe] = new("Emerald Pickaxe", 1, BlockType.Air, "emerald_pickaxe", new Tool(140e6, 4.5), 2),
        [ItemType.Coal] = new("Coal", StackSize, BlockType.Air, "coal_lump", null, 0.3),
        [ItemType.IronIngot] = new("Iron Ingot", StackSize, BlockType.Air, "iron_ingot", null, 0.3),
        [ItemType.GoldIngot] = new("Gold Ingot", StackSize, BlockType.Air, "gold_ingot", null, 0.3),
        [ItemType.Diamond] = new("Diamond", StackSize, BlockType.Air, "diamond", null, 0.3),
        [ItemType.Emerald] = new("Emerald", StackSize, BlockType.Air, "emerald", null, 0.3),
        [ItemType.Ruby] = new("Ruby", StackSize, BlockType.Air, "ruby", null, 0.3),
        [ItemType.Soul] = new("Soul", StackSize, BlockType.Air, "soul", null, 0.1),
        [ItemType.Cocoa] = new("Cocoa", StackSize, BlockType.Air, "cocoa", null, 0.1),
        [ItemType.Cookie] = new("Cookie", StackSize, BlockType.Air, "cookie", null, 0.1),
        [ItemType.RubyPickaxe] = new("Ruby Pickaxe", 1, BlockType.Air, "ruby_pickaxe", new Tool(160e6, 5.0), 2.0)
    };

    private static readonly ItemInfo[] BlockItems =
    [
        .. Enumerable.Range(0, Blocks.Count).Select(type =>
        {
            BlockMaterial material = Blocks.Get((BlockType)type);
            double volume = BlockShapes.HasShape((BlockType)type) ? BlockShapes.Parts(new Voxel((BlockType)type, 0)).Count() / 8.0 : 1;
            if (BlockShapes.IsDoor((BlockType)type)) volume *= BlockShapes.DoorThickness * 2;
            return new ItemInfo(material.Name, StackSize, (BlockType)type, null, null, material.Density * BlockEdge * BlockEdge * BlockEdge * volume);
        })
    ];

    /// <summary>The items the creative inventory offers, in its order: building blocks, then the rest.</summary>
    public static readonly ItemType[] Creative =
    [.. Enumerable.Range(1, Blocks.Count - 1).Where(i => (BlockType)i != BlockType.AcaciaDoorTop).Select(i => Of((BlockType)i)), .. NonBlocks.Keys];

    /// <summary>The resource obtained by mining a block.</summary>
    public static ItemType DropOf(BlockType block) => block switch
    {
        BlockType.CoalOre => ItemType.Coal,
        BlockType.DiamondOre => ItemType.Diamond,
        BlockType.EmeraldOre => ItemType.Emerald,
        BlockType.RubyOre => ItemType.Ruby,
        BlockType.AcaciaDoorTop => Of(BlockType.AcaciaDoorBottom),
        BlockType.Water or BlockType.Lava or BlockType.Ice => ItemType.None,
        _ => Of(Blocks.DropOf(block))
    };

    /// <summary>The items that are not blocks.</summary>
    public static IEnumerable<ItemType> NonBlockTypes => NonBlocks.Keys;

    public static ItemType Of(BlockType block)
    {
        return (ItemType)(ushort)block;
    }

    /// <summary>Whether a number names an item: a block other than air, or one of the others.</summary>
    public static bool Exists(ItemType type)
    {
        return (ushort)type < FirstNonBlock ? type != ItemType.None && (int)type < Blocks.Count : NonBlocks.ContainsKey(type);
    }

    /// <exception cref="ArgumentOutOfRangeException">No item has that number.</exception>
    public static ItemInfo Get(ItemType type)
    {
        if ((ushort)type < FirstNonBlock && (int)type < Blocks.Count)
            return BlockItems[(int)type];
        if (NonBlocks.TryGetValue(type, out ItemInfo? info))
            return info;

        throw new ArgumentOutOfRangeException(nameof(type), $"No item is numbered {(int)type}");
    }

    /// <summary>What a hand holding this mines with: its tool, or the bare hand's.</summary>
    public static Tool ToolOf(ItemStack held)
    {
        return held.IsEmpty ? Tool.Hand : held.Info.Tool ?? Tool.Hand;
    }
}
