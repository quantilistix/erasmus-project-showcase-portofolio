using System.Text;
using System.Text.Json;

namespace VoxelGame;


/// <summary>A saved world as the world list shows it: the folder it is kept in, its name and seed, when it was made and last played, and how it is played.</summary>
internal sealed record WorldInfo(string Folder, string Name, int Seed, DateTime Created, DateTime LastPlayed, GameMode Mode);


/// <summary>
/// The saved worlds, a folder each in one directory. A world's folder holds world.json, its
/// <see cref="WorldInfo"/>, and once it has been played, world.dat (<see cref="WorldFile"/>).
/// A folder is named after the world when it is made and keeps that name when the world is renamed.
/// </summary>
internal sealed class WorldStore(string directory)
{
    public const string DataFile = "world.dat";
    private const string InfoFile = "world.json";
    private const int MaxFolderLength = 48;

    /// <summary>What world.json holds; the folder is where it is. One written before game modes has none, so creative (0).</summary>
    private sealed record StoredInfo(string Name, int Seed, DateTime Created, DateTime LastPlayed, GameMode Mode);

    /// <summary>A seed from what the player typed: a number as it is, other text hashed as Java hashes strings, nothing a random one.</summary>
    public static int SeedFrom(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return Random.Shared.Next();
        if (int.TryParse(text, out int number))
            return number;

        int hash = 0;
        foreach (char c in text)
            hash = unchecked(31 * hash + c);

        return hash;
    }

    public string Directory => directory;

    /// <summary>The saved worlds, the one played last first. Folders without a readable world.json are left out.</summary>
    public List<WorldInfo> List()
    {
        List<WorldInfo> worlds = [];
        if (!System.IO.Directory.Exists(directory))
            return worlds;

        foreach (string folder in System.IO.Directory.EnumerateDirectories(directory))
        {
            string path = Path.Combine(folder, InfoFile);
            if (!File.Exists(path))
                continue;

            try
            {
                StoredInfo stored = JsonSerializer.Deserialize<StoredInfo>(File.ReadAllText(path))!;
                worlds.Add(new WorldInfo(Path.GetFileName(folder), stored.Name, stored.Seed, stored.Created, stored.LastPlayed, stored.Mode));
            }
            catch (JsonException exception)
            {
                Console.Error.WriteLine($"Leaving out the world in {folder}: its {InfoFile} cannot be read: {exception.Message}");
            }
        }

        worlds.Sort((a, b) => b.LastPlayed.CompareTo(a.LastPlayed));
        return worlds;
    }

    /// <summary>Makes a new world's folder and records it, never played.</summary>
    public WorldInfo Create(string name, int seed, GameMode mode)
    {
        DateTime now = DateTime.Now;
        WorldInfo world = new(FreeFolder(name), name, seed, now, now, mode);
        System.IO.Directory.CreateDirectory(Path.Combine(directory, world.Folder));
        WriteInfo(world);
        return world;
    }

    public WorldInfo Rename(WorldInfo world, string name)
    {
        WorldInfo renamed = world with { Name = name };
        WriteInfo(renamed);
        return renamed;
    }

    /// <summary>Records that the world was played now.</summary>
    public WorldInfo Touch(WorldInfo world)
    {
        WorldInfo touched = world with { LastPlayed = DateTime.Now };
        WriteInfo(touched);
        return touched;
    }

    /// <summary>Deletes a world's folder and everything in it.</summary>
    public void Delete(WorldInfo world)
    {
        System.IO.Directory.Delete(Path.Combine(directory, world.Folder), recursive: true);
    }

    /// <summary>A file in a world's folder.</summary>
    public string PathOf(WorldInfo world, string file)
    {
        return Path.Combine(directory, world.Folder, file);
    }

    /// <summary>
    /// The folder a new world of this name would get: its letters, digits, spaces, dashes and
    /// underscores, the rest as underscores, and a number after it if a folder has that name.
    /// </summary>
    public string FreeFolder(string name)
    {
        StringBuilder safe = new();
        foreach (char c in name.Trim())
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '_');

        string folder = safe.ToString().Trim();
        if (folder.Length > MaxFolderLength)
            folder = folder[..MaxFolderLength].TrimEnd();
        if (folder.Length == 0)
            folder = "World";

        string candidate = folder;
        for (int n = 2; System.IO.Directory.Exists(Path.Combine(directory, candidate)); n++)
            candidate = $"{folder} ({n})";

        return candidate;
    }

    private void WriteInfo(WorldInfo world)
    {
        StoredInfo stored = new(world.Name, world.Seed, world.Created, world.LastPlayed, world.Mode);
        File.WriteAllText(PathOf(world, InfoFile), JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
    }
}
