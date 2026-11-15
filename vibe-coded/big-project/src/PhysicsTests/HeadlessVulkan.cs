using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace PhysicsTests;

/// <summary>
/// Windowless Vulkan context for running GPU physics in tests: an instance, one logical device with a
/// single compute-capable queue (graphics+compute preferred, matching the real app), and a command pool.
/// </summary>
public sealed class HeadlessVulkan : IDisposable
{
    private static ReadOnlySpan<byte> ValidationLayerName => "VK_LAYER_KHRONOS_validation"u8;
    private static ReadOnlySpan<byte> DebugUtilsExtensionName => "VK_EXT_debug_utils"u8;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe Bool32 OnDebugMessage
    (
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* callbackData,
        void* userData
    )
    {
        string? message = Marshal.PtrToStringUTF8((nint)callbackData->PMessage);
        Console.Error.WriteLine($"[Vulkan Validation] {message}");

        if (severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt))
        {
            HeadlessVulkan owner = (HeadlessVulkan)GCHandle.FromIntPtr((nint)userData).Target!;
            Interlocked.Increment(ref owner._validationErrorCount);
        }

        return Vk.False;
    }

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new InvalidOperationException($"{operation} failed with {result}.");
        }
    }

    public HeadlessVulkan(bool enableValidation)
    {
        Vk = Vk.GetApi();
        _selfHandle = GCHandle.Alloc(this);

        try
        {
            ValidationEnabled = enableValidation && IsValidationLayerAvailable();
            if (enableValidation && !ValidationEnabled)
            {
                Console.Error.WriteLine("[Vulkan Validation] VK_LAYER_KHRONOS_validation is not installed; running without validation.");
            }

            DebugUtilsMessengerCreateInfoEXT messengerInfo = CreateMessengerInfo();
            Instance = CreateInstance(messengerInfo);
            if (ValidationEnabled)
            {
                (_debugUtils, _debugMessenger) = CreateMessenger(messengerInfo);
            }

            (PhysicalDevice, QueueFamily, DeviceName) = PickPhysicalDevice();
            Device = CreateDevice();
            Vk.GetDeviceQueue(Device, QueueFamily, 0, out Queue queue);
            Queue = queue;
            CommandPool = CreateCommandPool();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private readonly ExtDebugUtils? _debugUtils;
    private readonly DebugUtilsMessengerEXT _debugMessenger;
    private GCHandle _selfHandle;
    private int _validationErrorCount;
    private bool _disposed;

    public Vk Vk { get; }
    public Instance Instance { get; }
    public PhysicalDevice PhysicalDevice { get; }
    public Device Device { get; }
    public Queue Queue { get; }
    public uint QueueFamily { get; }
    public CommandPool CommandPool { get; }
    public string DeviceName { get; } = string.Empty;

    /// <summary>
    /// False when validation was requested but the layer is not installed; ValidationErrorCount then stays 0
    /// without meaning anything.
    /// </summary>
    public bool ValidationEnabled { get; }

    /// <summary>
    /// Number of error-severity messages reported by the debug messenger so far, including during Dispose.
    /// </summary>
    public int ValidationErrorCount => _validationErrorCount;

    private unsafe bool IsValidationLayerAvailable()
    {
        uint count = 0;
        Check(Vk.EnumerateInstanceLayerProperties(&count, null), "vkEnumerateInstanceLayerProperties");

        LayerProperties[] layers = new LayerProperties[count];
        fixed (LayerProperties* layersPtr = layers)
        {
            Check(Vk.EnumerateInstanceLayerProperties(&count, layersPtr), "vkEnumerateInstanceLayerProperties");
        }

        foreach (LayerProperties layer in layers)
        {
            if (MemoryMarshal.CreateReadOnlySpanFromNullTerminated(layer.LayerName).SequenceEqual(ValidationLayerName))
            {
                return true;
            }
        }

        return false;
    }

    private unsafe DebugUtilsMessengerCreateInfoEXT CreateMessengerInfo()
    {
        return new()
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                            | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                        | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                        | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(&OnDebugMessage),
            PUserData = (void*)GCHandle.ToIntPtr(_selfHandle)
        };
    }

    // The messenger info is also chained into vkCreateInstance so creation/destruction of the instance itself
    // is validated; the persistent messenger only exists between those two calls.
    private unsafe Instance CreateInstance(DebugUtilsMessengerCreateInfoEXT messengerInfo)
    {
        fixed (byte* applicationName = "MYT PhysicsTests"u8)
        fixed (byte* engineName = "MYT"u8)
        fixed (byte* layerName = ValidationLayerName)
        fixed (byte* debugUtilsName = DebugUtilsExtensionName)
        {
            byte** layerNames = stackalloc byte*[] { layerName };
            byte** extensionNames = stackalloc byte*[] { debugUtilsName };

            ApplicationInfo applicationInfo = new()
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = applicationName,
                ApplicationVersion = Vk.MakeVersion(1, 0, 0),
                PEngineName = engineName,
                EngineVersion = Vk.MakeVersion(1, 0, 0),
                ApiVersion = Vk.Version13
            };

            InstanceCreateInfo createInfo = new()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo
            };

            if (ValidationEnabled)
            {
                createInfo.PNext = &messengerInfo;
                createInfo.EnabledLayerCount = 1;
                createInfo.PpEnabledLayerNames = layerNames;
                createInfo.EnabledExtensionCount = 1;
                createInfo.PpEnabledExtensionNames = extensionNames;
            }

            Check(Vk.CreateInstance(&createInfo, null, out Instance instance), "vkCreateInstance");
            return instance;
        }
    }

    private unsafe (ExtDebugUtils DebugUtils, DebugUtilsMessengerEXT Messenger) CreateMessenger
    (
        DebugUtilsMessengerCreateInfoEXT messengerInfo
    )
    {
        if (!Vk.TryGetInstanceExtension(Instance, out ExtDebugUtils debugUtils))
        {
            throw new InvalidOperationException("VK_EXT_debug_utils was enabled but its functions could not be loaded.");
        }

        Result result = debugUtils.CreateDebugUtilsMessenger(Instance, &messengerInfo, null, out DebugUtilsMessengerEXT messenger);
        Check(result, "vkCreateDebugUtilsMessengerEXT");
        return (debugUtils, messenger);
    }

    private unsafe (PhysicalDevice Device, uint QueueFamily, string Name) PickPhysicalDevice()
    {
        uint count = 0;
        Check(Vk.EnumeratePhysicalDevices(Instance, &count, null), "vkEnumeratePhysicalDevices");

        PhysicalDevice[] candidates = new PhysicalDevice[count];
        fixed (PhysicalDevice* candidatesPtr = candidates)
        {
            Check(Vk.EnumeratePhysicalDevices(Instance, &count, candidatesPtr), "vkEnumeratePhysicalDevices");
        }

        (PhysicalDevice Device, uint QueueFamily, string Name)? best = null;
        int bestScore = -1;

        foreach (PhysicalDevice candidate in candidates)
        {
            Vk.GetPhysicalDeviceFeatures(candidate, out PhysicalDeviceFeatures features);
            uint? queueFamily = FindQueueFamily(candidate);
            if (!features.ShaderFloat64 || queueFamily is null)
            {
                continue;
            }

            Vk.GetPhysicalDeviceProperties(candidate, out PhysicalDeviceProperties properties);
            int score = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 3,
                PhysicalDeviceType.IntegratedGpu => 2,
                PhysicalDeviceType.VirtualGpu => 1,
                _ => 0
            };

            if (score > bestScore)
            {
                string name = Marshal.PtrToStringUTF8((nint)properties.DeviceName) ?? string.Empty;
                best = (candidate, queueFamily.Value, name);
                bestScore = score;
            }
        }

        if (best is null)
        {
            throw new InvalidOperationException
            (
                $"None of the {count} Vulkan device(s) supports shaderFloat64 with a compute-capable queue family."
            );
        }

        return best.Value;
    }

    private unsafe uint? FindQueueFamily(PhysicalDevice physicalDevice)
    {
        uint count = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &count, null);

        QueueFamilyProperties[] families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* familiesPtr = families)
        {
            Vk.GetPhysicalDeviceQueueFamilyProperties(physicalDevice, &count, familiesPtr);
        }

        uint? computeOnly = null;
        for (uint i = 0; i < count; i++)
        {
            QueueFlags flags = families[i].QueueFlags;
            if (!flags.HasFlag(QueueFlags.ComputeBit))
            {
                continue;
            }

            if (flags.HasFlag(QueueFlags.GraphicsBit))
            {
                return i;
            }

            computeOnly ??= i;
        }

        return computeOnly;
    }

    private unsafe Device CreateDevice()
    {
        Vk.GetPhysicalDeviceFeatures(PhysicalDevice, out PhysicalDeviceFeatures supported);

        float priority = 1.0f;
        DeviceQueueCreateInfo queueInfo = new()
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority
        };

        PhysicalDeviceFeatures enabled = new()
        {
            ShaderFloat64 = true,
            ShaderInt64 = supported.ShaderInt64
        };

        DeviceCreateInfo createInfo = new()
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueInfo,
            PEnabledFeatures = &enabled
        };

        Check(Vk.CreateDevice(PhysicalDevice, &createInfo, null, out Device device), "vkCreateDevice");
        return device;
    }

    private unsafe CommandPool CreateCommandPool()
    {
        CommandPoolCreateInfo createInfo = new()
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = QueueFamily
        };

        Check(Vk.CreateCommandPool(Device, &createInfo, null, out CommandPool pool), "vkCreateCommandPool");
        return pool;
    }

    // Also runs on a partially constructed object when the constructor throws, hence the handle checks.
    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (Device.Handle != 0)
        {
            Vk.DeviceWaitIdle(Device);
            if (CommandPool.Handle != 0)
            {
                Vk.DestroyCommandPool(Device, CommandPool, null);
            }

            Vk.DestroyDevice(Device, null);
        }

        if (_debugUtils is not null)
        {
            _debugUtils.DestroyDebugUtilsMessenger(Instance, _debugMessenger, null);
            _debugUtils.Dispose();
        }

        if (Instance.Handle != 0)
        {
            Vk.DestroyInstance(Instance, null);
        }

        _selfHandle.Free();
        Vk.Dispose();
    }
}
