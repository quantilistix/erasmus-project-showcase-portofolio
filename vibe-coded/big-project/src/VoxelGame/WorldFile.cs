using System.IO.Compression;

using GlmSharp;

using MYT.Kit.Vulkan;

namespace VoxelGame;


/// <summary>The player as a save holds it; its inventory is beside it in the <see cref="WorldSnapshot"/>.</summary>
/// <param name="Selected">The hotbar slot held.</param>
internal readonly record struct PlayerState
(
    dvec3 Feet,
    dvec3 Velocity,
    double Yaw,
    double Pitch,
    double Health,
    bool Flying,
    bool Invulnerable,
    dvec3 Spawn,
    int Selected,
    bool InstantBreak
);


/// <summary>A dropped item as a save holds it: what it is, its pose and motion, how long it has lain and how long until it can be picked up.</summary>
internal readonly record struct ItemState
(
    ItemStack Stack,
    dvec3 Position,
    dquat Orientation,
    dvec3 LinearVelocity,
    dvec3 AngularVelocity,
    double Age,
    double PickupDelay
);


/// <summary>An object as a save holds it: its blocks in its own frame, its pose and its motion.</summary>
internal sealed record ObjectState((ivec3 Cell, Voxel Voxel)[] Cells, dvec3 Position, dquat Orientation, dvec3 LinearVelocity, dvec3 AngularVelocity);


/// <summary>
/// Everything of a world that generating it from its seed would not give back: every map
/// column changed since it was generated, loaded or not, the objects, the dropped items, and
/// the player and its inventory (<see cref="Inventory.Size"/> slots). What is passing
/// (impacts being analysed, stresses, contact loads) is left out: it builds up
/// again within a few steps.
/// </summary>
internal sealed record WorldSnapshot(MapColumn[] Columns, ObjectState[] Objects, PlayerState Player, ItemStack[] Inventory, ItemState[] Items)
{
    public FurnaceState[] Furnaces { get; init; } = [];
    public LightChunkState[] Lighting { get; init; } = [];
    public double DaylightSeconds { get; init; }
    public BlockParticles.Particle[] Particles { get; init; } = [];
    public (dvec3 Position, int Frames)[] Flashes { get; init; } = [];
}

/// <summary>RGB9E5 float lighting for all 4096 cells, or one uniform value.</summary>
internal sealed record LightChunkState(ivec3 Coordinate, uint GeometryHash, uint[] Rgb);

internal sealed record FurnaceState(int Body, ivec3 Cell, ItemType Input, ItemType Pending, int Queued, double Remaining, double FuelSeconds, ItemStack Output);


/// <summary>
/// Takes a <see cref="WorldSnapshot"/> of a world and puts one back, and keeps it in a file
/// (world.dat): gzip-compressed binary, written whole to a file beside it first, which then
/// replaces it, so that a save cut short leaves the last one as it was.
/// </summary>
internal static class WorldFile
{
    // "MYTW", little-endian.
    private const uint Magic = 0x5754594D;
    // Version 6 adds the day/moon clock and live particle state. Versions 1–5 remain readable.
    private const int Version = 6;

