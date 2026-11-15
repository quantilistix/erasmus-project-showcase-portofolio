

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using System.Runtime.InteropServices;

namespace IntelliCraft;


public static partial class Program
{

    private static Vk vk = null!;
    private static Instance instance;

    private const bool EnableValidationLayers = true;
    private static readonly string[] ValidationLayers = ["VK_LAYER_KHRONOS_validation"];

    private static ExtDebugUtils debugUtils = null!;
    private static DebugUtilsMessengerEXT debugMessenger;

    private static unsafe bool CheckValidationLayerSupport()
    {
        uint layerCount = 0;
        vk.EnumerateInstanceLayerProperties(&layerCount, null);

        var availableLayers = new LayerProperties[layerCount];
        fixed (LayerProperties* availableLayersPtr = availableLayers)
        {
            vk.EnumerateInstanceLayerProperties(&layerCount, availableLayersPtr);
        }

        foreach (var layerName in ValidationLayers)
        {
            bool found = false;
            foreach (var layerProperties in availableLayers)
            {
                var name = Marshal.PtrToStringAnsi((IntPtr)layerProperties.LayerName);
                if (name == layerName)
                {
                    found = true;
                    break;
                }
            }
            if (!found) return false;
        }

        return true;
    }

    private static unsafe uint DebugCallback
    (
        DebugUtilsMessageSeverityFlagsEXT messageSeverity,
        DebugUtilsMessageTypeFlagsEXT messageTypes,
        DebugUtilsMessengerCallbackDataEXT* pCallbackData,
        void* pUserData
    )
    {
        var message = Marshal.PtrToStringAnsi((IntPtr)pCallbackData->PMessage);
        Console.Error.WriteLine($"[Vulkan Validation] {message}");
        return Vk.False;
    }

    private static unsafe DebugUtilsMessengerCreateInfoEXT PopulateDebugMessengerCreateInfo()
    {
        return new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt
                            | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                            | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                        | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                        | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = (PfnDebugUtilsMessengerCallbackEXT)DebugCallback
        };
    }

    private static unsafe void CreateInstance()
    {
        vk = Vk.GetApi();

        if (EnableValidationLayers && !CheckValidationLayerSupport())
        {
            throw new Exception("Validation layers requested, but not available!");
        }

        uint glfwExtensionCount = 0;
        byte** glfwExtensions = Glfw.GetRequiredInstanceExtensions(out glfwExtensionCount);


        uint extensionCount = glfwExtensionCount + (EnableValidationLayers ? 1u : 0u);
        var extensionPtrs = stackalloc byte*[(int)extensionCount];
        for (int i = 0; i < glfwExtensionCount; i++)
        {
            extensionPtrs[i] = glfwExtensions[i];
        }

        nint debugUtilsExtNamePtr = 0;
        if (EnableValidationLayers)
        {
            debugUtilsExtNamePtr = Marshal.StringToHGlobalAnsi(ExtDebugUtils.ExtensionName);
            extensionPtrs[glfwExtensionCount] = (byte*)debugUtilsExtNamePtr;
        }

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)Marshal.StringToHGlobalAnsi("Vulkan Test"),
            ApplicationVersion = Vk.MakeVersion(1, 0, 0),
            PEngineName = (byte*)Marshal.StringToHGlobalAnsi("No Engine"),
            EngineVersion = Vk.MakeVersion(1, 0, 0),
            ApiVersion = Vk.Version13
        };

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = extensionCount,
            PpEnabledExtensionNames = extensionPtrs,
            EnabledLayerCount = 0
        };

        // Set up validation layers and debug messenger for instance creation
        nint layerNamePtr = 0;
        DebugUtilsMessengerCreateInfoEXT debugCreateInfo;
        if (EnableValidationLayers)
        {
            layerNamePtr = Marshal.StringToHGlobalAnsi(ValidationLayers[0]);
            var layerPtr = (byte*)layerNamePtr;

            createInfo.EnabledLayerCount = 1;
            createInfo.PpEnabledLayerNames = &layerPtr;

            debugCreateInfo = PopulateDebugMessengerCreateInfo();
            createInfo.PNext = &debugCreateInfo;
        }

        if (vk.CreateInstance(&createInfo, null, out instance) != Result.Success)
        {
            throw new Exception("Failed to create Vulkan instance!");
        }

        // Set up the persistent debug messenger
        if (EnableValidationLayers)
        {
            if (!vk.TryGetInstanceExtension(instance, out debugUtils))
            {
                throw new Exception("Failed to load debug utils extension!");
            }

            var messengerCreateInfo = PopulateDebugMessengerCreateInfo();
            if (debugUtils.CreateDebugUtilsMessenger(instance, &messengerCreateInfo, null, out debugMessenger) != Result.Success)
            {
                throw new Exception("Failed to set up debug messenger!");
            }
        }

        if (layerNamePtr != 0) Marshal.FreeHGlobal(layerNamePtr);
        if (debugUtilsExtNamePtr != 0) Marshal.FreeHGlobal(debugUtilsExtNamePtr);
        Marshal.FreeHGlobal((nint)appInfo.PApplicationName);
        Marshal.FreeHGlobal((nint)appInfo.PEngineName);

        Console.WriteLine("Vulkan instance created successfully!");
    }



    private static unsafe void DestroyInstance()
    {
        if (EnableValidationLayers)
        {
            debugUtils.DestroyDebugUtilsMessenger(instance, debugMessenger, null);
        }

        vk.DestroyInstance(instance, null);
    }


}
