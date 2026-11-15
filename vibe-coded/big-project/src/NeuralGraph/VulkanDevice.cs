using Silk.NET.Vulkan;
using System.Runtime.InteropServices;

namespace NeuralGraph;

/// <summary>Owns a Vulkan 1.1 compute/graphics queue. No window-system dependency.</summary>
public sealed unsafe class VulkanDevice : IDisposable
{
    public Vk Api { get; } = Vk.GetApi();
    public Instance Instance { get; private set; }
    public PhysicalDevice PhysicalDevice { get; private set; }
    public Device Device { get; private set; }
    public Queue Queue { get; private set; }
    public uint QueueFamily { get; private set; }
    public CommandPool CommandPool { get; private set; }
    public string Name { get; private set; } = "";
    public PhysicalDeviceType DeviceType { get; private set; }
    public int DeviceIndex { get; private set; }
    public IReadOnlyList<string> AvailableDevices => availableDevices;
    private readonly List<string> availableDevices = [];
    private bool disposed;
    public static void Check(Result result) { if (result != Result.Success) throw new InvalidOperationException($"Vulkan: {result}"); }

    public VulkanDevice(string[]? instanceExtensions = null, bool presentation = false, int deviceIndex = -1, bool graphics = false)
    {
        try
        {
            using var extensions = new NativeStrings(instanceExtensions ?? []);
            var app = new ApplicationInfo { SType = StructureType.ApplicationInfo, ApiVersion = Vk.Version11 };
            var create = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &app,
                EnabledExtensionCount = (uint)extensions.Count, PpEnabledExtensionNames = extensions.Pointer };
            Check(Api.CreateInstance(in create, null, out var instance)); Instance = instance;
            uint count = 0; Check(Api.EnumeratePhysicalDevices(Instance, &count, null));
            if (count == 0) throw new InvalidOperationException("No Vulkan devices found.");
            var devices = new PhysicalDevice[count]; fixed (PhysicalDevice* p = devices) Check(Api.EnumeratePhysicalDevices(Instance, &count, p));
            int best = int.MinValue;
            for (int d = 0; d < devices.Length; d++)
            {
                var physical = devices[d]; Api.GetPhysicalDeviceProperties(physical, out var props);
                string deviceName = Marshal.PtrToStringUTF8((nint)props.DeviceName) ?? "Vulkan device";
                availableDevices.Add($"{d}: {deviceName} ({props.DeviceType})");
                if (deviceIndex >= 0 && deviceIndex != d) continue;
                uint families = 0; Api.GetPhysicalDeviceQueueFamilyProperties(physical, &families, null);
                var qs = new QueueFamilyProperties[families]; fixed (QueueFamilyProperties* p = qs) Api.GetPhysicalDeviceQueueFamilyProperties(physical, &families, p);
                for (uint q = 0; q < families; q++)
                {
                    var required = QueueFlags.ComputeBit | (presentation || graphics ? QueueFlags.GraphicsBit : 0);
                    if ((qs[q].QueueFlags & required) != required) continue;
                    int score = props.DeviceType switch { PhysicalDeviceType.DiscreteGpu => 300, PhysicalDeviceType.IntegratedGpu => 200, PhysicalDeviceType.Cpu => 0, _ => 100 };
                    if (score <= best) continue;
                    best = score; DeviceIndex = d; PhysicalDevice = physical; QueueFamily = q; DeviceType = props.DeviceType;
                    Name = deviceName;
                }
            }
            if (best == int.MinValue) throw new InvalidOperationException("No suitable Vulkan queue/device index.");
            float priority = 1;
            var queueInfo = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = QueueFamily, QueueCount = 1, PQueuePriorities = &priority };
            using var deviceExtensions = new NativeStrings(presentation ? ["VK_KHR_swapchain"] : []);
            var info = new DeviceCreateInfo { SType = StructureType.DeviceCreateInfo, QueueCreateInfoCount = 1, PQueueCreateInfos = &queueInfo,
                EnabledExtensionCount = (uint)deviceExtensions.Count, PpEnabledExtensionNames = deviceExtensions.Pointer };
            Check(Api.CreateDevice(PhysicalDevice, in info, null, out var device)); Device = device;
            Api.GetDeviceQueue(Device, QueueFamily, 0, out var queue); Queue = queue;
            var poolInfo = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = QueueFamily, Flags = CommandPoolCreateFlags.ResetCommandBufferBit };
            Check(Api.CreateCommandPool(Device, in poolInfo, null, out var pool)); CommandPool = pool;
        }
        catch { Dispose(); throw; }
    }
    public CommandBuffer AllocateCommand()
    {
        var info = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = CommandPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
        Check(Api.AllocateCommandBuffers(Device, in info, out var command)); return command;
    }
    public void Begin(CommandBuffer command)
    {
        Check(Api.ResetCommandBuffer(command, 0));
        var info = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
        Check(Api.BeginCommandBuffer(command, in info));
    }
    public GpuBuffer CreateBuffer(ulong bytes, BufferUsageFlags usage = BufferUsageFlags.StorageBufferBit, bool preferDeviceLocal = false, bool preferHostCached = false) => new(this, bytes, usage, preferDeviceLocal, preferHostCached:preferHostCached);
    public GpuBuffer CreateDeviceBuffer(ulong bytes, BufferUsageFlags usage = BufferUsageFlags.StorageBufferBit) => new(this, bytes, usage, true, deviceOnly:true);
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (Device.Handle != 0) { Api.DeviceWaitIdle(Device); if (CommandPool.Handle != 0) Api.DestroyCommandPool(Device, CommandPool, null); Api.DestroyDevice(Device, null); Device = default; }
        if (Instance.Handle != 0) { Api.DestroyInstance(Instance, null); Instance = default; }
        Api.Dispose();
    }
    private sealed class NativeStrings : IDisposable
    {
        private readonly nint[] strings;
        public byte** Pointer { get; }
        public int Count => strings.Length;
        public NativeStrings(string[] values)
        {
            strings = values.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
            Pointer = (byte**)NativeMemory.Alloc((nuint)Math.Max(1, values.Length), (nuint)sizeof(nint));
            for (int i = 0; i < strings.Length; i++) Pointer[i] = (byte*)strings[i];
        }
        public void Dispose() { foreach (var p in strings) Marshal.FreeCoTaskMem(p); NativeMemory.Free(Pointer); }
    }
}

