using GlmSharp;
using MYT.Kit.Structural;
using MYT.Kit.Vulkan;

namespace VoxelGame;


internal enum BlockType : ushort
{
    Air,
    Bedrock,
    Stone,
    Cobblestone,
    Dirt,
    Grass,
    Sand,
    OakLog,
    OakPlanks,
    AcaciaLog,
    AcaciaPlanks,
    Leaves,
    Glass,
    Workbench,
    // New blocks go last: saves store blocks by these numbers.
    GoldOre,
    DiamondOre,
    CoalOre, IronOre, EmeraldOre, LapisOre, BronzeOre, RubyOre, SoulOre, UraniumOre,
    Furnace, Water,
    BedrockSlab, BedrockStairs, BedrockCornerStairs, BedrockMissingCornerStairs,
    StoneSlab, StoneStairs, StoneCornerStairs, StoneMissingCornerStairs,
    CobblestoneSlab, CobblestoneStairs, CobblestoneCornerStairs, CobblestoneMissingCornerStairs,
    DirtSlab, DirtStairs, DirtCornerStairs, DirtMissingCornerStairs,
    SandSlab, SandStairs, SandCornerStairs, SandMissingCornerStairs,
    OakPlanksSlab, OakPlanksStairs, OakPlanksCornerStairs, OakPlanksMissingCornerStairs,
    AcaciaPlanksSlab, AcaciaPlanksStairs, AcaciaPlanksCornerStairs, AcaciaPlanksMissingCornerStairs,
    GlassSlab, GlassStairs, GlassCornerStairs, GlassMissingCornerStairs,
    Tnt, IronBlock, Ice, Lava, AcaciaDoorBottom, AcaciaDoorTop
}


/// <summary>Stresses a block endures before it starts breaking, Pa: pulled apart, crushed and sheared.</summary>
internal readonly record struct ElasticLimits(double Tension, double Compression, double Shear)
{
    public static readonly ElasticLimits Infinite = new(double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity);

    public static ElasticLimits operator *(ElasticLimits limits, double factor)
    {
        return new ElasticLimits(limits.Tension * factor, limits.Compression * factor, limits.Shear * factor);
    }

    /// <summary>How far past these limits a set of stresses goes: the worst stress over its limit.</summary>
    public double Ratio(CellStress stress)
    {
        return Math.Max(stress.Tension / Tension, Math.Max(stress.Compression / Compression, stress.Shear / Shear));
    }
}