    /// <summary>
    /// A copy of what a world holds that its seed would not give back. Copies, so that it may
    /// be written on another thread while the world goes on. What the pointer carries on the
    /// inventory screen goes back into the inventory, or where there is no room, lies at the
    /// player's feet.
    /// </summary>
    public static WorldSnapshot Capture(World world, ChunkStreamer streamer)
    {
        Player player = world.Player ?? throw new InvalidOperationException("A world is saved with its player");
        Dictionary<ivec2, MapColumn> columns = streamer.KeptColumns.ToDictionary(c => c.Coordinate);
        foreach (ivec2 column in world.Map.EditedColumns)
        {
            ivec2 key = WorldTopology.Column(column);
            columns[key] = world.CopyColumn(column) with { Coordinate = key };
        }

        ObjectState[] objects =
        [
            .. world.Objects
                .Where(o => o.Grid.Count > 0)
                .Select(o => new ObjectState([.. o.Grid.Occupied()], o.Position, o.Orientation, o.LinearVelocity, o.AngularVelocity))
        ];

        PlayerState state = new
        (
            player.Feet,
            player.Body.LinearVelocity,
            player.Yaw,
            player.Pitch,
            player.Health,
            player.Flying,
            player.Invulnerable,
            player.Spawn,
            player.Inventory.Selected,
            player.InstantBreak
        );

        List<ItemState> items =
        [
            .. world.DroppedItems.Select(i => new ItemState(i.Stack, i.Position, i.Orientation, i.LinearVelocity, i.AngularVelocity, i.Age, i.PickupDelay))
        ];
        Inventory inventory = player.Inventory.Copy();
        ItemStack carried = inventory.Add(inventory.Cursor);
        if (!carried.IsEmpty)
            items.Add(new ItemState(carried, player.Feet + new dvec3(0, 0, 0.5), dquat.Identity, dvec3.Zero, dvec3.Zero, 0.0, 0.0));

        VoxelObject[] savedBodies = [.. world.Objects.Where(o => o.Grid.Count > 0)];
        return new WorldSnapshot([.. columns.Values], objects, state, inventory.Slots, [.. items])
        {
            DaylightSeconds = world.DaylightSeconds,
            Particles = world.Particles.Capture(),
            Flashes = world.Particles.CaptureFlashes(),
            Furnaces = [.. world.Furnaces.Select(pair => new FurnaceState(
                pair.Key.Body == world.Map ? -1 : Array.IndexOf(savedBodies, pair.Key.Body), pair.Key.Cell,
                pair.Value.Input, pair.Value.Pending, pair.Value.Queued, pair.Value.Remaining, pair.Value.FuelSeconds, pair.Value.Output.Slots[0]))]
        };
    }

    /// <summary>
    /// Puts a snapshot into a new world, which has nothing in it yet: the changed columns are
    /// handed to the streamer, which loads them as the player comes near, and the objects and
    /// the player go where they were, moving as they were.
    /// </summary>
    public static void Restore(WorldSnapshot snapshot, World world, ChunkStreamer streamer)
    {
        world.DaylightSeconds = snapshot.DaylightSeconds;
        world.Particles.Restore(snapshot.Particles, snapshot.Flashes);
        foreach (MapColumn column in snapshot.Columns)
            streamer.Keep(column);

        foreach (ObjectState body in snapshot.Objects)
            world.Spawn(body.Cells, body.Position, body.Orientation, body.LinearVelocity, body.AngularVelocity);

        foreach (FurnaceState saved in snapshot.Furnaces)
        {
            VoxelBody body = saved.Body < 0 ? world.Map : world.Objects[saved.Body];
            Furnace furnace = new() { Input = saved.Input, Pending = saved.Pending, Queued = saved.Queued, Remaining = saved.Remaining, FuelSeconds = saved.FuelSeconds };
            furnace.Output.Slots[0] = saved.Output;
            world.Furnaces[(body, saved.Cell)] = furnace;
        }
        PlayerState state = snapshot.Player;
        Player player = world.SpawnPlayer(state.Spawn);
        player.MoveTo(state.Feet);
        player.Body.LinearVelocity = state.Velocity;
        player.Yaw = state.Yaw;
        player.Pitch = state.Pitch;
        player.Health = state.Health;
        player.Flying = state.Flying;
        player.Invulnerable = state.Invulnerable;
        player.InstantBreak = state.InstantBreak;
        snapshot.Inventory.CopyTo(player.Inventory.Slots, 0);
        player.Inventory.Selected = state.Selected;
        foreach (ItemState item in snapshot.Items)
            world.DropItem(item.Stack, item.Position, item.LinearVelocity, item.PickupDelay, item.Orientation, item.AngularVelocity).Age = item.Age;

        world.ApplyTopologyChanges();
    }

