using GlmSharp;
using Silk.NET.Vulkan;
using Image = Silk.NET.Vulkan.Image;

namespace VoxelGame;

internal sealed unsafe partial class Renderer
{
    private const uint SunShadowSize = 2048;
    private readonly List<VisibleDraw> _sunDraws = [];
    private readonly Image[] _sunImages = new Image[GraphicsContext.FramesInFlight];
    private readonly DeviceMemory[] _sunMemory = new DeviceMemory[GraphicsContext.FramesInFlight];
    private readonly ImageView[] _sunViews = new ImageView[GraphicsContext.FramesInFlight];
    private readonly bool[] _sunInitialized = new bool[GraphicsContext.FramesInFlight];
    private Sampler _sunSampler;
    private Pipeline _sunPipeline;

    private mat4 SunShadowProjection(dvec3 eye)
    {
        float radius = (float)Math.Min(ViewDistance,128)+16, texel = 2*radius/SunShadowSize;
        float sx = SunDirection.x/Math.Max(SunDirection.z,0.025f), sy = SunDirection.y/Math.Max(SunDirection.z,0.025f);
        float x = (float)Math.Round((eye.x-sx*eye.z)/texel)*texel;
        float y = (float)Math.Round((eye.y-sy*eye.z)/texel)*texel;
        const float depth = 2048;
        // Parallel projection along the sun. Greater depth is closer to the light.
        return new mat4(1/radius,0,0,0, 0,1/radius,0,0,
            -sx/radius,-sy/radius,1/depth,0, -x/radius,-y/radius,.5f-(float)eye.z/depth,1);
    }
    private void CreateSunShadows()
    {
        for (int f = 0; f < GraphicsContext.FramesInFlight; f++)
        {
            ImageCreateInfo info = new()
            {
                SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D,
                Extent = new(SunShadowSize,SunShadowSize,1), MipLevels = 1, ArrayLayers = 1,
                Format = GraphicsContext.DepthFormat, Tiling = ImageTiling.Optimal,
                InitialLayout = ImageLayout.Undefined, Samples = SampleCountFlags.Count1Bit,
                Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive
            };
            if (_vk.CreateImage(_device,&info,null,out _sunImages[f]) != Result.Success) throw new InvalidOperationException("Cannot create sun shadow map");
            _vk.GetImageMemoryRequirements(_device,_sunImages[f],out MemoryRequirements requirements);
            MemoryAllocateInfo allocation = new()
            {
                SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size,
                MemoryTypeIndex = _graphics.FindMemoryType(requirements.MemoryTypeBits,MemoryPropertyFlags.DeviceLocalBit)
            };
            if (_vk.AllocateMemory(_device,&allocation,null,out _sunMemory[f]) != Result.Success) throw new InvalidOperationException("Cannot allocate sun shadow map");
            _vk.BindImageMemory(_device,_sunImages[f],_sunMemory[f],0);
            _sunViews[f] = _graphics.CreateView(_sunImages[f],GraphicsContext.DepthFormat,ImageAspectFlags.DepthBit);
        }
        SamplerCreateInfo sampler = new()
        {
            SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Nearest, MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest, AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge, AddressModeW = SamplerAddressMode.ClampToEdge, MaxLod = 0
        };
        if (_vk.CreateSampler(_device,&sampler,null,out _sunSampler) != Result.Success) throw new InvalidOperationException("Cannot create sun shadow sampler");
    }
    private void BindSunShadow(uint frame)
    {
        DescriptorImageInfo image = new() { Sampler = _sunSampler, ImageView = _sunViews[frame], ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal };
        WriteDescriptorSet write = new()
        {
            SType = StructureType.WriteDescriptorSet, DstSet = _sets[frame], DstBinding = 5,
            DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &image
        };
        _vk.UpdateDescriptorSets(_device,1,&write,0,null);
    }
    private void RecordSunShadow(Frame frame)
    {
        uint f = frame.FrameIndex; CommandBuffer cmd = frame.Commands;
        if (_sunInitialized[f] && (!Video.Sun || !Video.DynamicShadows || Daylight.SunIntensity <= 0)) return;
        _graphics.ImageBarrier(cmd,_sunImages[f],ImageAspectFlags.DepthBit,
            _sunInitialized[f] ? ImageLayout.DepthStencilReadOnlyOptimal : ImageLayout.Undefined,ImageLayout.DepthAttachmentOptimal,
            _sunInitialized[f] ? PipelineStageFlags.FragmentShaderBit : PipelineStageFlags.TopOfPipeBit,
            _sunInitialized[f] ? AccessFlags.ShaderReadBit : 0,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,AccessFlags.DepthStencilAttachmentWriteBit);
        RenderingAttachmentInfo depth = new()
        {
            SType = StructureType.RenderingAttachmentInfo, ImageView = _sunViews[f], ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(depthStencil:new ClearDepthStencilValue(0,0))
        };
        RenderingInfo rendering = new()
        {
            SType = StructureType.RenderingInfo, LayerCount = 1,
            RenderArea = new(new Offset2D(0,0),new Extent2D(SunShadowSize,SunShadowSize)), PDepthAttachment = &depth
        };
        _vk.CmdBeginRendering(cmd,&rendering);
        Viewport viewport = new() { Width = SunShadowSize, Height = SunShadowSize, MinDepth = 0, MaxDepth = 1 };
        Rect2D scissor = new(new Offset2D(0,0),new Extent2D(SunShadowSize,SunShadowSize));
        _vk.CmdSetViewport(cmd,0,1,&viewport); _vk.CmdSetScissor(cmd,0,1,&scissor);
        if (_sunDraws.Count != 0) RecordVoxelDraws(cmd,f,_sunDraws,0,_sunPipeline);
        _vk.CmdEndRendering(cmd);
        _graphics.ImageBarrier(cmd,_sunImages[f],ImageAspectFlags.DepthBit,ImageLayout.DepthAttachmentOptimal,ImageLayout.DepthStencilReadOnlyOptimal,
            PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit,AccessFlags.DepthStencilAttachmentWriteBit,
            PipelineStageFlags.FragmentShaderBit,AccessFlags.ShaderReadBit);
        _sunInitialized[f] = true;
    }
    private void DestroySunShadows()
    {
        _vk.DestroyPipeline(_device,_sunPipeline,null);
        _vk.DestroySampler(_device,_sunSampler,null);
        for (int f = 0; f < GraphicsContext.FramesInFlight; f++)
        {
            _vk.DestroyImageView(_device,_sunViews[f],null); _vk.DestroyImage(_device,_sunImages[f],null); _vk.FreeMemory(_device,_sunMemory[f],null);
        }
    }
}
