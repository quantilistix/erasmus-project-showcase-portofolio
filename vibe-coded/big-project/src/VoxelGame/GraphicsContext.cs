using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.GLFW;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

using Buffer = Silk.NET.Vulkan.Buffer;
using Image = Silk.NET.Vulkan.Image;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace VoxelGame;


/// <summary>The command buffer and swapchain image of the frame being recorded.</summary>
internal readonly record struct Frame(CommandBuffer Commands, uint FrameIndex, uint ImageIndex);


/// <summary>
/// The window's Vulkan setup: instance, device, a queue that draws and presents, queues of
/// their own for the physics and the map's stress analysis (<see cref="PhysicsQueue"/>,
/// <see cref="StressQueue"/>), the swapchain with a depth buffer, and per-frame
/// synchronization for <see cref="FramesInFlight"/> frames. Recreates the swapchain when the
/// window is resized.
/// </summary>
internal sealed unsafe class GraphicsContext : IDisposable
{
    public const uint FramesInFlight = 2;
    // The drawing queue, then the physics' and the stress analysis'.
    private const uint WantedQueues = 3;
    public const Format DepthFormat = Format.D32Sfloat;
    public const Format ColorFormat = Format.B8G8R8A8Srgb;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Bool32 OnDebugMessage
    (
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* callbackData,
        void* userData
    )
    {
        Console.Error.WriteLine($"[Vulkan] {Marshal.PtrToStringUTF8((nint)callbackData->PMessage)}");
        return Vk.False;
    }

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"{operation} failed with {result}");
    }

    public GraphicsContext(Glfw glfw, WindowHandle* window, bool validation)
    {
        _glfw = glfw;
        _window = window;
        Vk = Vk.GetApi();
        Validation = validation && IsValidationLayerAvailable();

        CreateInstance();
        CreateSurface();
        PickDevice();
        CreateDevice();
        CreateCommandResources();
        CreateSwapchain();
        CreateSync();
    }

    private readonly Glfw _glfw;
    private readonly WindowHandle* _window;
    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _messenger;
    private KhrSurface _khrSurface = null!;
    private KhrSwapchain _khrSwapchain = null!;
    private SurfaceKHR _surface;
    private SwapchainKHR _swapchain;
    private Image[] _images = [];
    private ImageView[] _imageViews = [];
    private Image _depthImage;
    private DeviceMemory _depthMemory;
    private ImageView _depthView;
    private CommandBuffer[] _commandBuffers = [];
    private Semaphore[] _imageAvailable = [];
    private Semaphore[] _renderFinished = [];
    private Fence[] _inFlight = [];
    private uint _frameIndex;
    private bool _resized;
    private QueryPool _renderQueries;
    private readonly bool[] _renderTimed = new bool[FramesInFlight];
    private float _timestampPeriod;
    private ulong _timestampMask;
    public double RenderGpuMilliseconds { get; private set; }
    public PresentModeKHR PresentMode { get; private set; } = PresentModeKHR.FifoKhr;
    // Queues created in the drawing queue's family, and those beyond the first with their pools.
    private uint _queueCount = 1;
    private readonly List<(Queue Queue, uint Family)> _ownQueues = [];
    // A compute-only family for the stress analysis, when the device has one; uint.MaxValue otherwise.
    private uint _stressFamily = uint.MaxValue;
    private bool _globalPriority;
    private readonly List<CommandPool> _ownPools = [];

    public Vk Vk { get; }
    public bool Validation { get; }
    public Instance Instance { get; private set; }
    public PhysicalDevice PhysicalDevice { get; private set; }
    public Device Device { get; private set; }
    public uint QueueFamily { get; private set; }
    public Queue Queue { get; private set; }
    public CommandPool CommandPool { get; private set; }

    /// <summary>
    /// The queue the physics runs on, and a command pool for it. A frame's draws wait for a
    /// swapchain image, which with vsync can take until the next refresh, and work submitted
    /// after them on their queue waits with them: on a queue of its own, a physics step waits
    /// only for itself. The drawing queue when the GPU offers no second one.
    /// </summary>
    public (Queue Queue, CommandPool Pool) PhysicsQueue { get; private set; }

    /// <summary>
    /// The queue the map's stress analysis runs on, so that a step never waits for its iterations
    /// either; as <see cref="PhysicsQueue"/>. On a compute-only family at low global priority when
    /// the device has one (MYT_STRESS_FAMILY=0 keeps it beside the others): its long dispatches,
    /// sharing the GPU evenly, starved the step's chain of short ones.
    /// </summary>
    public (Queue Queue, CommandPool Pool) StressQueue { get; private set; }
    public Extent2D Extent { get; private set; }
    public string DeviceName { get; private set; } = "";

    /// <summary>Tells the context the framebuffer changed size; the swapchain is rebuilt before the next frame.</summary>
    public void NotifyResized()
    {
        _resized = true;
    }

    /// <summary>Polls a submitted frame without waiting for its slot to be reused.</summary>
    internal bool IsFrameComplete(uint frame) => Vk.GetFenceStatus(Device, _inFlight[frame]) == Result.Success;

    /// <summary>
    /// Waits for this frame slot, acquires an image and begins recording into it, outside any
    /// rendering, where transfers go; <see cref="BeginRendering"/> follows. Returns false when
    /// the window is minimized or the swapchain had to be rebuilt; skip the frame then.
    /// </summary>
    public bool BeginFrame(out Frame frame)
    {
        frame = default;
        Fence fence = _inFlight[_frameIndex];
        long waitStart = Prof.On ? Stopwatch.GetTimestamp() : 0;
        Vk.WaitForFences(Device, 1, &fence, true, ulong.MaxValue);
        if (Prof.On) Prof.Add("swap.fence", Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds);
        if (_renderQueries.Handle != 0 && _renderTimed[_frameIndex])
        {
            ulong* times = stackalloc ulong[2];
            Check(Vk.GetQueryPoolResults(Device, _renderQueries, _frameIndex * 2, 2,
                2 * sizeof(ulong), times, sizeof(ulong), QueryResultFlags.Result64Bit), "render timestamps");
            RenderGpuMilliseconds = ((times[1] - times[0]) & _timestampMask) * _timestampPeriod / 1e6;
            Prof.Add("gpu.render", RenderGpuMilliseconds);
        }

        if (_resized)
        {
            RecreateSwapchain();
            return false;
        }

        uint imageIndex = 0;
        long acquireStart = Prof.On ? Stopwatch.GetTimestamp() : 0;
        Result acquired = _khrSwapchain.AcquireNextImage(Device, _swapchain, ulong.MaxValue, _imageAvailable[_frameIndex], default, &imageIndex);
        if (Prof.On) Prof.Add("swap.acquire", Stopwatch.GetElapsedTime(acquireStart).TotalMilliseconds);
        if (acquired == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return false;
        }

        if (acquired != Result.Success && acquired != Result.SuboptimalKhr)
            throw new InvalidOperationException($"Acquiring a swapchain image failed with {acquired}");

        Vk.ResetFences(Device, 1, &fence);
        CommandBuffer cmd = _commandBuffers[_frameIndex];
        Vk.ResetCommandBuffer(cmd, 0);
        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        Vk.BeginCommandBuffer(cmd, &beginInfo);

        ImageBarrier
        (
            cmd, _images[imageIndex], ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags.ColorAttachmentOutputBit, AccessFlags.None,
            PipelineStageFlags.ColorAttachmentOutputBit, AccessFlags.ColorAttachmentWriteBit
        );
        ImageBarrier
        (
            cmd, _depthImage, ImageAspectFlags.DepthBit,
            ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal,
            PipelineStageFlags.LateFragmentTestsBit, AccessFlags.DepthStencilAttachmentWriteBit,
            PipelineStageFlags.EarlyFragmentTestsBit, AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit
        );

        frame = new Frame(cmd, _frameIndex, imageIndex);
        return true;
    }

    /// <summary>
    /// Begins dynamic rendering into the frame's image, its color cleared to
    /// <paramref name="clear"/> and its depth to 0, the far end of the reversed depth range.
    /// </summary>
    public void BeginRenderTiming(Frame frame)
    {
        if (_renderQueries.Handle != 0)
        {
            Vk.CmdResetQueryPool(frame.Commands, _renderQueries, frame.FrameIndex * 2, 2);
            Vk.CmdWriteTimestamp(frame.Commands, PipelineStageFlags.TopOfPipeBit, _renderQueries, frame.FrameIndex * 2);
        }
    }

    public void BeginRendering(Frame frame, ClearColorValue clear,bool startTiming = true)
    {
        if (startTiming) BeginRenderTiming(frame);
        CommandBuffer cmd = frame.Commands;
        RenderingAttachmentInfo color = new()
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _imageViews[frame.ImageIndex],
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(clear)
        };
        RenderingAttachmentInfo depth = new()
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = _depthView,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(0f, 0))
        };
        RenderingInfo rendering = new()
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), Extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &color,
            PDepthAttachment = &depth
        };
        Vk.CmdBeginRendering(cmd, &rendering);

        Viewport viewport = new() { Width = Extent.Width, Height = Extent.Height, MinDepth = 0, MaxDepth = 1 };
        Rect2D scissor = new(new Offset2D(0, 0), Extent);
        Vk.CmdSetViewport(cmd, 0, 1, &viewport);
        Vk.CmdSetScissor(cmd, 0, 1, &scissor);
    }

    /// <summary>When set, the next frame is also saved to this file as a binary PPM image.</summary>
    public string? CapturePath { get; set; }

    /// <summary>Ends rendering, submits the frame and presents it.</summary>
    public void EndFrame(Frame frame)
    {
        CommandBuffer cmd = frame.Commands;
        Vk.CmdEndRendering(cmd);
        if (_renderQueries.Handle != 0)
        {
            Vk.CmdWriteTimestamp(cmd, PipelineStageFlags.BottomOfPipeBit, _renderQueries, frame.FrameIndex * 2 + 1);
            _renderTimed[frame.FrameIndex] = true;
        }

        HostBuffer capture = default;
        if (CapturePath != null)
        {
            capture = CreateHostBuffer((ulong)Extent.Width * Extent.Height * 4, BufferUsageFlags.TransferDstBit);
            ImageBarrier
            (
                cmd, _images[frame.ImageIndex], ImageAspectFlags.ColorBit,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
                PipelineStageFlags.ColorAttachmentOutputBit, AccessFlags.ColorAttachmentWriteBit,
                PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit
            );
            BufferImageCopy region = new()
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(Extent.Width, Extent.Height, 1)
            };
            Vk.CmdCopyImageToBuffer(cmd, _images[frame.ImageIndex], ImageLayout.TransferSrcOptimal, capture.Buffer, 1, &region);
            ImageBarrier
            (
                cmd, _images[frame.ImageIndex], ImageAspectFlags.ColorBit,
                ImageLayout.TransferSrcOptimal, ImageLayout.PresentSrcKhr,
                PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit,
                PipelineStageFlags.BottomOfPipeBit, AccessFlags.None
            );
        }
        else
        {
            ImageBarrier
            (
                cmd, _images[frame.ImageIndex], ImageAspectFlags.ColorBit,
                ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr,
                PipelineStageFlags.ColorAttachmentOutputBit, AccessFlags.ColorAttachmentWriteBit,
                PipelineStageFlags.BottomOfPipeBit, AccessFlags.None
            );
        }

        Vk.EndCommandBuffer(cmd);

        PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        Semaphore wait = _imageAvailable[frame.FrameIndex];
        Semaphore signal = _renderFinished[frame.ImageIndex];
        SubmitInfo submit = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &wait,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signal
        };
        Check(Vk.QueueSubmit(Queue, 1, &submit, _inFlight[frame.FrameIndex]), "vkQueueSubmit");

        if (CapturePath != null)
        {
            Vk.QueueWaitIdle(Queue);
            SaveCapture(capture, CapturePath);
            Destroy(capture);
            CapturePath = null;
        }

        SwapchainKHR swapchain = _swapchain;
        uint imageIndex = frame.ImageIndex;
        PresentInfoKHR present = new()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signal,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex
        };

        long presentStart = Prof.On ? Stopwatch.GetTimestamp() : 0;
        Result presented = _khrSwapchain.QueuePresent(Queue, &present);
        if (Prof.On) Prof.Add("swap.present", Stopwatch.GetElapsedTime(presentStart).TotalMilliseconds);
        if (presented == Result.ErrorOutOfDateKhr || presented == Result.SuboptimalKhr)
            _resized = true;
        else
            Check(presented, "vkQueuePresentKHR");

        _frameIndex = (_frameIndex + 1) % FramesInFlight;
    }

    /// <summary>Writes a captured BGRA swapchain image as a binary PPM.</summary>
    private void SaveCapture(HostBuffer capture, string path)
    {
        ReadOnlySpan<byte> pixels = capture.As<byte>();
        using FileStream file = File.Create(path);
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{Extent.Width} {Extent.Height}\n255\n");
        file.Write(header);

        byte[] row = new byte[Extent.Width * 3];
        for (int y = 0; y < Extent.Height; y++)
        {
            for (int x = 0; x < Extent.Width; x++)
            {
                int source = (y * (int)Extent.Width + x) * 4;
                row[x * 3] = pixels[source + 2];
                row[x * 3 + 1] = pixels[source + 1];
                row[x * 3 + 2] = pixels[source];
            }

            file.Write(row);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Resource helpers
    // ---------------------------------------------------------------------------------------

    public uint FindMemoryType(uint typeBits, MemoryPropertyFlags properties)
    {
        return TryFindMemoryType(typeBits, properties, out uint type) ? type : throw new InvalidOperationException("No suitable memory type");
    }

    private bool TryFindMemoryType(uint typeBits, MemoryPropertyFlags properties, out uint type)
    {
        Vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, out PhysicalDeviceMemoryProperties memory);
        for (int i = 0; i < memory.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << i)) != 0 && (memory.MemoryTypes[i].PropertyFlags & properties) == properties)
            {
                type = (uint)i;
                return true;
            }
        }

        type = 0;
        return false;
    }

    /// <summary>A buffer in host-visible, coherent memory, persistently mapped.</summary>
    public HostBuffer CreateHostBuffer(ulong size, BufferUsageFlags usage)
    {
        BufferCreateInfo info = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = Math.Max(size, 16),
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };
        Check(Vk.CreateBuffer(Device, &info, null, out Buffer buffer), "vkCreateBuffer");
        Vk.GetBufferMemoryRequirements(Device, buffer, out MemoryRequirements requirements);

        MemoryAllocateInfo allocation = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };
        Check(Vk.AllocateMemory(Device, &allocation, null, out DeviceMemory memory), "vkAllocateMemory");
        Vk.BindBufferMemory(Device, buffer, memory, 0);

        void* mapped;
        Vk.MapMemory(Device, memory, 0, Vk.WholeSize, 0, &mapped);
        return new HostBuffer(buffer, memory, (nint)mapped, info.Size);
    }

    /// <summary>
    /// A buffer for the GPU to write and the CPU to read, persistently mapped: host-cached when the
    /// device offers coherent cached memory, since uncached reads cost milliseconds per megabyte.
    /// </summary>
    public HostBuffer CreateReadbackBuffer(ulong size)
    {
        BufferCreateInfo info = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = Math.Max(size, 16),
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive
        };
        Check(Vk.CreateBuffer(Device, &info, null, out Buffer buffer), "vkCreateBuffer");
        Vk.GetBufferMemoryRequirements(Device, buffer, out MemoryRequirements requirements);

        MemoryPropertyFlags coherent = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        if (!TryFindMemoryType(requirements.MemoryTypeBits, coherent | MemoryPropertyFlags.HostCachedBit, out uint type))
            type = FindMemoryType(requirements.MemoryTypeBits, coherent);

        MemoryAllocateInfo allocation = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = type
        };
        Check(Vk.AllocateMemory(Device, &allocation, null, out DeviceMemory memory), "vkAllocateMemory");
        Vk.BindBufferMemory(Device, buffer, memory, 0);

        void* mapped;
        Vk.MapMemory(Device, memory, 0, Vk.WholeSize, 0, &mapped);
        return new HostBuffer(buffer, memory, (nint)mapped, info.Size);
    }

    public void Destroy(HostBuffer buffer)
    {
        if (buffer.Buffer.Handle != 0)
            Vk.DestroyBuffer(Device, buffer.Buffer, null);
        if (buffer.Memory.Handle != 0)
            Vk.FreeMemory(Device, buffer.Memory, null);
    }

    /// <summary>Records commands into a temporary buffer, submits them and waits; for setup only.</summary>
    public void Submit(Action<CommandBuffer> record)
    {
        CommandBufferAllocateInfo allocation = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        Vk.AllocateCommandBuffers(Device, &allocation, out CommandBuffer cmd);

        CommandBufferBeginInfo begin = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        Vk.BeginCommandBuffer(cmd, &begin);
        record(cmd);
        Vk.EndCommandBuffer(cmd);

        SubmitInfo submit = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd
        };
        Check(Vk.QueueSubmit(Queue, 1, &submit, default), "vkQueueSubmit");
        Vk.QueueWaitIdle(Queue);
        Vk.FreeCommandBuffers(Device, CommandPool, 1, &cmd);
    }

    public void ImageBarrier
    (
        CommandBuffer cmd,
        Image image,
        ImageAspectFlags aspect,
        ImageLayout from,
        ImageLayout to,
        PipelineStageFlags sourceStage,
        AccessFlags sourceAccess,
        PipelineStageFlags destinationStage,
        AccessFlags destinationAccess,
        uint layerCount = 1,
        uint levelCount = 1
    )
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, levelCount, 0, layerCount),
            SrcAccessMask = sourceAccess,
            DstAccessMask = destinationAccess
        };
        Vk.CmdPipelineBarrier(cmd, sourceStage, destinationStage, 0, 0, null, 0, null, 1, &barrier);
    }

    public ShaderModule CreateShaderModule(byte[] spirv)
    {
        fixed (byte* code = spirv)
        {
            ShaderModuleCreateInfo info = new()
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code
            };
            Check(Vk.CreateShaderModule(Device, &info, null, out ShaderModule module), "vkCreateShaderModule");
            return module;
        }
    }

    // ---------------------------------------------------------------------------------------
    // Setup
    // ---------------------------------------------------------------------------------------

    private bool IsValidationLayerAvailable()
    {
        uint count = 0;
        Vk.EnumerateInstanceLayerProperties(&count, null);
        LayerProperties[] layers = new LayerProperties[count];
        fixed (LayerProperties* pointer = layers)
            Vk.EnumerateInstanceLayerProperties(&count, pointer);

        foreach (LayerProperties layer in layers)
        {
            if (Marshal.PtrToStringAnsi((nint)layer.LayerName) == "VK_LAYER_KHRONOS_validation")
                return true;
        }

        return false;
    }

    private void CreateInstance()
    {
        byte** glfwExtensions = _glfw.GetRequiredInstanceExtensions(out uint glfwCount);
        List<nint> extensions = [];
        for (int i = 0; i < glfwCount; i++)
            extensions.Add((nint)glfwExtensions[i]);

        nint debugName = Marshal.StringToHGlobalAnsi(ExtDebugUtils.ExtensionName);
        nint layerName = Marshal.StringToHGlobalAnsi("VK_LAYER_KHRONOS_validation");
        nint appName = Marshal.StringToHGlobalAnsi("MYT Voxels");
        if (Validation)
            extensions.Add(debugName);

        nint* extensionNames = stackalloc nint[extensions.Count];
        for (int i = 0; i < extensions.Count; i++)
            extensionNames[i] = extensions[i];

        DebugUtilsMessengerCreateInfoEXT messengerInfo = new()
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt,
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(&OnDebugMessage)
        };

        ApplicationInfo application = new()
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)appName,
            ApplicationVersion = Vk.MakeVersion(1, 0, 0),
            PEngineName = (byte*)appName,
            EngineVersion = Vk.MakeVersion(1, 0, 0),
            ApiVersion = Vk.Version13
        };

        InstanceCreateInfo info = new()
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &application,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = (byte**)extensionNames
        };

        if (Validation)
        {
            info.EnabledLayerCount = 1;
            info.PpEnabledLayerNames = (byte**)&layerName;
            info.PNext = &messengerInfo;
        }

        Check(Vk.CreateInstance(&info, null, out Instance instance), "vkCreateInstance");
        Instance = instance;

        if (Validation && Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils debugUtils))
        {
            _debugUtils = debugUtils;
            Check(debugUtils.CreateDebugUtilsMessenger(Instance, &messengerInfo, null, out _messenger), "vkCreateDebugUtilsMessengerEXT");
        }

        Marshal.FreeHGlobal(debugName);
        Marshal.FreeHGlobal(layerName);
        Marshal.FreeHGlobal(appName);
    }

    private void CreateSurface()
    {
        VkNonDispatchableHandle handle;
        Check((Result)_glfw.CreateWindowSurface(Instance.ToHandle(), _window, null, &handle), "glfwCreateWindowSurface");
        _surface = handle.ToSurface();

        if (!Vk.TryGetInstanceExtension(Instance, out KhrSurface khrSurface))
            throw new InvalidOperationException("VK_KHR_surface is not available");
        _khrSurface = khrSurface;
    }

    private void PickDevice()
    {
        uint count = 0;
        Vk.EnumeratePhysicalDevices(Instance, &count, null);
        PhysicalDevice[] devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* pointer = devices)
            Vk.EnumeratePhysicalDevices(Instance, &count, pointer);

        int bestScore = -1;
        foreach (PhysicalDevice candidate in devices)
        {
            Vk.GetPhysicalDeviceFeatures(candidate, out PhysicalDeviceFeatures features);
            if (!features.ShaderFloat64 || !TryFindQueueFamily(candidate, out uint family, out uint queues))
                continue;

            Vk.GetPhysicalDeviceProperties(candidate, out PhysicalDeviceProperties properties);
            int score = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 1000,
                PhysicalDeviceType.IntegratedGpu => 100,
                _ => 10
            };

            if (score > bestScore)
            {
                bestScore = score;
                PhysicalDevice = candidate;
                QueueFamily = family;
                _queueCount = Math.Min(queues, WantedQueues);
                DeviceName = Marshal.PtrToStringAnsi((nint)properties.DeviceName) ?? "";
                _stressFamily = Environment.GetEnvironmentVariable("MYT_STRESS_FAMILY") == "0" ? uint.MaxValue : FindComputeFamily(candidate);
                _globalPriority = HasDeviceExtension(candidate, "VK_KHR_global_priority");
            }
        }

        if (bestScore < 0)
            throw new InvalidOperationException("No GPU with Vulkan 1.3, double precision shaders and a graphics, compute and present queue");
    }

    /// <summary>A family that computes but does not draw, or uint.MaxValue.</summary>
    private uint FindComputeFamily(PhysicalDevice device)
    {
        uint count = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        QueueFamilyProperties* families = stackalloc QueueFamilyProperties[(int)count];
        Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, families);
        for (uint i = 0; i < count; i++)
        {
            if ((families[i].QueueFlags & (QueueFlags.ComputeBit | QueueFlags.GraphicsBit)) == QueueFlags.ComputeBit
                && families[i].QueueCount > 0 && families[i].TimestampValidBits > 0)
                return i;
        }

        return uint.MaxValue;
    }

    private bool HasDeviceExtension(PhysicalDevice device, string name)
    {
        uint count = 0;
        Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null);
        ExtensionProperties* extensions = stackalloc ExtensionProperties[(int)count];
        Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, extensions);
        for (uint i = 0; i < count; i++)
        {
            if (Marshal.PtrToStringAnsi((nint)extensions[i].ExtensionName) == name)
                return true;
        }

        return false;
    }

    private bool TryFindQueueFamily(PhysicalDevice device, out uint family, out uint queues)
    {
        uint count = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        QueueFamilyProperties[] families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* pointer = families)
            Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, pointer);

        for (uint i = 0; i < count; i++)
        {
            QueueFlags needed = QueueFlags.GraphicsBit | QueueFlags.ComputeBit;
            _khrSurface.GetPhysicalDeviceSurfaceSupport(device, i, _surface, out Bool32 present);
            if ((families[i].QueueFlags & needed) == needed && present)
            {
                family = i;
                queues = families[i].QueueCount;
                return true;
            }
        }

        family = 0;
        queues = 0;
        return false;
    }

    public bool SupportsGeometryShader { get; private set; }
    public bool SupportsMultiDrawIndirect { get; private set; }
    public uint MaxIndirectDrawCount { get; private set; } = 1;

    private void CreateDevice()
    {
        // The stress analysis can wait for the frame and the step, which cannot wait for it.
        float* priorities = stackalloc float[(int)WantedQueues];
        priorities[0] = 1f;
        priorities[1] = 1f;
        priorities[2] = 0f;

        // Its long dispatches on a compute-only family at low global priority, when the device
        // has them: drawn on the same queues, they starved the step's chain of small dispatches.
        bool separate = _stressFamily != uint.MaxValue;
        if (separate)
            _queueCount = Math.Min(_queueCount, WantedQueues - 1);
        float stressPriority = 0f;
        DeviceQueueGlobalPriorityCreateInfoKHR low = new()
        {
            SType = StructureType.DeviceQueueGlobalPriorityCreateInfoKhr,
            GlobalPriority = QueueGlobalPriority.Low
        };
        DeviceQueueCreateInfo* queueInfos = stackalloc DeviceQueueCreateInfo[2];
        queueInfos[0] = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamily,
            QueueCount = _queueCount,
            PQueuePriorities = priorities
        };
        queueInfos[1] = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            PNext = _globalPriority ? &low : null,
            QueueFamilyIndex = _stressFamily,
            QueueCount = 1,
            PQueuePriorities = &stressPriority
        };

        Vk.GetPhysicalDeviceFeatures(PhysicalDevice, out PhysicalDeviceFeatures supported);
        Vk.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties properties);
        SupportsGeometryShader = supported.GeometryShader;
        SupportsMultiDrawIndirect = supported.MultiDrawIndirect && supported.DrawIndirectFirstInstance;
        MaxIndirectDrawCount = SupportsMultiDrawIndirect ? properties.Limits.MaxDrawIndirectCount : 1;
        PhysicalDeviceFeatures features = new()
        {
            ShaderFloat64 = true,
            GeometryShader = SupportsGeometryShader,
            MultiDrawIndirect = SupportsMultiDrawIndirect,
            DrawIndirectFirstInstance = SupportsMultiDrawIndirect
        };
        PhysicalDeviceVulkan13Features features13 = new()
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = true,
            Synchronization2 = true
        };

        nint* extensions = stackalloc nint[2];
        extensions[0] = Marshal.StringToHGlobalAnsi(KhrSwapchain.ExtensionName);
        extensions[1] = Marshal.StringToHGlobalAnsi("VK_KHR_global_priority");
        DeviceCreateInfo info = new()
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &features13,
            QueueCreateInfoCount = separate ? 2u : 1u,
            PQueueCreateInfos = queueInfos,
            PEnabledFeatures = &features,
            EnabledExtensionCount = separate && _globalPriority ? 2u : 1u,
            PpEnabledExtensionNames = (byte**)extensions
        };

        Check(Vk.CreateDevice(PhysicalDevice, &info, null, out Device device), "vkCreateDevice");
        Device = device;
        Marshal.FreeHGlobal(extensions[0]);
        Marshal.FreeHGlobal(extensions[1]);

        Vk.GetDeviceQueue(Device, QueueFamily, 0, out Queue queue);
        Queue = queue;
        for (uint i = 1; i < _queueCount; i++)
        {
            Vk.GetDeviceQueue(Device, QueueFamily, i, out Queue own);
            _ownQueues.Add((own, QueueFamily));
        }

        if (separate)
        {
            Vk.GetDeviceQueue(Device, _stressFamily, 0, out Queue stress);
            _ownQueues.Add((stress, _stressFamily));
        }

        if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain khrSwapchain))
            throw new InvalidOperationException("VK_KHR_swapchain is not available");
        _khrSwapchain = khrSwapchain;
    }

    private void CreateCommandResources()
    {
        if (Prof.On)
        {
            Vk.GetPhysicalDeviceProperties(PhysicalDevice, out PhysicalDeviceProperties properties);
            uint count = 0;
            Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &count, null);
            QueueFamilyProperties* families = stackalloc QueueFamilyProperties[(int)count];
            Vk.GetPhysicalDeviceQueueFamilyProperties(PhysicalDevice, &count, families);
            uint bits = families[QueueFamily].TimestampValidBits;
            if (bits > 0 && properties.Limits.TimestampComputeAndGraphics)
            {
                _timestampPeriod = properties.Limits.TimestampPeriod;
                _timestampMask = bits == 64 ? ulong.MaxValue : (1UL << (int)bits) - 1;
                QueryPoolCreateInfo queryInfo = new()
                {
                    SType = StructureType.QueryPoolCreateInfo,
                    QueryType = QueryType.Timestamp,
                    QueryCount = FramesInFlight * 2
                };
                Check(Vk.CreateQueryPool(Device, &queryInfo, null, out _renderQueries), "render query pool");
            }
        }
        CommandPoolCreateInfo poolInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = QueueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit
        };
        Check(Vk.CreateCommandPool(Device, &poolInfo, null, out CommandPool pool), "vkCreateCommandPool");
        CommandPool = pool;

        // Each queue's own pool: a pool belongs to whichever queue its buffers are submitted to.
        foreach ((Queue _, uint family) in _ownQueues)
        {
            poolInfo.QueueFamilyIndex = family;
            Check(Vk.CreateCommandPool(Device, &poolInfo, null, out CommandPool ownPool), "vkCreateCommandPool");
            _ownPools.Add(ownPool);
        }

        int physics = _ownQueues.FindIndex(q => q.Family == QueueFamily);
        int stress = _ownQueues.FindIndex(q => q.Family == _stressFamily);
        if (stress < 0)
            stress = _ownQueues.FindIndex(physics + 1, q => q.Family == QueueFamily);
        PhysicsQueue = physics >= 0 ? (_ownQueues[physics].Queue, _ownPools[physics]) : (Queue, CommandPool);
        StressQueue = stress >= 0 ? (_ownQueues[stress].Queue, _ownPools[stress]) : PhysicsQueue;

        _commandBuffers = new CommandBuffer[FramesInFlight];
        CommandBufferAllocateInfo allocation = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = FramesInFlight
        };
        fixed (CommandBuffer* pointer = _commandBuffers)
            Check(Vk.AllocateCommandBuffers(Device, &allocation, pointer), "vkAllocateCommandBuffers");
    }

    private void CreateSwapchain()
    {
        _khrSurface.GetPhysicalDeviceSurfaceCapabilities(PhysicalDevice, _surface, out SurfaceCapabilitiesKHR capabilities);

        Extent2D extent = capabilities.CurrentExtent;
        if (extent.Width == uint.MaxValue)
        {
            _glfw.GetFramebufferSize(_window, out int width, out int height);
            extent = new Extent2D
            (
                Math.Clamp((uint)width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                Math.Clamp((uint)height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height)
            );
        }

        Extent = extent;
        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0)
            imageCount = Math.Min(imageCount, capabilities.MaxImageCount);

        PresentMode = PresentModeKHR.FifoKhr;
        if (Environment.GetEnvironmentVariable("MYT_PRESENT_MODE") == "immediate")
        {
            uint modeCount = 0;
            _khrSurface.GetPhysicalDeviceSurfacePresentModes(PhysicalDevice, _surface, &modeCount, null);
            PresentModeKHR* modes = stackalloc PresentModeKHR[(int)modeCount];
            _khrSurface.GetPhysicalDeviceSurfacePresentModes(PhysicalDevice, _surface, &modeCount, modes);
            for (int i = 0; i < modeCount; i++)
                if (modes[i] == PresentModeKHR.ImmediateKhr)
                    PresentMode = modes[i];
        }

        SwapchainCreateInfoKHR info = new()
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = ColorFormat,
            ImageColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            ImageExtent = Extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentMode,
            Clipped = true
        };
        Check(_khrSwapchain.CreateSwapchain(Device, &info, null, out _swapchain), "vkCreateSwapchainKHR");

        uint count = 0;
        _khrSwapchain.GetSwapchainImages(Device, _swapchain, &count, null);
        _images = new Image[count];
        fixed (Image* pointer = _images)
            _khrSwapchain.GetSwapchainImages(Device, _swapchain, &count, pointer);

        _imageViews = [.. _images.Select(image => CreateView(image, ColorFormat, ImageAspectFlags.ColorBit))];
        CreateDepth();
    }

    private void CreateDepth()
    {
        ImageCreateInfo info = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(Extent.Width, Extent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = DepthFormat,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            Samples = SampleCountFlags.Count1Bit
        };
        Check(Vk.CreateImage(Device, &info, null, out _depthImage), "vkCreateImage");
        Vk.GetImageMemoryRequirements(Device, _depthImage, out MemoryRequirements requirements);

        MemoryAllocateInfo allocation = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };
        Check(Vk.AllocateMemory(Device, &allocation, null, out _depthMemory), "vkAllocateMemory");
        Vk.BindImageMemory(Device, _depthImage, _depthMemory, 0);
        _depthView = CreateView(_depthImage, DepthFormat, ImageAspectFlags.DepthBit);
    }

    public ImageView CreateView(Image image, Format format, ImageAspectFlags aspect, ImageViewType type = ImageViewType.Type2D, uint layers = 1, uint levels = 1)
    {
        ImageViewCreateInfo info = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = type,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, levels, 0, layers)
        };
        Check(Vk.CreateImageView(Device, &info, null, out ImageView view), "vkCreateImageView");
        return view;
    }

    private void CreateSync()
    {
        SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };
        FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };

        _imageAvailable = new Semaphore[FramesInFlight];
        _inFlight = new Fence[FramesInFlight];
        for (int i = 0; i < FramesInFlight; i++)
        {
            Check(Vk.CreateSemaphore(Device, &semaphoreInfo, null, out _imageAvailable[i]), "vkCreateSemaphore");
            Check(Vk.CreateFence(Device, &fenceInfo, null, out _inFlight[i]), "vkCreateFence");
        }

        CreateRenderFinishedSemaphores();
    }

    // One per swapchain image: presentation may still wait on an image's semaphore when the
    // frame slot comes round again.
    private void CreateRenderFinishedSemaphores()
    {
        SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };
        _renderFinished = new Semaphore[_images.Length];
        for (int i = 0; i < _images.Length; i++)
            Check(Vk.CreateSemaphore(Device, &semaphoreInfo, null, out _renderFinished[i]), "vkCreateSemaphore");
    }

    private void RecreateSwapchain()
    {
        _glfw.GetFramebufferSize(_window, out int width, out int height);
        if (width == 0 || height == 0)
            return;

        _resized = false;
        Vk.DeviceWaitIdle(Device);
        DestroySwapchain();
        CreateSwapchain();
        CreateRenderFinishedSemaphores();
    }

    private void DestroySwapchain()
    {
        foreach (Semaphore semaphore in _renderFinished)
            Vk.DestroySemaphore(Device, semaphore, null);

        Vk.DestroyImageView(Device, _depthView, null);
        Vk.DestroyImage(Device, _depthImage, null);
        Vk.FreeMemory(Device, _depthMemory, null);
        foreach (ImageView view in _imageViews)
            Vk.DestroyImageView(Device, view, null);
        _khrSwapchain.DestroySwapchain(Device, _swapchain, null);
    }

    public void Dispose()
    {
        Vk.DeviceWaitIdle(Device);
        DestroySwapchain();

        for (int i = 0; i < FramesInFlight; i++)
        {
            Vk.DestroySemaphore(Device, _imageAvailable[i], null);
            Vk.DestroyFence(Device, _inFlight[i], null);
        }

        Vk.DestroyCommandPool(Device, CommandPool, null);
        foreach (CommandPool pool in _ownPools)
            Vk.DestroyCommandPool(Device, pool, null);
        if (_renderQueries.Handle != 0)
            Vk.DestroyQueryPool(Device, _renderQueries, null);
        Vk.DestroyDevice(Device, null);
        _khrSurface.DestroySurface(Instance, _surface, null);
        _debugUtils?.DestroyDebugUtilsMessenger(Instance, _messenger, null);
        Vk.DestroyInstance(Instance, null);
    }
}


/// <summary>A persistently mapped host-visible buffer.</summary>
internal readonly record struct HostBuffer(Buffer Buffer, DeviceMemory Memory, nint Mapped, ulong Size)
{
    public unsafe Span<T> As<T>() where T : unmanaged
    {
        return new Span<T>((void*)Mapped, (int)(Size / (ulong)Unsafe.SizeOf<T>()));
    }
}
