

using Silk.NET.Vulkan;

namespace IntelliCraft;


public static partial class Program
{

    public const uint FramesInFlight = 2;

    private static CommandPool commandPool;
    private static CommandBuffer[] commandBuffers = [];

    private static unsafe void CreateCommandPool()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = graphicsQueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit
        };

        if (vk.CreateCommandPool(device, &poolInfo, null, out commandPool) != Result.Success)
            throw new Exception("Failed to create command pool!");

        commandBuffers = new CommandBuffer[FramesInFlight];
        fixed (CommandBuffer* commandBuffersPtr = commandBuffers)
        {
            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = FramesInFlight
            };

            if (vk.AllocateCommandBuffers(device, &allocInfo, commandBuffersPtr) != Result.Success)
                throw new Exception("Failed to allocate primary command buffers!");
        }

        Console.WriteLine("Command pool created successfully!");
    }

    private static unsafe void DestroyCommandPool()
    {
        vk.DestroyCommandPool(device, commandPool, null);
    }

    // Allocates and begins a temporary primary command buffer for a single
    // one-off operation (buffer copies, layout transitions performed once at
    // startup) — mirrors Mesh3D's own inline staging-copy pattern, exposed
    // here so Tester-level setup code can reuse it too.
    private static unsafe CommandBuffer BeginOneShotCommands()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = commandPool,
            CommandBufferCount = 1
        };

        vk.AllocateCommandBuffers(device, &allocInfo, out CommandBuffer cmd);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        vk.BeginCommandBuffer(cmd, &beginInfo);

        return cmd;
    }

    private static unsafe void EndOneShotCommands(CommandBuffer cmd)
    {
        vk.EndCommandBuffer(cmd);

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd
        };

        vk.QueueSubmit(graphicsQueue, 1, &submitInfo, default);
        vk.QueueWaitIdle(graphicsQueue);

        vk.FreeCommandBuffers(device, commandPool, 1, &cmd);
    }

}
