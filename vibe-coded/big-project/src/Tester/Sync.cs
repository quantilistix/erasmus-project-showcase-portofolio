

using Silk.NET.Vulkan;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace IntelliCraft;


public static partial class Program
{

    private static Semaphore[] imageAvailableSemaphores = [];
    private static Fence[] inFlightFences = [];
    private static Semaphore[] renderFinishedSemaphores = [];

    private static unsafe void CreateSync()
    {
        imageAvailableSemaphores = new Semaphore[FramesInFlight];
        inFlightFences = new Fence[FramesInFlight];
        renderFinishedSemaphores = new Semaphore[swapchainImages.Length];

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };

        for (int i = 0; i < FramesInFlight; i++)
        {
            if (vk.CreateSemaphore(device, &semaphoreInfo, null, out imageAvailableSemaphores[i]) != Result.Success
                || vk.CreateFence(device, &fenceInfo, null, out inFlightFences[i]) != Result.Success)
                throw new Exception("Failed to create per-frame-in-flight sync objects!");
        }

        for (int i = 0; i < renderFinishedSemaphores.Length; i++)
        {
            if (vk.CreateSemaphore(device, &semaphoreInfo, null, out renderFinishedSemaphores[i]) != Result.Success)
                throw new Exception("Failed to create per-swapchain-image sync objects!");
        }

        Console.WriteLine("Sync objects created successfully!");
    }

    private static unsafe void DestroySync()
    {
        foreach (var semaphore in imageAvailableSemaphores)
            vk.DestroySemaphore(device, semaphore, null);

        foreach (var fence in inFlightFences)
            vk.DestroyFence(device, fence, null);

        foreach (var semaphore in renderFinishedSemaphores)
            vk.DestroySemaphore(device, semaphore, null);
    }

}