/// <summary>
/// What a block is made of. Densities, moduli, limits, friction and bounce are real-world
/// orders of magnitude for a 1 m block in SI units, with soil limits raised a little so that a
/// one-block overhang of dirt holds while a two-block one does not.
/// <para>
/// A block's health h goes from 1 (intact) to 0 (broken). Its effective stiffness is
/// <see cref="Stiffness"/> × h, and it scales both what the block endures and how much damage
/// it soaks up: the elastic limit is <see cref="Elasticity"/> × stiffness, and damage d (HP)
/// costs it d / stiffness of its <see cref="Durability"/>. A worn block is therefore weaker
/// and wears faster: a fresh block breaks after Durability × Stiffness / 2 HP of damage, and
/// the second half of its health takes a third of the damage the first half did.
/// </para>
/// </summary>
/// <param name="Density">Mass per volume, kg/m³: the block's mass, so its inertia, momentum and impact energy.</param>
/// <param name="Elasticity">The stresses the block endures before it starts breaking at stiffness 1, Pa.</param>
/// <param name="Stiffness">How hard the block is to break: a factor on its elasticity and durability, scaled by its health.</param>
/// <param name="Durability">The block's health, HP.</param>
/// <param name="YoungsModulus">
/// How much the block deforms under load, Pa: how loads spread through a structure and how
/// hard an impact pushes. With <paramref name="PoissonRatio"/>, the shear modulus G = E / 2(1 + ν).
/// </param>
/// <param name="MiningSeconds">Time to mine it by hand from intact.</param>
/// <param name="Friction">
/// Coulomb friction coefficient of its faces, a real-world value for two blocks of it; two
/// different blocks grip with the geometric mean of theirs. The player's legs push and brake on
/// it in proportion to it.
/// </param>
/// <param name="Damping">
/// How much of a bounce it soaks up, from 0 to 1: two blocks of it meeting part at
/// 1 − Damping of the speed they met at, a real-world coefficient of restitution for it. Two
/// different blocks part at the geometric mean of theirs, so soil and sand, which soak up
/// almost everything, stop a block of anything dead.
/// </param>
/// <param name="Granular">
/// Soil and sand: cracking does not get it out of the way, so an impact has to crush it to
/// break it, which takes far more energy than snapping it, and only where the impact strikes
/// it, not under what is struck. Under its own weight it cracks and falls like any block.
/// </param>
internal sealed record BlockMaterial
(
    string Name,
    double Density,
    ElasticLimits Elasticity,
    double Stiffness,
    double Durability,
    double YoungsModulus,
    double PoissonRatio,
    double MiningSeconds,
    double Friction,
    double Damping,
    string East,
    string West,
    string North,
    string South,
    string Top,
    string Bottom,
    bool Transparent = false,
    bool Unbreakable = false,
    bool Granular = false
)
{
    /// <summary>Faces in grid order: +X, -X, +Y, -Y, +Z, -Z.</summary>
    public string Texture(int face) => face switch
    {
        0 => East, 1 => West, 2 => North, 3 => South, 4 => Top, 5 => Bottom,
        _ => throw new ArgumentOutOfRangeException(nameof(face))
    };

    public double ShearModulus => YoungsModulus / (2.0 * (1.0 + PoissonRatio));

    /// <summary>How its faces touch others in the physics.</summary>
    public SurfaceMaterial Surface => new(Friction, 1.0 - Damping);

    public double EffectiveStiffness(double health)
    {
        return Stiffness * health;
    }

    /// <summary>The stresses the block endures at a health before it starts breaking.</summary>
    public ElasticLimits ElasticLimit(double health)
    {
        return Elasticity * EffectiveStiffness(health);
    }

    /// <summary>
    /// How far past its elastic limit at a health a strike stresses the block: the strike
    /// crushes it under the tool's edge at <paramref name="strike"/> (Pa), which also shears
    /// it at half that, the most shear a one-way compression makes. At 1 or below, the strike
    /// bounces off and does nothing. As the block wears its limit falls, so a strike that
    /// bounced off it intact may get through once something else has worn it down.
    /// </summary>
    public double StrikeRatio(double health, double strike)
    {
        return Unbreakable ? 0.0 : ElasticLimit(health).Ratio(new CellStress(0.0, strike, strike / 2.0));
    }

    /// <summary>
    /// Health after taking <paramref name="damage"/> HP. Each bit of damage costs
    /// dD / (Durability × Stiffness × h) of health, so h² falls by 2·damage / (Durability × Stiffness).
    /// </summary>
    public double HealthAfter(double health, double damage)
    {
        double squared = health * health - 2.0 * damage / (Durability * Stiffness);
        return squared > 0.0 ? Math.Sqrt(squared) : 0.0;
    }

    /// <summary>Damage (HP) that breaks the block from a given health; infinite if it cannot break.</summary>
    public double DamageToBreak(double health)
    {
        return Unbreakable ? double.PositiveInfinity : 0.5 * Durability * Stiffness * health * health;
    }

    /// <summary>Damage per second (HP/s) mining deals, so an intact block takes <see cref="MiningSeconds"/>.</summary>
    public double MiningRate => Unbreakable ? 0.0 : DamageToBreak(1.0) / MiningSeconds;

    /// <summary>
    /// Elastic moduli for the stress analysis at a health: they fall with it, as the elastic
    /// limit does, so worn blocks shed load onto their neighbours. A broken-down block keeps
    /// <see cref="Blocks.ResidualModulus"/> of them, which keeps the analysis well posed.
    /// </summary>
    public CellMaterial Elastic(double health)
    {
        double remaining = Blocks.ResidualModulus + (1.0 - Blocks.ResidualModulus) * health;
        return new CellMaterial(YoungsModulus * remaining, ShearModulus * remaining);
    }
}


internal static class Blocks
{
    /// <summary>The fraction of its elastic moduli a block keeps as its health goes to 0.</summary>
    public const double ResidualModulus = 0.2;

