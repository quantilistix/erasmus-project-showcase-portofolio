using GlmSharp;

using MYT.Kit.Vulkan;

namespace VoxelGame;


/// <summary>A chunk column of the map, generated or saved: its chunks by chunk z, each in <see cref="Grid3D{T}.IndexInChunk"/> order.</summary>
internal sealed record MapColumn(ivec2 Coordinate, (int Z, Voxel[] Cells)[] Chunks);


/// <summary>Seeded terrain with seas, river channels, lakes, caves, ore veins and trees.
/// Each column is deterministic and safe to generate on a background thread.</summary>
internal sealed class TerrainGenerator
{
    public const int SpawnSize = 64;
    public const int DefaultSeed = 1;
    private const int ChunkSize = Grid3D<Voxel>.ChunkSize;
    // How far (m) beyond the spawn area the terrain takes to blend into the generated one.
    private const double BlendWidth = 40.0;
    private const double MaxHeight = 220.0;
    public const int SeaLevel = 63;
    // Mountain tops are bare stone from this height up.
    private const int RockLine = 112;
    // A cell this much higher or lower than one beside it is a cliff, of bare stone.
    private const int CliffStep = 3;
    // Trees are scattered one per cell of this grid at most, kept off its borders, so two are
    // never closer than 5 m.
    private const int TreeGrid = 8;
    private const int TreeInset = 2;
    // How far (m) from its trunk a tree reaches, its roots and canopy, and an acacia's bent trunk.
    private const int TreeReach = 4;
    // Ore veins occupy a small region and follow smoothly varying depth probabilities.
    private const int VeinRegion = 4;

    public enum Biome
    {
        Plains,
        Forest,
        Savanna,
        Desert,
        Mountains
    }

    private enum TreeKind
    {
        Oak,
        Acacia
    }

    /// <summary>A tree to grow: where its trunk meets the ground, its shape, its height and which way an acacia bends.</summary>
    private readonly record struct Tree(ivec3 Base, TreeKind Kind, int Trunk, int Bend);