    public static void Write(string path, WorldSnapshot snapshot)
    {
        string partial = path + ".partial";
        using (FileStream file = File.Create(partial))
        using (GZipStream compressed = new(file, CompressionLevel.Fastest))
        using (BinaryWriter writer = new(compressed))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(Grid3D<Voxel>.ChunkSize);

            PlayerState player = snapshot.Player;
            Write(writer, player.Feet);
            Write(writer, player.Velocity);
            writer.Write(player.Yaw);
            writer.Write(player.Pitch);
            writer.Write(player.Health);
            writer.Write(player.Flying);
            writer.Write(player.Invulnerable);
            Write(writer, player.Spawn);
            writer.Write(player.Selected);
            writer.Write(player.InstantBreak);

            writer.Write(snapshot.Inventory.Length);
            foreach (ItemStack slot in snapshot.Inventory)
                Write(writer, slot);

            writer.Write(snapshot.Items.Length);
            foreach (ItemState item in snapshot.Items)
            {
                Write(writer, item.Stack);
                Write(writer, item.Position);
                Write(writer, item.Orientation);
                Write(writer, item.LinearVelocity);
                Write(writer, item.AngularVelocity);
                writer.Write(item.Age);
                writer.Write(item.PickupDelay);
            }

            writer.Write(snapshot.Columns.Length);
            foreach (MapColumn column in snapshot.Columns)
            {
                writer.Write(column.Coordinate.x);
                writer.Write(column.Coordinate.y);
                writer.Write(column.Chunks.Length);
                foreach ((int z, Voxel[] cells) in column.Chunks)
                {
                    writer.Write(z);
                    foreach (Voxel voxel in cells)
                        Write(writer, voxel);
                }
            }

            writer.Write(snapshot.Objects.Length);
            foreach (ObjectState body in snapshot.Objects)
            {
                Write(writer, body.Position);
                Write(writer, body.Orientation);
                Write(writer, body.LinearVelocity);
                Write(writer, body.AngularVelocity);
                writer.Write(body.Cells.Length);
                foreach ((ivec3 cell, Voxel voxel) in body.Cells)
                {
                    writer.Write(cell.x);
                    writer.Write(cell.y);
                    writer.Write(cell.z);
                    Write(writer, voxel);
                }
            }
            writer.Write(snapshot.Furnaces.Length);
            foreach (FurnaceState furnace in snapshot.Furnaces)
            {
                writer.Write(furnace.Body);
                writer.Write(furnace.Cell.x); writer.Write(furnace.Cell.y); writer.Write(furnace.Cell.z);
                writer.Write((ushort)furnace.Input); writer.Write((ushort)furnace.Pending);
                writer.Write(furnace.Queued); writer.Write(furnace.Remaining); writer.Write(furnace.FuelSeconds);
                Write(writer, furnace.Output);
            }
            writer.Write(snapshot.Lighting.Length);
            foreach (LightChunkState light in snapshot.Lighting)
            {
                if (light.Rgb.Length != 1 && light.Rgb.Length != 4096) throw new InvalidDataException("Invalid light chunk length");
                writer.Write(light.Coordinate.x); writer.Write(light.Coordinate.y); writer.Write(light.Coordinate.z);
                writer.Write(light.GeometryHash); writer.Write(light.Rgb.Length);
                foreach (uint value in light.Rgb) writer.Write(value);
            }
            writer.Write(snapshot.DaylightSeconds);
            writer.Write(snapshot.Particles.Length);
            foreach (var particle in snapshot.Particles)
            {
                Write(writer, particle.Position); Write(writer, particle.Velocity);
                writer.Write(particle.Remaining); writer.Write(particle.Lifetime);
                Write(writer, particle.Voxel);
                writer.Write(particle.Face); writer.Write(particle.Pixel.x); writer.Write(particle.Pixel.y);
                writer.Write(particle.Effect); writer.Write(particle.Size); writer.Write(particle.Mass); writer.Write(particle.DustShade);
            }
            writer.Write(snapshot.Flashes.Length);
            foreach (var flash in snapshot.Flashes) { Write(writer, flash.Position); writer.Write(flash.Frames); }
        }