    private static readonly BlockMaterial[] BaseMaterials =
    [
        new("Air", 0, default, 1, 0, 1, 0.2, 0, 0, 1, "", "", "", "", "", "", Transparent: true),
        new("Bedrock", 3000, ElasticLimits.Infinite, 1, double.PositiveInfinity, 100e9, 0.25, double.PositiveInfinity, 0.7, 0.6, "bedrock", "bedrock", "bedrock", "bedrock", "bedrock", "bedrock", Unbreakable: true),
        new("Stone", 2600, new(6e6, 100e6, 8e6), 1.0, 1000, 50e9, 0.25, 2.0, 0.7, 0.6, "stone", "stone", "stone", "stone", "stone", "stone"),
        new("Cobblestone", 2300, new(1.6e6, 45e6, 2.2e6), 0.9, 80, 20e9, 0.25, 2.0, 0.8, 0.65, "cobblestone", "cobblestone", "cobblestone", "cobblestone", "cobblestone", "cobblestone"),
        new("Dirt", 1600, new(100e3, 1.9e6, 125e3), 0.8, 200, 50e6, 0.3, 0.6, 0.65, 0.95, "dirt", "dirt", "dirt", "dirt", "dirt", "dirt", Granular: true),
        new("Grass", 1500, new(125e3, 1.9e6, 150e3), 0.8, 220, 50e6, 0.3, 0.7, 0.55, 0.95, "grass_block_side", "grass_block_side", "grass_block_side", "grass_block_side", "grass_block_top", "dirt", Granular: true),
        new("Sand", 1700, new(30e3, 1.4e6, 43e3), 0.7, 4, 30e6, 0.3, 0.5, 0.5, 0.98, "sand", "sand", "sand", "sand", "sand", "sand", Granular: true),
        new("Oak Log", 800, new(6e6, 40e6, 4e6), 1.0, 120, 12e9, 0.3, 1.5, 0.6, 0.5, "oak_log_side", "oak_log_side", "oak_log_side", "oak_log_side", "oak_log_top", "oak_log_top"),
        new("Oak Planks", 200, new(4.5e6, 33e6, 3.3e6), 0.9, 30, 10e9, 0.3, 1.2, 0.4, 0.5, "oak_planks", "oak_planks", "oak_planks", "oak_planks", "oak_planks", "oak_planks"),
        new("Acacia Log", 800, new(6e6, 45e6, 4.5e6), 1.0, 120, 13e9, 0.3, 1.5, 0.6, 0.5, "acacia_log_side", "acacia_log_side", "acacia_log_side", "acacia_log_side", "acacia_log_top", "acacia_log_top"),
        new("Acacia Planks", 200, new(5e6, 35e6, 3.5e6), 0.9, 30, 11e9, 0.3, 1.2, 0.4, 0.5, "acacia_planks", "acacia_planks", "acacia_planks", "acacia_planks", "acacia_planks", "acacia_planks"),
        new("Leaves", 200, new(40e3, 200e3, 40e3), 0.5, 40, 10e6, 0.3, 0.2, 0.3, 0.9, "oak_leaves", "oak_leaves", "oak_leaves", "oak_leaves", "oak_leaves", "oak_leaves"),
        new("Glass", 2500, new(1.5e6, 15e6, 1.5e6), 1.0, 8, 70e9, 0.22, 0.3, 0.1, 0.4, "glass", "glass", "glass", "glass", "glass", "glass", Transparent: true),
        new("Workbench", 700, new(4.5e6, 33e6, 3.3e6), 0.9, 120, 10e9, 0.3, 1.2, 0.4, 0.5, "workbench_side1", "workbench_side1", "workbench_side2", "workbench_side2", "workbench_top", "workbench_bottom"),
        new("Gold Ore", 3000, new(10e6, 150e6, 16e6), 1.0, 1000, 60e9, 0.25, 3.0, 0.7, 0.6, "gold_ore", "gold_ore", "gold_ore", "gold_ore", "gold_ore", "gold_ore"),
        new("Diamond Ore", 3300, new(14e6, 220e6, 26e6), 1.0, 1000, 80e9, 0.22, 4.0, 0.7, 0.6, "diamond_ore", "diamond_ore", "diamond_ore", "diamond_ore", "diamond_ore", "diamond_ore"),
        new("Coal Ore", 2800, new(8e6, 120e6, 12e6), 1, 1000, 55e9, 0.25, 3, 0.7, 0.6, "coal_ore", "coal_ore", "coal_ore", "coal_ore", "coal_ore", "coal_ore"),
        new("Iron Ore", 2800, new(8e6, 120e6, 12e6), 1, 1000, 55e9, 0.25, 3, 0.7, 0.6, "iron_ore", "iron_ore", "iron_ore", "iron_ore", "iron_ore", "iron_ore"),
        new("Emerald Ore", 2800, new(8e6, 120e6, 12e6), 1, 1000, 55e9, 0.25, 3, 0.7, 0.6, "emerald_ore", "emerald_ore", "emerald_ore", "emerald_ore", "emerald_ore", "emerald_ore"),
        new("Lapis Ore", 2800, new(8e6, 120e6, 12e6), 1, 1000, 55e9, 0.25, 3, 0.7, 0.6, "lapis_ore", "lapis_ore", "lapis_ore", "lapis_ore", "lapis_ore", "lapis_ore"),
        new("Bronze Ore", 2800, new(8e6, 120e6, 12e6), 1, 1000, 55e9, 0.25, 3, 0.7, 0.6, "bronze_ore", "bronze_ore", "bronze_ore", "bronze_ore", "bronze_ore", "bronze_ore"),
        new("Ruby Ore", 2800, new(8e6, 120e6, 12e6), 1, 1000, 55e9, 0.25, 3, 0.7, 0.6, "ruby_ore", "ruby_ore", "ruby_ore", "ruby_ore", "ruby_ore", "ruby_ore"),
        new("Soul Ore", 2800, new(8e6, 120e6, 12e6), 1, 1000, 55e9, 0.25, 3, 0.7, 0.6, "soul_ore", "soul_ore", "soul_ore", "soul_ore", "soul_ore", "soul_ore"),
        new("Uranium Ore", 2800, new(8e6, 120e6, 12e6), 1, 1000, 55e9, 0.25, 3, 0.7, 0.6, "uranium_ore", "uranium_ore", "uranium_ore", "uranium_ore", "uranium_ore", "uranium_ore"),
        new("Furnace", 2300, new(1.6e6, 45e6, 2.2e6), 0.9, 120, 20e9, 0.25, 3, 0.8, 0.65, "furnace_side", "furnace_side", "furnace_front", "furnace_side", "furnace_top", "furnace_bottom"),
        new("Water", 1000, ElasticLimits.Infinite, 1, double.PositiveInfinity, 1, 0.3, double.PositiveInfinity, 0, 1, "water", "water", "water", "water", "water", "water", Transparent: true, Unbreakable: true)
        // string Name,
        // double Density,
        // ElasticLimits Elasticity,
        // double Stiffness,
        // double Durability,
        // double YoungsModulus,
        // double PoissonRatio,
        // double MiningSeconds,
        // double Friction,
        // double Damping,
        // string Top,
        // string East, West, North, South,
        // string Bottom,
        // bool Transparent = false,
        // bool Unbreakable = false,
        // bool Granular = false
    ];

