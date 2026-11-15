namespace VoxelGame;


/// <summary>Part of what a recipe costs: <paramref name="Count"/> of any of some items, mixed as they come, such as planks of any wood.</summary>
/// <param name="Name">What the cost is called when it accepts more than one item.</param>
internal sealed record Ingredient(string Name, ItemType[] Accepts, int Count)
{
    public static Ingredient Of(ItemType type, int count)
    {
        return new Ingredient(Items.Get(type).Name, [type], count);
    }

    public static Ingredient Of(BlockType block, int count)
    {
        return Of(Items.Of(block), count);
    }

    /// <summary>How many of what it accepts an inventory holds.</summary>
    public int Available(Inventory inventory)
    {
        return Accepts.Sum(inventory.Count);
    }
}


/// <summary>Something to craft, how many of it, what it costs, and whether it needs a opened workbench.</summary>
internal sealed record Recipe(ItemStack Result, Ingredient[] Cost, bool NeedsWorkbench);


/// <summary>Recipes paid from the inventory; large recipes require an opened workbench.</summary>
internal static class Crafting
{
    private static readonly Ingredient Planks = new("Planks", [Items.Of(BlockType.OakPlanks), Items.Of(BlockType.AcaciaPlanks)], 0);
    private static readonly Ingredient Sticks = Ingredient.Of(ItemType.Stick, 2);

    public static readonly Recipe[] Recipes =
    [
        .. Enumerable.Range((int)BlockType.BedrockSlab, BlockShapes.Bases.Length * 4)
            .Where(i => !Blocks.Get((BlockType)i).Unbreakable)
            .Select(i => new Recipe(new ItemStack(Items.Of((BlockType)i), 1),
                [Ingredient.Of(BlockShapes.BaseOf((BlockType)i), 1)], true)),
        new(new ItemStack(Items.Of(BlockType.OakPlanks), 4), [Ingredient.Of(BlockType.OakLog, 1)], false),
        new(new ItemStack(Items.Of(BlockType.AcaciaPlanks), 4), [Ingredient.Of(BlockType.AcaciaLog, 1)], false),
        new(new ItemStack(ItemType.Stick, 4), [Planks with { Count = 2 }], false),
        new(new ItemStack(Items.Of(BlockType.Workbench), 1), [Planks with { Count = 4 }], false),
        new(new ItemStack(Items.Of(BlockType.Cobblestone), 1), [Ingredient.Of(BlockType.Stone, 1)], false),
        new(new ItemStack(Items.Of(BlockType.Furnace), 1), [Ingredient.Of(BlockType.Cobblestone, 8)], true),
        new(new ItemStack(ItemType.WoodenPickaxe, 1), [Planks with { Count = 3 }, Sticks], true),
        new(new ItemStack(ItemType.StonePickaxe, 1), [Ingredient.Of(BlockType.Cobblestone, 3), Sticks], true),
        new(new ItemStack(ItemType.GoldenPickaxe, 1), [Ingredient.Of(ItemType.GoldIngot, 3), Sticks], true),
        new(new ItemStack(ItemType.DiamondPickaxe, 1), [Ingredient.Of(ItemType.Diamond, 3), Sticks], true),
        new(new ItemStack(ItemType.IronPickaxe, 1), [Ingredient.Of(ItemType.IronIngot, 3), Sticks], true),
        new(new ItemStack(ItemType.EmeraldPickaxe, 1), [Ingredient.Of(ItemType.Emerald, 3), Sticks], true),
        new(new ItemStack(ItemType.RubyPickaxe, 1), [Ingredient.Of(ItemType.Ruby, 3), Sticks], true),
        new(new ItemStack(Items.Of(BlockType.IronBlock), 1), [Ingredient.Of(ItemType.IronIngot, 9)], true),
        new(new ItemStack(ItemType.IronIngot, 9), [Ingredient.Of(BlockType.IronBlock, 1)], false),
        new(new ItemStack(Items.Of(BlockType.AcaciaDoorBottom), 3), [Ingredient.Of(BlockType.AcaciaPlanks, 6)], true),
        new(new ItemStack(ItemType.Cookie, 4), [Ingredient.Of(ItemType.Cocoa, 2)], false)
    ];

    /// <summary>Whether the inventory holds a recipe's cost; anything is affordable with unlimited resources.</summary>
    public static bool Affords(Inventory inventory, Recipe recipe, bool unlimited)
    {
        return unlimited || recipe.Cost.All(cost => cost.Available(inventory) >= cost.Count);
    }

    /// <summary>
    /// Crafts a recipe once: takes its cost from the inventory, unless resources are
    /// unlimited, and adds what it makes. Returns false, changing nothing, if the inventory
    /// cannot pay for it, or has no room for what it makes once paid.
    /// </summary>
    public static bool Craft(Inventory inventory, Recipe recipe, bool unlimited)
    {
        if (!Affords(inventory, recipe, unlimited))
            return false;

        Inventory trial = inventory.Copy();
        if (!unlimited)
        {
            foreach (Ingredient cost in recipe.Cost)
                Pay(trial, cost);
        }

        if (!trial.Add(recipe.Result).IsEmpty)
            return false;

        trial.Slots.CopyTo(inventory.Slots, 0);
        return true;
    }

    /// <summary>Takes a cost from the inventory, of the items it accepts in their order.</summary>
    private static void Pay(Inventory inventory, Ingredient cost)
    {
        int left = cost.Count;
        foreach (ItemType type in cost.Accepts)
        {
            int taken = Math.Min(left, inventory.Count(type));
            inventory.Remove(type, taken);
            left -= taken;
        }
    }
}