        File.Move(partial, path, overwrite: true);
    }

    /// <summary>Reads a snapshot written by <see cref="Write(string, WorldSnapshot)"/>, or by a game that wrote version 1, which had no inventory or items.</summary>
    /// <exception cref="InvalidDataException">The file is not a world save this version reads, or is damaged.</exception>
    public static WorldSnapshot Read(string path)
    {
        using FileStream file = File.OpenRead(path);
        using GZipStream compressed = new(file, CompressionMode.Decompress);
        using BinaryReader reader = new(compressed);
        try
        {
            if (reader.ReadUInt32() != Magic)
                throw new InvalidDataException($"{path} is not a world save");
            int version = reader.ReadInt32();
            if (version < 1 || version > Version)
                throw new InvalidDataException($"{path} is a version {version} world save; this game reads versions 1 to {Version}");
            if (reader.ReadInt32() != Grid3D<Voxel>.ChunkSize)
                throw new InvalidDataException($"{path} was saved with chunks of another size");

            PlayerState player = new
            (
                ReadVector(reader),
                ReadVector(reader),
                reader.ReadDouble(),
                reader.ReadDouble(),
                reader.ReadDouble(),
                reader.ReadBoolean(),
                reader.ReadBoolean(),
                ReadVector(reader),
                reader.ReadInt32(),
                // Every world version 1 saved plays in creative, which breaks in one hit unless told not to.
                version < 2 || reader.ReadBoolean()
            );

            ItemStack[] inventory = new ItemStack[Inventory.Size];
            ItemState[] items = [];
            if (version >= 2)
            {
                int slots = ReadCount(reader);
                if (slots != Inventory.Size)
                    throw new InvalidDataException($"{path} holds an inventory of {slots} slots, not {Inventory.Size}");
                for (int i = 0; i < slots; i++)
                    inventory[i] = ReadStack(reader, path);

                items = new ItemState[ReadCount(reader)];
                for (int i = 0; i < items.Length; i++)
                {
                    ItemStack stack = ReadStack(reader, path);
                    if (stack.IsEmpty)
                        throw new InvalidDataException($"{path} holds a dropped item of nothing");
                    items[i] = new ItemState(stack, ReadVector(reader), ReadQuaternion(reader), ReadVector(reader), ReadVector(reader), reader.ReadDouble(), reader.ReadDouble());
                }
            }

            MapColumn[] columns = new MapColumn[ReadCount(reader)];
            for (int i = 0; i < columns.Length; i++)
            {
                ivec2 coordinate = new(reader.ReadInt32(), reader.ReadInt32());
                (int Z, Voxel[] Cells)[] chunks = new (int, Voxel[])[ReadCount(reader)];
                for (int c = 0; c < chunks.Length; c++)
                {
                    int z = reader.ReadInt32();
                    Voxel[] cells = new Voxel[Grid3D<Voxel>.ChunkVolume];
                    for (int v = 0; v < cells.Length; v++)
                        cells[v] = ReadVoxel(reader, version);
                    foreach (Voxel voxel in cells)
                    {
                        if ((int)voxel.Type >= Blocks.Count)
                            throw new InvalidDataException($"{path} holds a block of an unknown type {(int)voxel.Type}");
                    }

                    chunks[c] = (z, cells);
                }

                columns[i] = new MapColumn(coordinate, chunks);
            }

            ObjectState[] objects = new ObjectState[ReadCount(reader)];
            for (int i = 0; i < objects.Length; i++)
            {
                dvec3 position = ReadVector(reader);
                dquat orientation = ReadQuaternion(reader);
                dvec3 linear = ReadVector(reader);
                dvec3 angular = ReadVector(reader);
                (ivec3, Voxel)[] cells = new (ivec3, Voxel)[ReadCount(reader)];
                for (int c = 0; c < cells.Length; c++)
                {
                    ivec3 cell = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                    BlockType type = (BlockType)reader.ReadUInt16();
                    if ((int)type >= Blocks.Count)
                        throw new InvalidDataException($"{path} holds a block of an unknown type {(int)type}");
                    ushort damage = reader.ReadUInt16();
                    cells[c] = (cell, ReadVoxelState(reader, version, type, damage));
                }

                objects[i] = new ObjectState(cells, position, orientation, linear, angular);
            }

            FurnaceState[] furnaces = [];
            if (version >= 3)
            {
                furnaces = new FurnaceState[ReadCount(reader)];
                for (int i = 0; i < furnaces.Length; i++)
                {
                    int body = reader.ReadInt32();
                    ivec3 cell = new(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                    ItemType input = (ItemType)reader.ReadUInt16(), pending = (ItemType)reader.ReadUInt16();
                    int queued = reader.ReadInt32();
                    double remaining = reader.ReadDouble(), fuel = reader.ReadDouble();
                    ItemStack output = ReadStack(reader, path);
                    if (body < -1 || body >= objects.Length || queued < 0 || queued > 64
                        || !double.IsFinite(remaining) || remaining < 0 || remaining > 10
                        || !double.IsFinite(fuel) || fuel < 0 || fuel > 80
                        || (queued > 0 && !Furnace.Recipes.Any(r => r.Input == input && r.Output == pending)))
                        throw new InvalidDataException($"{path} holds invalid furnace progress");
                    furnaces[i] = new(body, cell, input, pending, queued, remaining, fuel, output);
                }
            }
            LightChunkState[] lighting = [];
            if (version >= 5)
            {
                lighting = new LightChunkState[ReadCount(reader)];
                HashSet<ivec3> seen = [];
                for (int i = 0; i < lighting.Length; i++)
                {
                    ivec3 key = new(reader.ReadInt32(),reader.ReadInt32(),reader.ReadInt32());
                    uint hash = reader.ReadUInt32(); int length = reader.ReadInt32();
                    if ((length != 1 && length != 4096) || !seen.Add(key)) throw new InvalidDataException($"{path} holds invalid light chunks");
                    uint[] rgb = new uint[length]; for (int c = 0; c < length; c++) rgb[c] = reader.ReadUInt32();
                    lighting[i] = new(key,hash,rgb);
                }
            }
            double daylight = 0;
            BlockParticles.Particle[] particles = [];
            (dvec3 Position, int Frames)[] flashes = [];
            if (version >= 6)
            {
                daylight = reader.ReadDouble();
                if (!double.IsFinite(daylight) || daylight < 0 || daylight >= DaylightCycle.RepetitionSeconds)
                    throw new InvalidDataException("Invalid saved daylight clock");
                int count = ReadCount(reader);
                if (count > BlockParticles.MaxParticles) throw new InvalidDataException("Too many saved particles");
                particles = new BlockParticles.Particle[count];
                for (int i = 0; i < count; i++)
                {
                    var p = new BlockParticles.Particle
                    {
                        Position = ReadVector(reader), Velocity = ReadVector(reader),
                        Remaining = reader.ReadDouble(), Lifetime = reader.ReadDouble(), Voxel = ReadVoxel(reader, version),
                        Face = reader.ReadInt32(), Pixel = new(reader.ReadInt32(), reader.ReadInt32()),
                        Effect = reader.ReadInt32(), Size = reader.ReadDouble(), Mass = reader.ReadDouble(), DustShade = reader.ReadSingle()
                    };
                    if (!Finite(p.Position) || !Finite(p.Velocity) || !double.IsFinite(p.Remaining) || p.Remaining <= 0
                        || !double.IsFinite(p.Lifetime) || p.Lifetime < 0 || !double.IsFinite(p.Size) || p.Size < 0
                        || !double.IsFinite(p.Mass) || p.Mass <= 0 || !float.IsFinite(p.DustShade)
                        || p.Effect < 0 || p.Effect > 2 || p.Face < 0 || p.Face >= 6 || (int)p.Voxel.Type >= Blocks.Count
                        || p.Pixel.x < 0 || p.Pixel.y < 0 || p.Pixel.x > BlockParticles.TexturePixels - BlockParticles.ChipPixels
                        || p.Pixel.y > BlockParticles.TexturePixels - BlockParticles.ChipPixels
                        || (p.Effect != 0 && (p.Size <= 0 || p.Lifetime <= 0 || p.Remaining > p.Lifetime)))
                        throw new InvalidDataException("Invalid saved particle state");
                    particles[i] = p;
                }
                count = ReadCount(reader);
                if (count > 4) throw new InvalidDataException("Too many saved explosion flashes");
                flashes = new (dvec3, int)[count];
                for (int i = 0; i < count; i++)
                {
                    dvec3 position = ReadVector(reader); int frames = reader.ReadInt32();
                    if (!Finite(position) || frames < 1 || frames > 2) throw new InvalidDataException("Invalid saved explosion flash");
                    flashes[i] = (position, frames);
                }
            }
            return new WorldSnapshot(columns, objects, player, inventory, items)
            { Furnaces = furnaces, Lighting = lighting, DaylightSeconds = daylight, Particles = particles, Flashes = flashes };
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException($"{path} ends too soon: the save is damaged", exception);
        }
    }

    private static void Write(BinaryWriter writer, Voxel voxel)
    {
        writer.Write((ushort)voxel.Type);
        writer.Write(voxel.Damage);
        writer.Write((byte)voxel.Orientation);
        writer.Write(voxel.Lit);
    }

    private static bool Finite(dvec3 p) => double.IsFinite(p.x) && double.IsFinite(p.y) && double.IsFinite(p.z);

    private static Voxel ReadVoxel(BinaryReader reader, int version)
    {
        BlockType type = (BlockType)reader.ReadUInt16();
        ushort damage = reader.ReadUInt16();
        return ReadVoxelState(reader, version, type, damage);
    }

    private static Voxel ReadVoxelState(BinaryReader reader, int version, BlockType type, ushort damage)
    {
        if (version < 4) return new Voxel(type, damage);
        BlockOrientation orientation = (BlockOrientation)reader.ReadByte();
        bool lit = reader.ReadBoolean();
        if (!Enum.IsDefined(orientation))
            throw new InvalidDataException("A world save holds an unknown block orientation");
        return new Voxel(type, damage, orientation, lit);
    }

    private static void Write(BinaryWriter writer, dvec3 vector)
    {
        writer.Write(vector.x);
        writer.Write(vector.y);
        writer.Write(vector.z);
    }

    private static void Write(BinaryWriter writer, dquat rotation)
    {
        writer.Write(rotation.x);
        writer.Write(rotation.y);
        writer.Write(rotation.z);
        writer.Write(rotation.w);
    }

    private static void Write(BinaryWriter writer, ItemStack stack)
    {
        writer.Write(stack.IsEmpty ? (ushort)0 : (ushort)stack.Type);
        writer.Write(stack.IsEmpty ? 0 : stack.Count);
    }

    private static dvec3 ReadVector(BinaryReader reader)
    {
        return new dvec3(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
    }

    private static dquat ReadQuaternion(BinaryReader reader)
    {
        return new dquat(reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());
    }

    private static ItemStack ReadStack(BinaryReader reader, string path)
    {
        ItemType type = (ItemType)reader.ReadUInt16();
        int count = reader.ReadInt32();
        if (type == ItemType.None && count == 0)
            return ItemStack.Empty;
        if (!Items.Exists(type) || count < 1 || count > Items.Get(type).MaxStack)
            throw new InvalidDataException($"{path} holds {count} of an unknown item {(int)type}, or more than a stack");

        return new ItemStack(type, count);
    }

    private static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0)
            throw new InvalidDataException("A world save holds a negative count: it is damaged");

        return count;
    }
}