    private static readonly BlockMaterial[] Materials = BuildMaterials();

    private static BlockMaterial[] BuildMaterials()
    {
        List<BlockMaterial> materials = [.. BaseMaterials];
        foreach (BlockType type in BlockShapes.Bases)
        foreach (string shape in new[] { "Slab", "Stairs", "Corner Stairs", "Missing Corner Stairs" })
            materials.Add(BaseMaterials[(int)type] with { Name = $"{BaseMaterials[(int)type].Name} {shape}" });
        // Append after the existing shape variants to preserve every saved block number.
        materials.AddRange
        ([
            BaseMaterials[(int)BlockType.OakPlanks] with
            {
                Name = "TNT", East = "tnt_side", West = "tnt_side", North = "tnt_side", South = "tnt_side",
                Top = "tnt_top", Bottom = "tnt_bottom"
            },
            new("Iron Block", 7870, new(200e6, 250e6, 120e6), 1, 1000, 200e9, 0.3, 5, 0.6, 0.6,
                "iron_block", "iron_block", "iron_block", "iron_block", "iron_block", "iron_block"),
            BaseMaterials[(int)BlockType.Glass] with
            {
                Name = "Ice", Density = 917, Friction = 0.02, East = "ice", West = "ice", North = "ice", South = "ice",
                Top = "ice", Bottom = "ice"
            },
            BaseMaterials[(int)BlockType.Water] with
            {
                Name = "Lava", Density = 2800, East = "lava", West = "lava", North = "lava", South = "lava", Top = "lava", Bottom = "lava"
            },
            BaseMaterials[(int)BlockType.AcaciaPlanks] with
            {
                Name = "Acacia Door", East = "acacia_door_side", West = "acacia_door_side",
                North = "acacia_door_bottom", South = "acacia_door_bottom", Top = "acacia_door_side", Bottom = "acacia_door_side"
            },
            BaseMaterials[(int)BlockType.AcaciaPlanks] with
            {
                Name = "Acacia Door (Top)", East = "acacia_door_side", West = "acacia_door_side",
                North = "acacia_door_top", South = "acacia_door_top", Top = "acacia_door_side", Bottom = "acacia_door_side"
            }
        ]);
        return [.. materials];
    }

    public static BlockMaterial Get(BlockType type)
    {
        return Materials[(int)type];
    }

    public static int Count => Materials.Length;

    public static bool IsSolid(BlockType type) => type is not (BlockType.Air or BlockType.Water or BlockType.Lava);

    /// <summary>The block a mined block drops: grass loses its turf and drops dirt, every other block drops itself.</summary>
    public static BlockType DropOf(BlockType type)
    {
        return type == BlockType.Grass ? BlockType.Dirt : type;
    }