public sealed unsafe class GpuBuffer : IDisposable
{
    private readonly VulkanDevice context;
    public Silk.NET.Vulkan.Buffer Buffer { get; private set; }
    public DeviceMemory Memory { get; private set; }
    public ulong Size { get; }
    public MemoryPropertyFlags MemoryProperties { get; private set; }
    private void* mapped;
    public Span<float> Floats => mapped != null ? new(mapped, checked((int)(Size / 4))) : throw new InvalidOperationException("Device-only memory must be accessed through a transfer buffer.");
    public Span<int> Ints => mapped != null ? new(mapped, checked((int)(Size / 4))) : throw new InvalidOperationException("Device-only memory must be accessed through a transfer buffer.");
    internal GpuBuffer(VulkanDevice context, ulong size, BufferUsageFlags usage, bool preferDeviceLocal, bool deviceOnly = false, bool preferHostCached = false)
    {
        this.context = context; Size = Math.Max(4, size);
        try
        {
            var info = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = Size, Usage = usage, SharingMode = SharingMode.Exclusive };
            VulkanDevice.Check(context.Api.CreateBuffer(context.Device, in info, null, out var buffer)); Buffer = buffer;
            context.Api.GetBufferMemoryRequirements(context.Device, Buffer, out var req);
            context.Api.GetPhysicalDeviceMemoryProperties(context.PhysicalDevice, out var props);
            uint type = uint.MaxValue;int bestScore=-1;
            var required = deviceOnly ? MemoryPropertyFlags.DeviceLocalBit : MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
            for (uint i = 0; i < props.MemoryTypeCount; i++)
                if ((req.MemoryTypeBits & (1u << (int)i)) != 0 &&
                    (props.MemoryTypes[(int)i].PropertyFlags & required) == required)
                {
                    var flags=props.MemoryTypes[(int)i].PropertyFlags;
                    int score=(preferDeviceLocal&&(flags&MemoryPropertyFlags.DeviceLocalBit)!=0?2:0)
                        +(preferHostCached&&(flags&MemoryPropertyFlags.HostCachedBit)!=0?1:0);
                    if(score>bestScore){type=i;bestScore=score;}
                }
            if (type == uint.MaxValue) throw new NotSupportedException(deviceOnly ? "Device-local storage memory is required." : "Host-visible coherent storage memory is required.");
            MemoryProperties=props.MemoryTypes[(int)type].PropertyFlags;
            var allocation = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = type };
            VulkanDevice.Check(context.Api.AllocateMemory(context.Device, in allocation, null, out var memory)); Memory = memory;
            VulkanDevice.Check(context.Api.BindBufferMemory(context.Device, Buffer, Memory, 0));
            if (!deviceOnly)
            {
                void* pointer; VulkanDevice.Check(context.Api.MapMemory(context.Device, Memory, 0, Size, 0, &pointer)); mapped = pointer;
            }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        if (mapped != null) { context.Api.UnmapMemory(context.Device, Memory); mapped = null; }
        if (Buffer.Handle != 0) { context.Api.DestroyBuffer(context.Device, Buffer, null); Buffer = default; }
        if (Memory.Handle != 0) { context.Api.FreeMemory(context.Device, Memory, null); Memory = default; }
    }
}
