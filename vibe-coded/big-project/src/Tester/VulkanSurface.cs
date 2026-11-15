


using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace IntelliCraft;


public static partial class Program
{

    private static SurfaceKHR surface;
    private static KhrSurface khrSurface = null!;

    private static unsafe void CreateSurface()
    {
        var surfaceHandle = new VkNonDispatchableHandle();

        var result = (Result)Glfw.CreateWindowSurface(instance.ToHandle(), Window, null, &surfaceHandle);
        if (result != Result.Success)
        {
            throw new Exception($"Failed to create window surface! Result: {result}");
        }

        surface = surfaceHandle.ToSurface();

        if (!vk.TryGetInstanceExtension(instance, out khrSurface))
        {
            throw new Exception("Failed to load VK_KHR_surface extension!");
        }

        Console.WriteLine("Vulkan surface created successfully!");
    }

    private static unsafe void DestroySurface()
    {
        khrSurface.DestroySurface(instance, surface, null);
    }

}
