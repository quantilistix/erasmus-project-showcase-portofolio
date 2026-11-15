


using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System.Runtime.InteropServices;

namespace IntelliCraft;


public static partial class Program
{

    private static PhysicalDevice physicalDevice;
    private static Device device;
    private static Queue graphicsQueue;
    private static Queue presentQueue;
    private static Queue computeQueue;
    private static uint graphicsQueueFamily;
    private static uint presentQueueFamily;
    private static uint computeQueueFamily;
    private static KhrSwapchain khrSwapchain = null!;

    private static unsafe void CreateDevice()
    {
        PickPhysicalDevice();
        CreateLogicalDevice();
    }

    private static unsafe void PickPhysicalDevice()
    {
        uint deviceCount = 0;
        vk.EnumeratePhysicalDevices(instance, &deviceCount, null);

        if (deviceCount == 0)
            throw new Exception("Failed to find GPUs with Vulkan support!");

        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* devicesPtr = devices)
        {
            vk.EnumeratePhysicalDevices(instance, &deviceCount, devicesPtr);
        }

        // Pick the best suitable device (prefer discrete GPU)
        PhysicalDevice? chosen = null;
        int bestScore = -1;

        foreach (var dev in devices)
        {
            if (!FindQueueFamilies(dev, out uint gFamily, out uint pFamily, out uint cFamily))
                continue;

            if (!CheckDeviceExtensionSupport(dev))
                continue;

            vk.GetPhysicalDeviceProperties(dev, out var properties);
            vk.GetPhysicalDeviceFeatures(dev, out var features);

            int score = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 1000,
                PhysicalDeviceType.IntegratedGpu => 100,
                PhysicalDeviceType.VirtualGpu => 50,
                _ => 10
            };

            if (score > bestScore)
            {
                bestScore = score;
                chosen = dev;
                graphicsQueueFamily = gFamily;
                presentQueueFamily = pFamily;
                computeQueueFamily = cFamily;
            }
        }

        if (chosen is null)
            throw new Exception("Failed to find a suitable GPU!");

        physicalDevice = chosen.Value;

        vk.GetPhysicalDeviceProperties(physicalDevice, out var props);
        var name = Marshal.PtrToStringAnsi((IntPtr)props.DeviceName);
        Console.WriteLine($"Physical device selected: {name}");
    }

    private static unsafe bool FindQueueFamilies(PhysicalDevice dev, out uint graphicsFamily, out uint presentFamily, out uint computeFamily)
    {
        graphicsFamily = 0;
        presentFamily = 0;
        computeFamily = 0;
        bool foundGraphics = false;
        bool foundPresent = false;
        bool foundCompute = false;

        uint queueFamilyCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(dev, &queueFamilyCount, null);

        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* ptr = queueFamilies)
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(dev, &queueFamilyCount, ptr);
        }

        for (uint i = 0; i < queueFamilyCount; i++)
        {
            if (queueFamilies[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                graphicsFamily = i;
                foundGraphics = true;
            }

            if (queueFamilies[i].QueueFlags.HasFlag(QueueFlags.ComputeBit))
            {
                computeFamily = i;
                foundCompute = true;
            }

            khrSurface.GetPhysicalDeviceSurfaceSupport(dev, i, surface, out var presentSupported);
            if (presentSupported)
            {
                presentFamily = i;
                foundPresent = true;
            }

            if (foundGraphics && foundPresent && foundCompute)
                break;
        }

        return foundGraphics && foundPresent && foundCompute;
    }

    private static unsafe bool CheckDeviceExtensionSupport(PhysicalDevice dev)
    {
        uint extensionCount = 0;
        vk.EnumerateDeviceExtensionProperties(dev, (byte*)null, &extensionCount, null);

        var availableExtensions = new ExtensionProperties[extensionCount];
        fixed (ExtensionProperties* ptr = availableExtensions)
        {
            vk.EnumerateDeviceExtensionProperties(dev, (byte*)null, &extensionCount, ptr);
        }

        foreach (var ext in availableExtensions)
        {
            var name = Marshal.PtrToStringAnsi((IntPtr)ext.ExtensionName);
            if (name == KhrSwapchain.ExtensionName)
                return true;
        }

        return false;
    }

    private static unsafe void CreateLogicalDevice()
    {
        // Determine unique queue families
        var uniqueSet = new HashSet<uint> { graphicsQueueFamily, presentQueueFamily, computeQueueFamily };
        var uniqueFamilies = uniqueSet.ToArray();

        var queueCreateInfos = stackalloc DeviceQueueCreateInfo[uniqueFamilies.Length];
        float queuePriority = 1.0f;

        for (int i = 0; i < uniqueFamilies.Length; i++)
        {
            queueCreateInfos[i] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = uniqueFamilies[i],
                QueueCount = 1,
                PQueuePriorities = &queuePriority
            };
        }

        var deviceFeatures = new PhysicalDeviceFeatures
        {
            ShaderFloat64 = true
        };

        var dynamicRenderingFeatures = new PhysicalDeviceDynamicRenderingFeatures
        {
            SType = StructureType.PhysicalDeviceDynamicRenderingFeatures,
            DynamicRendering = true
        };

        var swapchainExtName = Marshal.StringToHGlobalAnsi(KhrSwapchain.ExtensionName);
        var extensionPtr = (byte*)swapchainExtName;

        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            PNext = &dynamicRenderingFeatures,
            QueueCreateInfoCount = (uint)uniqueFamilies.Length,
            PQueueCreateInfos = queueCreateInfos,
            PEnabledFeatures = &deviceFeatures,
            EnabledExtensionCount = 1,
            PpEnabledExtensionNames = &extensionPtr,
            EnabledLayerCount = 0
        };

        // Set validation layers on device (for backwards compatibility)
        nint layerNamePtr = 0;
        if (EnableValidationLayers)
        {
            layerNamePtr = Marshal.StringToHGlobalAnsi(ValidationLayers[0]);
            var layerPtr = (byte*)layerNamePtr;
            createInfo.EnabledLayerCount = 1;
            createInfo.PpEnabledLayerNames = &layerPtr;
        }

        if (vk.CreateDevice(physicalDevice, &createInfo, null, out device) != Result.Success)
            throw new Exception("Failed to create logical device!");

        vk.GetDeviceQueue(device, graphicsQueueFamily, 0, out graphicsQueue);
        vk.GetDeviceQueue(device, presentQueueFamily, 0, out presentQueue);
        vk.GetDeviceQueue(device, computeQueueFamily, 0, out computeQueue);

        if (!vk.TryGetDeviceExtension(instance, device, out khrSwapchain))
            throw new Exception("Failed to load VK_KHR_swapchain extension!");

        Marshal.FreeHGlobal(swapchainExtName);
        if (layerNamePtr != 0) Marshal.FreeHGlobal(layerNamePtr);

        Console.WriteLine("Logical device created successfully!");
    }

    private static unsafe void DestroyDevice()
    {
        vk.DestroyDevice(device, null);
    }

}
