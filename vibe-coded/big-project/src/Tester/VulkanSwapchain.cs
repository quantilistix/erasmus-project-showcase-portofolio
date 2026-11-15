

using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace IntelliCraft;


public static partial class Program
{

    public const Format DepthFormat = Format.D32Sfloat;

    private static SwapchainKHR swapchain;
    private static Image[] swapchainImages = [];
    private static ImageView[] swapchainImageViews = [];
    private static Format swapchainFormat;
    private static Extent2D swapchainExtent;

    private static Image depthImage;
    private static DeviceMemory depthImageMemory;
    private static ImageView depthImageView;

    private static unsafe void CreateSwapchain()
    {
        khrSurface.GetPhysicalDeviceSurfaceCapabilities(physicalDevice, surface, out var capabilities);

        var surfaceFormat = ChooseSurfaceFormat();
        swapchainFormat = surfaceFormat.Format;
        swapchainExtent = ChooseExtent(capabilities);

        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
            imageCount = capabilities.MaxImageCount;

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = swapchainExtent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true
        };

        var familyIndices = stackalloc uint[2] { graphicsQueueFamily, presentQueueFamily };
        if (graphicsQueueFamily != presentQueueFamily)
        {
            createInfo.ImageSharingMode = SharingMode.Concurrent;
            createInfo.QueueFamilyIndexCount = 2;
            createInfo.PQueueFamilyIndices = familyIndices;
        }
        else
        {
            createInfo.ImageSharingMode = SharingMode.Exclusive;
        }

        if (khrSwapchain.CreateSwapchain(device, &createInfo, null, out swapchain) != Result.Success)
            throw new Exception("Failed to create swapchain!");

        uint actualImageCount = 0;
        khrSwapchain.GetSwapchainImages(device, swapchain, &actualImageCount, null);
        swapchainImages = new Image[actualImageCount];
        fixed (Image* imagesPtr = swapchainImages)
            khrSwapchain.GetSwapchainImages(device, swapchain, &actualImageCount, imagesPtr);

        swapchainImageViews = new ImageView[actualImageCount];
        for (int i = 0; i < actualImageCount; i++)
            swapchainImageViews[i] = CreateImageView(swapchainImages[i], swapchainFormat, ImageAspectFlags.ColorBit);

        CreateDepthResources();

        Console.WriteLine($"Swapchain created: {actualImageCount} images, {swapchainExtent.Width}x{swapchainExtent.Height}");
    }

    private static unsafe void DestroySwapchain()
    {
        vk.DestroyImageView(device, depthImageView, null);
        vk.DestroyImage(device, depthImage, null);
        vk.FreeMemory(device, depthImageMemory, null);

        foreach (var view in swapchainImageViews)
            vk.DestroyImageView(device, view, null);

        khrSwapchain.DestroySwapchain(device, swapchain, null);
    }

    private static unsafe SurfaceFormatKHR ChooseSurfaceFormat()
    {
        uint formatCount = 0;
        khrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, &formatCount, null);

        var formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* formatsPtr = formats)
            khrSurface.GetPhysicalDeviceSurfaceFormats(physicalDevice, surface, &formatCount, formatsPtr);

        foreach (var format in formats)
        {
            if (format.Format == Format.B8G8R8A8Srgb && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                return format;
        }

        throw new Exception("Required surface format B8G8R8A8Srgb/SrgbNonlinear is not supported.");
    }

    private static unsafe Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
            return capabilities.CurrentExtent;

        Glfw.GetFramebufferSize(Window, out int width, out int height);

        return new Extent2D
        {
            Width = Math.Clamp((uint)width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Height = Math.Clamp((uint)height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height)
        };
    }

    private static unsafe void CreateDepthResources()
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D(swapchainExtent.Width, swapchainExtent.Height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Format = DepthFormat,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            Samples = SampleCountFlags.Count1Bit
        };

        if (vk.CreateImage(device, &imageInfo, null, out depthImage) != Result.Success)
            throw new Exception("Failed to create depth image!");

        vk.GetImageMemoryRequirements(device, depthImage, out var memRequirements);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };

        if (vk.AllocateMemory(device, &allocInfo, null, out depthImageMemory) != Result.Success)
            throw new Exception("Failed to allocate depth image memory!");

        vk.BindImageMemory(device, depthImage, depthImageMemory, 0);

        depthImageView = CreateImageView(depthImage, DepthFormat, ImageAspectFlags.DepthBit);

        TransitionDepthImageLayout();
    }

    private static unsafe void TransitionDepthImageLayout()
    {
        CommandBuffer cmd = BeginOneShotCommands();

        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.DepthAttachmentOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = depthImage,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.DepthStencilAttachmentReadBit | AccessFlags.DepthStencilAttachmentWriteBit
        };

        vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.EarlyFragmentTestsBit,
            0, 0, null, 0, null, 1, &barrier);

        EndOneShotCommands(cmd);
    }

    private static unsafe ImageView CreateImageView(Image image, Format format, ImageAspectFlags aspect)
    {
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1)
        };

        if (vk.CreateImageView(device, &viewInfo, null, out ImageView view) != Result.Success)
            throw new Exception("Failed to create image view!");

        return view;
    }

    private static unsafe uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out var memProperties);

        for (uint i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 && (memProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        }

        throw new Exception("Failed to find suitable memory type.");
    }

}
