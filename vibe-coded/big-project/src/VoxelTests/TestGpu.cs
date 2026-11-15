using PhysicsTests;

namespace VoxelTests;


/// <summary>The headless GPU the tests share, made on first use, and where the shaders are.</summary>
internal static class TestGpu
{
    private static HeadlessVulkan? _vulkan;
    private static VoxelGame.ItemSprites? _sprites;

    public static HeadlessVulkan Vulkan => _vulkan ??= new HeadlessVulkan(enableValidation: Environment.GetEnvironmentVariable("MYT_VALIDATION") != "0");

    /// <summary>assets/shaders of the repository.</summary>
    public static string Shaders { get; } = FindShaders();

    /// <summary>The items' images from assets/images, which dropped items are shaped from.</summary>
    public static VoxelGame.ItemSprites Sprites => _sprites ??= new VoxelGame.ItemSprites(Path.Combine(Shaders, "..", "images"));

    public static void Dispose()
    {
        _vulkan?.Dispose();
        _vulkan = null;
    }

    private static string FindShaders()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "assets", "shaders", "physics")))
            directory = directory.Parent;

        return Path.Combine(directory!.FullName, "assets", "shaders");
    }
}