    private static readonly ivec3[] Sideways = [new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0)];

    /// <summary>The ground of the spawn area: the top block's height at (x, y).</summary>
    public static int GroundHeight(int x, int y)
    {
        double hills = 2.2 * Math.Sin(x * 0.11 + 0.7) * Math.Cos(y * 0.09 - 0.4)
                     + 1.4 * Math.Sin((x + 2.0 * y) * 0.05)
                     + 1.0 * Math.Cos((x - y) * 0.13);
        return 7 + (int)Math.Round(hills);
    }

    /// <summary>Where the player starts: in the spawn area, looking at the structures.</summary>
    public static dvec3 SpawnPoint => new(SpawnSize / 2.0 + 0.5, 12.5, GroundHeight(SpawnSize / 2, 12) + 2.7);

    /// <summary>The chunk columns the spawn area spans.</summary>
    public static IEnumerable<ivec2> SpawnColumns()
    {
        for (int x = 0; x < SpawnSize / ChunkSize; x++)
        for (int y = 0; y < SpawnSize / ChunkSize; y++)
            yield return new ivec2(x, y);
    }

    /// <summary>The spawn area of the default seed, its structures and objects: the world the tests stand on.</summary>
    public static void Generate(World world)
    {
        new TerrainGenerator(DefaultSeed, testFixture: true).BuildSpawn(world, testing: true);
    }

    public static IEnumerable<(ivec3 Cell, BlockType Type)> Box(ivec3 size, BlockType type)
    {
        for (int x = 0; x < size.x; x++)
        for (int y = 0; y < size.y; y++)
        for (int z = 0; z < size.z; z++)
            yield return (new ivec3(x, y, z), type);
    }

    /// <summary>
    /// How far (m) a point lies outside the spawn area, along x or y, whichever is farther;
    /// zero or less inside.
    /// </summary>
    private static int OutsideSpawn(int x, int y)
    {
        return Math.Max(Math.Max(-x, x - (SpawnSize - 1)), Math.Max(-y, y - (SpawnSize - 1)));
    }

    /// <summary>How far the generated terrain has taken over from the spawn area's, from 0 inside it to 1.</summary>
    private static double Blend(int x, int y)
    {
        return Noise.SmoothStep(0.0, BlendWidth, OutsideSpawn(x, y));
    }

    public TerrainGenerator(int seed, bool testFixture = false)
    {
        Seed = seed;
        _testFixture = testFixture;
    }

    private readonly bool _testFixture;
    public dvec3 PlayerSpawn => new(32.5, 12.5, Height(32, 12) + 2.7);

    public int Seed { get; }

    /// <summary>The height of the top block at (x, y), above the bedrock at z = 0.</summary>
    public int Height(int x, int y)
    {
        x = WorldTopology.Wrap(x);
        y = WorldTopology.Wrap(y);
        double blend = Blend(x, y);
        int spawn = GroundHeight(x, y) + (_testFixture ? 0 : 64);
        if (blend <= 0.0)
            return spawn;

        return (int)Math.Round(spawn + blend * (GeneratedHeight(x, y) - spawn));
    }

    /// <summary>The biome at (x, y).</summary>
    public Biome BiomeOf(int x, int y)
    {
        return BiomeAt(WorldTopology.Wrap(x), WorldTopology.Wrap(y), Height(x, y));
    }

    /// <summary>
    /// Loads spawn terrain. The optional test fixture includes the demonstration structures.
    /// </summary>
    public void BuildSpawn(World world, bool testing = false)
    {
        foreach (ivec2 column in SpawnColumns())
            world.LoadColumn(GenerateColumn(column));

        if (!testing)
            return;

        // Sand around a hollow, which cannot hold an overhang.
        for (int x = 46; x < 54; x++)
        for (int y = 44; y < 52; y++)
            world.SetBlock(world.Map, new ivec3(x, y, GroundHeight(x, y)), BlockType.Sand);

        foreach (ivec3 at in (ivec3[])[new(12, 40, 0), new(18, 47, 0), new(50, 20, 0)])
        {
            ivec3 root = new(at.x, at.y, Ground(at));
            Grow(new Tree(root, TreeKind.Oak, 5, 0), (cell, voxel) => world.SetBlock(world.Map, cell, voxel.Type, voxel.Orientation), world.Map.Grid.IsOccupied);
        }

        Bridge(world, new ivec3(20, 24, 0));
        Cantilever(world, new ivec3(42, 30, 0));
        Gallows(world, new ivec3(30, 34, 0));
        GlassWall(world, new ivec3(24, 16, 0));

        // Loose objects to push around, drop and break.
        world.Spawn(Box(new ivec3(3, 3, 3), BlockType.OakPlanks), new dvec3(36, 20, GroundHeight(36, 20) + 1.05), dquat.Identity);
        world.Spawn
        (
            Box(new ivec3(4, 1, 1), BlockType.Stone).Concat(Box(new ivec3(1, 1, 3), BlockType.Stone).Select(b => (b.Cell + new ivec3(0, 0, 1), b.Type))),
            new dvec3(26, 30, GroundHeight(26, 30) + 1.05),
            dquat.Identity
        );
    }

    /// <summary>
    /// One chunk column: bedrock, stone, the biome's soil and the trees that reach into it,
    /// those rooted in the columns around included, so that columns generated apart meet.
    /// </summary>
    public MapColumn GenerateColumn(ivec2 column)
    {
        ivec2 requested = column;
        column = WorldTopology.Column(column);
        ivec2 origin = column * ChunkSize;

        // Heights with a border of one, which tells cliffs.
        const int Bordered = ChunkSize + 2;
        int[] heights = new int[Bordered * Bordered];
        for (int j = 0; j < Bordered; j++)
        for (int i = 0; i < Bordered; i++)
            heights[i + Bordered * j] = Height(origin.x + i - 1, origin.y + j - 1);

        List<Tree> trees = TreesBetween(origin - TreeReach, origin + ChunkSize + TreeReach);
        int highest = Math.Max(SeaLevel, heights.Max());
        foreach (Tree tree in trees)
            highest = Math.Max(highest, tree.Base.z + tree.Trunk + 2);

        Voxel[][] chunks = new Voxel[highest / ChunkSize + 1][];
        for (int k = 0; k < chunks.Length; k++)
            chunks[k] = new Voxel[Grid3D<Voxel>.ChunkVolume];

        for (int ly = 0; ly < ChunkSize; ly++)
        for (int lx = 0; lx < ChunkSize; lx++)
        {
            int x = origin.x + lx;
            int y = origin.y + ly;
            int top = heights[lx + 1 + Bordered * (ly + 1)];
            int steepest = 0;
            foreach (ivec3 side in Sideways)
                steepest = Math.Max(steepest, Math.Abs(heights[lx + 1 + side.x + Bordered * (ly + 1 + side.y)] - top));

            Biome biome = BiomeAt(x, y, top);
            bool rocky = top > RockLine + 18 * Noise.Fractal(x, y, 75, 3, Seed + 101)
                || (steepest >= CliffStep && Blend(x, y) >= 0.5);
            int soil = 2 + (int)(3 * Noise.Unit(x, y, Seed + 103));
            for (int z = 0; z <= Math.Max(top, SeaLevel); z++)
            {
                BlockType type;
                if (z > top)
                    type = BlockType.Water;
                else if (z <= 4 && Noise.Unit(x, y, Seed + z * 7919) < (5 - z) / 5.0)
                    type = BlockType.Bedrock;
                else if (!_testFixture && z > 4 && z < top - 10 && IsCave(x, y, z))
                    type = BlockType.Air;
                else
                {
                    type = top < SeaLevel + 2 && top - z < 2 ? BlockType.Sand
                        : !rocky && biome != Biome.Desert && top - z > 0 && top - z <= soil ? BlockType.Dirt
                        : Layer(biome, rocky, top - z);
                    if (type == BlockType.Stone && !_testFixture)
                        type = OreAt(x, y, z);
                }
                if (_testFixture)
                    type = z > top ? BlockType.Air : z == 0 ? BlockType.Bedrock : Layer(biome, rocky, top - z);
                chunks[z / ChunkSize][Grid3D<Voxel>.IndexInChunk(new ivec3(lx, ly, z))] = new Voxel(type, 0);
            }
        }

        ivec3 low = new(origin, 0);
        ivec3 high = new(origin + ChunkSize, chunks.Length * ChunkSize);
        bool Inside(ivec3 cell) => cell.x >= low.x && cell.y >= low.y && cell.z >= low.z && cell.x < high.x && cell.y < high.y && cell.z < high.z;
        ref Voxel At(ivec3 cell) => ref chunks[cell.z / ChunkSize][Grid3D<Voxel>.IndexInChunk(cell)];

        foreach (Tree tree in trees)
        {
            Grow
            (
                tree,
                (cell, voxel) =>
                {
                    if (Inside(cell))
                        At(cell) = voxel;
                },
                cell => Inside(cell) && At(cell).Type != BlockType.Air
            );
        }

        List<(int, Voxel[])> filled = [];
        for (int k = 0; k < chunks.Length; k++)
        {
            if (chunks[k].Any(v => v.Type != BlockType.Air))
                filled.Add((k, chunks[k]));
        }

        return new MapColumn(requested, [.. filled]);
    }

    /// <summary>
    /// The generated terrain's height at (x, y), before blending into the spawn area: a broad
    /// swell of lowlands and uplands, hills on it, and where a slow mask allows, ridged
    /// mountain ranges.
    /// </summary>
    private double GeneratedHeight(double x, double y)
    {
        double continent = Noise.Fractal(x, y, 1100.0, 3, Seed + 11);
        double hills = Noise.Fractal(x, y, 220.0, 4, Seed + 23);
        double detail = Noise.Fractal(x, y, 40.0, 2, Seed + 37);
        double mountainous = Noise.SmoothStep(-0.05, 0.3, Noise.Fractal(x, y, 900.0, 2, Seed + 41));
        double ridges = Noise.Ridged(x, y, 260.0, 4, Seed + 53);

        double height = 72.0 + 48.0 * continent + 12.0 * hills + 3.0 * detail;
        height += mountainous * (10.0 + 80.0 * ridges * ridges);
        // Continuous river channels, isolated lake basins, and broad submerged seas.
        double river = Math.Abs(Noise.Fractal(x + 1700, y - 900, 420, 3, Seed + 107));
        double channel = 1 - Noise.SmoothStep(0.012, 0.06, river);
        height += channel * (Math.Min(height, SeaLevel - 5) - height);
        double lake = Noise.SmoothStep(0.34, 0.52, Noise.Fractal(x, y, 150, 3, Seed + 109));
        height += lake * (Math.Min(height, SeaLevel - 9) - height);
        return Math.Clamp(height, 12.0, MaxHeight);
    }

    /// <summary>
    /// The biome at a point, from slow noises of temperature and humidity, with a little fast
    /// noise so that borders wander. High ground is mountains whatever the climate, and the
    /// edge of the spawn area is plains, like the area itself.
    /// </summary>
    internal Biome BiomeAt(int x, int y, int height)
    {
        x = WorldTopology.Wrap(x);
        y = WorldTopology.Wrap(y);
        if (Blend(x, y) < 0.5)
            return Biome.Plains;
        if (height >= RockLine + 18 * Noise.Fractal(x, y, 75, 3, Seed + 101))
            return Biome.Mountains;

        double wander = 0.06 * Noise.Fractal(x, y, 24.0, 2, Seed + 59);
        double temperature = Noise.Fractal(x, y, 700.0, 2, Seed + 61) + wander;
        double humidity = Noise.Fractal(x, y, 600.0, 2, Seed + 67) - wander;
        if (temperature > 0.18)
            return humidity < 0.0 ? Biome.Desert : Biome.Savanna;

        return humidity > 0.12 ? Biome.Forest : Biome.Plains;
    }

    /// <summary>
    /// The block <paramref name="depth"/> cells under the surface (0 is the top block). Under
    /// the desert's sand lies dirt, never stone: sand pressed down by the sand over it, bonded
    /// beside a stone block that does not give, would shear off at its weight.
    /// </summary>
    private static BlockType Layer(Biome biome, bool rocky, int depth)
    {
        if (rocky)
            return BlockType.Stone;
        if (biome == Biome.Desert)
            return depth < 2 ? BlockType.Sand : depth < 4 ? BlockType.Dirt : BlockType.Stone;

        return depth == 0 ? BlockType.Grass : depth <= 2 ? BlockType.Dirt : BlockType.Stone;
    }

    /// <summary>Depth-weighted ore veins in generated stone.</summary>
    private BlockType OreAt(int x, int y, int z)
    {
        // Triangular depth profiles keep veins most abundant near their preferred level.
        foreach (var ore in OreProfiles)
        {
            double probability = ore.Odds * Math.Max(0, 1 - Math.Abs(z - ore.Peak) / ore.Spread);
            if (probability > 0 && InVein(x, y, z, Seed + ore.Salt, probability))
                return ore.Type;
        }
        return BlockType.Stone;
    }

    private static readonly (BlockType Type, double Peak, double Spread, double Odds, int Salt)[] OreProfiles =
    [
        (BlockType.DiamondOre, 12, 22, .06, 79),
        (BlockType.GoldOre, 28, 30, .10, 71),
        (BlockType.EmeraldOre, 42, 40, .035, 113),
        (BlockType.RubyOre, 18, 26, .035, 127),
        (BlockType.SoulOre, 8, 14, .025, 131),
        (BlockType.UraniumOre, 20, 28, .035, 137),
        (BlockType.LapisOre, 30, 32, .09, 139),
        (BlockType.BronzeOre, 44, 44, .12, 149),
        (BlockType.IronOre, 48, 48, .20, 151),
        (BlockType.CoalOre, 68, 66, .28, 157)
    ];

    private bool IsCave(int x, int y, int z)
    {
        double a = Noise.Volume(x / 32.0, y / 32.0, z / 24.0, Seed + 163);
        double b = Noise.Volume(x / 32.0, y / 32.0, z / 24.0, Seed + 167);
        bool worm = a * a + b * b < .008;
        // Larger low-frequency pockets create connected underground chambers.
        bool chamber = Noise.Volume(x / 65.0, y / 65.0, z / 38.0, Seed + 173) > .57;
        return worm || chamber;
    }

    /// <summary>
    /// Whether a cell lies in a vein of an ore: its region of <see cref="VeinRegion"/> cells a
    /// side has one at those odds, the cells within 1 to 1.6 m of a point in the region.
    /// </summary>
    private static bool InVein(int x, int y, int z, int seed, double odds)
    {
        ivec3 region = (ivec3)dvec3.Floor(new dvec3(x, y, z) / VeinRegion);
        int regionSeed = seed ^ (region.z * 0x2C1B3C6D);
        if ((Noise.Hash(region.x, region.y, regionSeed) & 0xFFFF) / 65536.0 >= odds)
            return false;

        uint shape = Noise.Hash(region.x, region.y, regionSeed + 1);
        dvec3 spread = new(shape & 0xFF, (shape >> 8) & 0xFF, (shape >> 16) & 0xFF);
        dvec3 center = new dvec3(region * VeinRegion) + 0.5 + (VeinRegion - 1) * spread / 255.0;
        double radius = 1.0 + 0.6 * (shape >> 24) / 255.0;
        return (new dvec3(x, y, z) + 0.5 - center).LengthSqr <= radius * radius;
    }

    /// <summary>Odds that a cell of the tree grid has a tree, by biome.</summary>
    private static double TreeOdds(Biome biome)
    {
        return biome switch
        {
            Biome.Forest => 0.9,
            Biome.Savanna => 0.3,
            Biome.Plains => 0.12,
            _ => 0.0
        };
    }

    /// <summary>
    /// The trees rooted in [low, high) in x and y, at most one per cell of the tree grid,
    /// ordered by where they stand, so that every column grows the trees they share in the
    /// same order and an overlap comes out the same in each. None near the spawn area, which
    /// has its own, nor on cliffs or sand.
    /// </summary>
    private List<Tree> TreesBetween(ivec2 low, ivec2 high)
    {
        List<Tree> trees = [];
        int firstX = (int)Math.Floor(low.x / (double)TreeGrid);
        int firstY = (int)Math.Floor(low.y / (double)TreeGrid);
        int lastX = (int)Math.Floor((high.x - 1) / (double)TreeGrid);
        int lastY = (int)Math.Floor((high.y - 1) / (double)TreeGrid);

        for (int gy = firstY; gy <= lastY; gy++)
        for (int gx = firstX; gx <= lastX; gx++)
        {
            int tx = WorldTopology.Wrap(gx * TreeGrid) / TreeGrid;
            int ty = WorldTopology.Wrap(gy * TreeGrid) / TreeGrid;
            uint hash = Noise.Hash(tx, ty, Seed + 71);
            int span = TreeGrid - 2 * TreeInset;
            int x = gx * TreeGrid + TreeInset + (int)(hash % (uint)span);
            int y = gy * TreeGrid + TreeInset + (int)((hash >> 8) % (uint)span);
            if (x < low.x || y < low.y || x >= high.x || y >= high.y || (_testFixture && OutsideSpawn(x, y) < BlendWidth / 4))
                continue;

            int ground = Height(x, y);
            Biome biome = BiomeAt(x, y, ground);
            if (Noise.Unit(tx, ty, Seed + 73) >= TreeOdds(biome))
                continue;

            int steepest = 0;
            foreach (ivec3 side in Sideways)
                steepest = Math.Max(steepest, Math.Abs(Height(x + side.x, y + side.y) - ground));
            if (ground <= SeaLevel + 1 || steepest >= CliffStep)
                continue;

            TreeKind kind = biome == Biome.Savanna ? TreeKind.Acacia : TreeKind.Oak;
            int trunk = kind == TreeKind.Acacia ? 5 + (int)((hash >> 16) % 2) : 4 + (int)((hash >> 16) % 3);
            trees.Add(new Tree(new ivec3(x, y, ground), kind, trunk, (int)((hash >> 24) % 4)));
        }

        trees.Sort((a, b) => a.Base.y != b.Base.y ? a.Base.y.CompareTo(b.Base.y) : a.Base.x.CompareTo(b.Base.x));
        return trees;
    }

    /// <summary>
    /// Grows a tree through <paramref name="set"/>: logs replace whatever is there, leaves only
    /// fill air, as <paramref name="occupied"/> tells. Both kinds stand on roots that carry them
    /// into the ground and spread their weight in the soil, and hold every leaf on wood or
    /// beside it, since leaves hanging off leaves would snap.
    /// <list type="bullet">
    /// <item>An oak: a straight trunk, branches under the canopy and a round canopy over them.</item>
    /// <item>An acacia: a trunk that bends aside once, and a flat crown of branches spreading
    /// from its top, ringed and roofed with leaves.</item>
    /// </list>
    /// </summary>
    private static void Grow(Tree tree, Action<ivec3, Voxel> set, Func<ivec3, bool> occupied)
    {
        ivec3 at = tree.Base;
        int ground = at.z;
        int top = ground + tree.Trunk;
        Voxel log = new(tree.Kind == TreeKind.Acacia ? BlockType.AcaciaLog : BlockType.OakLog, 0);
        Voxel Along(ivec3 direction) => log with { Orientation = Voxel.Placement(log.Type, direction, dvec3.Zero) };

        foreach (ivec3 side in Sideways)
        {
            set(new ivec3(at.x, at.y, ground - 1) + side, Along(side));
            // Step down through a shared face before extending out again.
            set(new ivec3(at.x, at.y, ground - 2) + side, log);
            set(new ivec3(at.x, at.y, ground - 2) + 2 * side, Along(side));
        }

        if (tree.Kind == TreeKind.Oak)
        {
            for (int z = ground - 3; z <= top; z++)
                set(new ivec3(at.x, at.y, z), log);

            foreach (ivec3 side in Sideways)
            {
                set(new ivec3(at.x, at.y, top - 1) + side, Along(side));
                set(new ivec3(at.x, at.y, top - 1) + 2 * side, Along(side));
            }

            for (int z = top - 1; z <= top + 1; z++)
            {
                int radius = z == top + 1 ? 1 : 2;
                for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                {
                    ivec3 cell = new(at.x + dx, at.y + dy, z);
                    if (!occupied(cell) && !(Math.Abs(dx) == 2 && Math.Abs(dy) == 2))
                        set(cell, new Voxel(BlockType.Leaves, 0));
                }
            }

            return;
        }

        int bend = ground + 2 + tree.Trunk % 2;
        ivec3 aside = Sideways[tree.Bend];
        for (int z = ground - 3; z <= bend; z++)
            set(new ivec3(at.x, at.y, z), log);

        ivec3 crown = new ivec3(at.x, at.y, 0) + aside;
        for (int z = bend; z <= top; z++)
            set(new ivec3(crown.x, crown.y, z), z == bend ? Along(aside) : log);

        for (int dx = -3; dx <= 3; dx++)
        for (int dy = -3; dy <= 3; dy++)
        {
            int distance = Math.Abs(dx) + Math.Abs(dy);
            ivec3 cell = new(crown.x + dx, crown.y + dy, top);
            if (distance <= 2)
            {
                // Keep the crown's center upright; the other logs run out from it.
                ivec3 direction = Math.Abs(dx) >= Math.Abs(dy) ? new(dx, 0, 0) : new(0, dy, 0);
                set(cell, distance == 0 ? log : Along(direction));
            }
            else if (distance == 3 && !occupied(cell))
                set(cell, new Voxel(BlockType.Leaves, 0));

            ivec3 roof = cell + new ivec3(0, 0, 1);
            if (distance <= 2 && !occupied(roof))
                set(roof, new Voxel(BlockType.Leaves, 0));
        }
    }

    // ---------------------------------------------------------------------------------------
    // The spawn area's structures
    // ---------------------------------------------------------------------------------------

    private static int Ground(ivec3 at)
    {
        return GroundHeight(at.x, at.y);
    }

    /// <summary>
    /// A column of one block from the bedrock up to <paramref name="top"/>, replacing the soil:
    /// structures stand on foundations, since grass and dirt shear under a heavy tower.
    /// </summary>
    private static void Column(World world, ivec3 at, int top, BlockType type)
    {
        for (int z = 1; z <= top; z++)
            world.SetBlock(world.Map, new ivec3(at.x, at.y, z), type);
    }

    /// <summary>A plank deck on two stone piers: a beam fixed at both ends.</summary>
    private static void Bridge(World world, ivec3 at)
    {
        const int span = 12;
        int deck = Math.Max(Ground(at), Ground(at + new ivec3(span + 1, 0, 0))) + 6;
        foreach (int x in new[] { 0, span + 1 })
        {
            for (int y = 0; y < 3; y++)
                Column(world, new ivec3(at.x + x, at.y + y, 0), deck, BlockType.Stone);
        }

        for (int x = 1; x <= span; x++)
        for (int y = 0; y < 3; y++)
            world.SetBlock(world.Map, new ivec3(at.x + x, at.y + y, deck), BlockType.OakPlanks);
    }

    /// <summary>A stone tower with a stone beam sticking out: σ = 3ρgL² at its root, 3.75 MPa of its 6 at L = 7.</summary>
    private static void Cantilever(World world, ivec3 at)
    {
        int ground = Ground(at);
        int top = ground + 7;
        for (int x = 0; x < 2; x++)
        for (int y = 0; y < 2; y++)
            Column(world, new ivec3(at.x + x, at.y + y, 0), top, BlockType.Stone);

        for (int x = 2; x < 9; x++)
            world.SetBlock(world.Map, new ivec3(at.x + x, at.y, top), BlockType.Stone);
    }

    /// <summary>
    /// Two log posts with a plank beam, and two stone blocks hanging from it by one block of
    /// dirt: 51 kPa in a link good for 80. A third stone block snaps it.
    /// </summary>
    private static void Gallows(World world, ivec3 at)
    {
        int ground = Math.Max(Ground(at), Ground(at + new ivec3(4, 0, 0)));
        int beam = ground + 7;
        foreach (int x in new[] { 0, 4 })
        {
            int footing = GroundHeight(at.x + x, at.y);
            Column(world, new ivec3(at.x + x, at.y, 0), footing, BlockType.Stone);
            for (int z = footing + 1; z < beam; z++)
                world.SetBlock(world.Map, new ivec3(at.x + x, at.y, z), BlockType.OakLog);
        }

        for (int x = 0; x <= 4; x++)
            world.SetBlock(world.Map, new ivec3(at.x + x, at.y, beam), BlockType.OakPlanks);

        world.SetBlock(world.Map, new ivec3(at.x + 2, at.y, beam - 1), BlockType.Dirt);
        world.SetBlock(world.Map, new ivec3(at.x + 2, at.y, beam - 2), BlockType.Stone);
        world.SetBlock(world.Map, new ivec3(at.x + 2, at.y, beam - 3), BlockType.Stone);
    }

    private static void GlassWall(World world, ivec3 at)
    {
        int ground = Ground(at);
        for (int x = 0; x < 7; x++)
        for (int z = 1; z <= 5; z++)
        {
            bool frame = x == 0 || x == 6 || z == 1 || z == 5;
            int baseHeight = GroundHeight(at.x + x, at.y);
            for (int fill = baseHeight + 1; fill < ground + 1; fill++)
                world.SetBlock(world.Map, new ivec3(at.x + x, at.y, fill), BlockType.Cobblestone);
            world.SetBlock(world.Map, new ivec3(at.x + x, at.y, ground + z), frame ? BlockType.OakPlanks : BlockType.Glass);
        }
    }
}
