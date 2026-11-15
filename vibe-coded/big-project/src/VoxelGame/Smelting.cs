using GlmSharp;

namespace VoxelGame;

internal sealed record SmeltingRecipe(ItemType Input, ItemType Output, double Seconds = 10);

/// <summary>A furnace's paid work queue and completed output. Progress advances during play.</summary>
internal sealed class Furnace
{
    public static readonly SmeltingRecipe[] Recipes =
    [
        new(Items.Of(BlockType.IronOre), ItemType.IronIngot),
        new(Items.Of(BlockType.GoldOre), ItemType.GoldIngot),
        new(Items.Of(BlockType.Sand), Items.Of(BlockType.Glass)),
        new(Items.Of(BlockType.Cobblestone), Items.Of(BlockType.Stone))
    ];

    public Inventory Output { get; } = new();
    public ItemType Input { get; set; }
    public ItemType Pending { get; set; }
    public int Queued { get; set; }
    public double Remaining { get; set; }
    public double FuelSeconds { get; set; }
    public bool IsSmelting => Queued > 0 && (Remaining > 0 || Output.Slots[0].IsEmpty
        || (Output.Slots[0].Type == Pending && Output.Slots[0].Count < Items.StackSize));
    public double Progress => Queued == 0 ? 0 : 1 - Remaining / 10;

    public bool Start(Inventory inventory, SmeltingRecipe recipe)
    {
        if (inventory.Count(recipe.Input) < 1 || Queued >= Items.StackSize || (Queued > 0 && Pending != recipe.Output))
            return false;
        // Reserve one job's fuel up front. Coal smelts eight items; wood smelts one.
        if (FuelSeconds < recipe.Seconds)
        {
            ItemType fuel = inventory.Count(ItemType.Coal) > 0 ? ItemType.Coal :
                new[] { BlockType.OakPlanks, BlockType.AcaciaPlanks, BlockType.OakLog, BlockType.AcaciaLog }
                    .Select(Items.Of).FirstOrDefault(t => inventory.Count(t) > (t == recipe.Input ? 1 : 0));
            if (fuel == ItemType.None)
                return false;
            inventory.Remove(fuel, 1);
            FuelSeconds += fuel == ItemType.Coal ? 80 : 10;
        }
        inventory.Remove(recipe.Input, 1);
        FuelSeconds -= recipe.Seconds;
        Input = recipe.Input;
        Pending = recipe.Output;
        if (Queued++ == 0)
            Remaining = recipe.Seconds;
        return true;
    }

    public void Update(double seconds)
    {
        while (Queued > 0 && seconds > 0)
        {
            double advance = Math.Min(seconds, Remaining);
            seconds -= advance;
            Remaining -= advance;
            if (Remaining > 0)
                break;
            ItemStack output = Output.Slots[0];
            if (!output.IsEmpty && (output.Type != Pending || output.Count >= Items.StackSize))
                break;
            Output.Slots[0] = new ItemStack(Pending, output.IsEmpty ? 1 : output.Count + 1);
            if (--Queued > 0)
                Remaining = 10;
            else
                Pending = ItemType.None;
        }
    }
}

internal sealed partial class World
{
    // Map coordinates survive column unloading; moving object stations retain their body reference.
    public Dictionary<(VoxelBody Body, ivec3 Cell), Furnace> Furnaces { get; } = [];

    public Furnace FurnaceAt(VoxelHit hit)
    {
        var key = (hit.Body, hit.Cell);
        if (!Furnaces.TryGetValue(key, out Furnace? furnace))
            Furnaces[key] = furnace = new Furnace();
        return furnace;
    }

    private void BreakFurnace(VoxelBody body, ivec3 cell)
    {
        if (!Furnaces.Remove((body, cell), out Furnace? furnace)) return;
        dvec3 center = body.ToWorld(VoxelBody.CellCenter(cell));
        if (furnace.Queued > 0)
            DropItem(new ItemStack(furnace.Input, furnace.Queued), center, body.LinearVelocity);
        foreach (ItemStack stack in furnace.Output.Slots)
            if (!stack.IsEmpty) DropItem(stack, center, body.LinearVelocity);
    }

    public void UpdateFurnaces(double seconds)
    {
        foreach (var (key, furnace) in Furnaces)
        {
            furnace.Update(seconds);
            Voxel voxel = key.Body.Grid[key.Cell];
            if (voxel.Type != BlockType.Furnace || voxel.Lit == furnace.IsSmelting)
                continue;
            key.Body.Grid[key.Cell] = voxel with { Lit = furnace.IsSmelting };
            key.Body.MeshDirty.Add(MYT.Kit.Vulkan.Grid3D<Voxel>.ChunkOf(key.Cell));
            NoteEdit(key.Body, key.Cell);
        }
    }
}