    /// <summary>Every texture name the blocks use, so the renderer can load them as layers.</summary>
    public static IEnumerable<string> TextureNames()
    {
        return Materials.Skip(1).SelectMany(m => Enumerable.Range(0, 6).Select(m.Texture)).Append("furnace_front_lit").Distinct();
    }
}


/// <summary>
/// A block rotation in the body frame. Default is upright with its front toward +Y.
/// </summary>
internal enum BlockOrientation : byte
{
    Default, East, South, West, LogX, LogY,
    DoorOpenNorth = 16, DoorOpenEast, DoorOpenSouth, DoorOpenWest,
    SlabBottom = 32, SlabTop, SlabWest, SlabEast, SlabSouth, SlabNorth,
    StairsNorth = 64, StairsEast, StairsSouth, StairsWest,
    StairsNorthTop, StairsEastTop, StairsSouthTop, StairsWestTop
}

/// <summary>A cell’s block, accumulated damage, orientation and furnace appearance.</summary>
internal readonly record struct Voxel(BlockType Type, ushort Damage, BlockOrientation Orientation = BlockOrientation.Default, bool Lit = false)
{
    public static readonly Voxel Air = default;

    public static BlockOrientation Placement(BlockType type, ivec3 normal, dvec3 towardPlayer, dvec3? hitPoint = null)
    {
        if (BlockShapes.IsPartial(type)) return BlockShapes.Placement(type, normal, towardPlayer, hitPoint ?? new dvec3(0.5));
        if (type is BlockType.OakLog or BlockType.AcaciaLog)
            return normal.x != 0 ? BlockOrientation.LogX : normal.y != 0 ? BlockOrientation.LogY : BlockOrientation.Default;
        if (type == BlockType.Furnace || BlockShapes.IsDoor(type))
            return Math.Abs(towardPlayer.x) > Math.Abs(towardPlayer.y)
                ? towardPlayer.x > 0 ? BlockOrientation.East : BlockOrientation.West
                : towardPlayer.y < 0 ? BlockOrientation.South : BlockOrientation.Default;
        return BlockOrientation.Default;
    }

    // Inverse of the block's rotation, applied to a direction or a centered corner.
    private BlockOrientation TextureOrientation => BlockShapes.IsDoor(Type)
        ? (BlockOrientation)((int)Orientation & 3) : Orientation;

    private ivec3 TextureLocal(ivec3 p) => TextureOrientation switch
    {
        BlockOrientation.East => new(-p.y, p.x, p.z),
        BlockOrientation.South => new(-p.x, -p.y, p.z),
        BlockOrientation.West => new(p.y, -p.x, p.z),
        BlockOrientation.LogX => new(-p.z, p.y, p.x),
        BlockOrientation.LogY => new(p.x, -p.z, p.y),
        _ => p
    };

    public int TextureFace(int face)
    {
        if (BlockShapes.IsPartial(Type) || Orientation == BlockOrientation.Default) return face;
        ivec3 normal = TextureLocal(Grid3D<Voxel>.FaceDirections[face]);
        return normal.x != 0 ? (normal.x > 0 ? 0 : 1)
            : normal.y != 0 ? (normal.y > 0 ? 2 : 3) : (normal.z > 0 ? 4 : 5);
    }

    public int TextureRotation(int face)
    {
        if (BlockShapes.IsPartial(Type) || Orientation == BlockOrientation.Default) return 0;
        ivec3 corner = (TextureLocal(VoxelMesher.FaceCorners[face][0] * 2 - 1) + 1) / 2;
        return Array.IndexOf(VoxelMesher.FaceCorners[TextureFace(face)], corner);
    }

    public int TextureLayer(int[,] layers, int face)
    {
        int localFace = TextureFace(face);
        return layers[(int)Type, Type == BlockType.Furnace && Lit && localFace == 2 ? 6 : localFace];
    }

    public static ushort ToDamage(double fraction)
    {
        return (ushort)Math.Clamp(Math.Round(fraction * ushort.MaxValue), 0, ushort.MaxValue);
    }

    public double DamageFraction => Damage / (double)ushort.MaxValue;

    /// <summary>From 1 (intact) to 0 (broken).</summary>
    public double Health => 1.0 - DamageFraction;

    /// <summary>
    /// The share of the damage that breaks the block intact it has taken, 1 − h², from 0 to 1:
    /// what its cracks show. It grows evenly with damage, so every hit of mining adds as much
    /// to the cracks, while health falls slowly at first and fast at the end.
    /// </summary>
    public double Wear => 1.0 - Health * Health;

    public BlockMaterial Material => Blocks.Get(Type);
}
