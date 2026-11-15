using System.Numerics;
using System.Runtime.InteropServices;
using NeuralGraph;
using Silk.NET.Vulkan;

namespace MYT.Kit.Graph;

/// <summary>Vulkan line-list weights with depth writes, followed by depth-tested neuron triangles and a label overlay. The host waits for the previous frame before reusing the mapped vertex buffer.</summary>
public sealed unsafe class GraphGpuRenderer : IDisposable
{
    private readonly VulkanDevice device;
    private Vk Vk=>device.Api;
    private readonly Extent2D extent;
    public RenderPass RenderPass {get;private set;}
    private Image depth;
    private DeviceMemory depthMemory;
    private ImageView depthView;
    private PipelineLayout layout;
    private Pipeline guides,lines,neurons,overlays;
    private GpuBuffer? vertices;
    private readonly List<Framebuffer> frames=[];
    private bool disposed;
    public GraphGpuRenderer(VulkanDevice device,Format colorFormat,Extent2D extent)
    {
        this.device=device;this.extent=extent;
        try { CreateDepth();CreatePass(colorFormat);CreatePipelines(); }
        catch { Dispose();throw; }
    }
    private void CreateDepth()
    {
        var info=new ImageCreateInfo {SType=StructureType.ImageCreateInfo,ImageType=ImageType.Type2D,Format=Format.D32Sfloat,
            Extent=new(extent.Width,extent.Height,1),MipLevels=1,ArrayLayers=1,Samples=SampleCountFlags.Count1Bit,
            Tiling=ImageTiling.Optimal,Usage=ImageUsageFlags.DepthStencilAttachmentBit,SharingMode=SharingMode.Exclusive};
        VulkanDevice.Check(Vk.CreateImage(device.Device,in info,null,out depth));
        Vk.GetImageMemoryRequirements(device.Device,depth,out var req);
        Vk.GetPhysicalDeviceMemoryProperties(device.PhysicalDevice,out var props);
        uint type=uint.MaxValue;
        for(uint i=0;i<props.MemoryTypeCount;i++)
            if((req.MemoryTypeBits&(1u<<(int)i))!=0&&(props.MemoryTypes[(int)i].PropertyFlags&MemoryPropertyFlags.DeviceLocalBit)!=0){type=i;break;}
        if(type==uint.MaxValue)throw new NotSupportedException("No depth attachment memory.");
        var allocation=new MemoryAllocateInfo {SType=StructureType.MemoryAllocateInfo,AllocationSize=req.Size,MemoryTypeIndex=type};
        VulkanDevice.Check(Vk.AllocateMemory(device.Device,in allocation,null,out depthMemory));
        VulkanDevice.Check(Vk.BindImageMemory(device.Device,depth,depthMemory,0));
        var view=new ImageViewCreateInfo {SType=StructureType.ImageViewCreateInfo,Image=depth,ViewType=ImageViewType.Type2D,Format=Format.D32Sfloat,
            SubresourceRange=new(ImageAspectFlags.DepthBit,0,1,0,1)};
        VulkanDevice.Check(Vk.CreateImageView(device.Device,in view,null,out depthView));
    }
    private void CreatePass(Format format)
    {
        AttachmentDescription* attachments=stackalloc AttachmentDescription[2];
        attachments[0]=new() {Format=format,Samples=SampleCountFlags.Count1Bit,LoadOp=AttachmentLoadOp.Load,StoreOp=AttachmentStoreOp.Store,
            StencilLoadOp=AttachmentLoadOp.DontCare,StencilStoreOp=AttachmentStoreOp.DontCare,
            InitialLayout=ImageLayout.ColorAttachmentOptimal,FinalLayout=ImageLayout.ColorAttachmentOptimal};
        attachments[1]=new() {Format=Format.D32Sfloat,Samples=SampleCountFlags.Count1Bit,LoadOp=AttachmentLoadOp.Clear,StoreOp=AttachmentStoreOp.DontCare,
            StencilLoadOp=AttachmentLoadOp.DontCare,StencilStoreOp=AttachmentStoreOp.DontCare,
            InitialLayout=ImageLayout.Undefined,FinalLayout=ImageLayout.DepthStencilAttachmentOptimal};
        var color=new AttachmentReference(0,ImageLayout.ColorAttachmentOptimal);
        var z=new AttachmentReference(1,ImageLayout.DepthStencilAttachmentOptimal);
        var subpass=new SubpassDescription {PipelineBindPoint=PipelineBindPoint.Graphics,ColorAttachmentCount=1,PColorAttachments=&color,PDepthStencilAttachment=&z};
        var dependency=new SubpassDependency {SrcSubpass=Vk.SubpassExternal,DstSubpass=0,
            SrcStageMask=PipelineStageFlags.ColorAttachmentOutputBit|PipelineStageFlags.LateFragmentTestsBit,
            DstStageMask=PipelineStageFlags.ColorAttachmentOutputBit|PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask=AccessFlags.ColorAttachmentWriteBit|AccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask=AccessFlags.ColorAttachmentReadBit|AccessFlags.ColorAttachmentWriteBit|AccessFlags.DepthStencilAttachmentWriteBit};
        var info=new RenderPassCreateInfo {SType=StructureType.RenderPassCreateInfo,AttachmentCount=2,PAttachments=attachments,
            SubpassCount=1,PSubpasses=&subpass,DependencyCount=1,PDependencies=&dependency};
        VulkanDevice.Check(Vk.CreateRenderPass(device.Device,in info,null,out var pass));RenderPass=pass;
    }
    public Framebuffer AddTarget(ImageView color)
    {
        ImageView* attachments=stackalloc ImageView[2] {color,depthView};
        var info=new FramebufferCreateInfo {SType=StructureType.FramebufferCreateInfo,RenderPass=RenderPass,AttachmentCount=2,PAttachments=attachments,
            Width=extent.Width,Height=extent.Height,Layers=1};
        VulkanDevice.Check(Vk.CreateFramebuffer(device.Device,in info,null,out var frame));frames.Add(frame);return frame;
    }
    private ShaderModule Shader(string name)
    {
        using var stream=typeof(GraphGpuRenderer).Assembly.GetManifestResourceStream("MYT.Kit.Graph.Shaders."+name+".spv")
            ?? throw new InvalidOperationException("Missing graph shader: "+name);
        byte[] code=new byte[stream.Length];stream.ReadExactly(code);
        fixed(byte* p=code)
        {
            var info=new ShaderModuleCreateInfo {SType=StructureType.ShaderModuleCreateInfo,CodeSize=(nuint)code.Length,PCode=(uint*)p};
            VulkanDevice.Check(Vk.CreateShaderModule(device.Device,in info,null,out var shader));return shader;
        }
    }
    private void CreatePipelines()
    {
        var push=new PushConstantRange(ShaderStageFlags.VertexBit,0,8);
        var info=new PipelineLayoutCreateInfo {SType=StructureType.PipelineLayoutCreateInfo,PushConstantRangeCount=1,PPushConstantRanges=&push};
        VulkanDevice.Check(Vk.CreatePipelineLayout(device.Device,in info,null,out layout));
        var vert=Shader("graph.vert");
        try
        {
            var frag=Shader("graph.frag");
            try {guides=Pipeline(vert,frag,PrimitiveTopology.LineList,false);lines=Pipeline(vert,frag,PrimitiveTopology.LineList,true);neurons=Pipeline(vert,frag,PrimitiveTopology.TriangleList,true);overlays=Pipeline(vert,frag,PrimitiveTopology.TriangleList,false);}
            finally {Vk.DestroyShaderModule(device.Device,frag,null);}
        }
        finally {Vk.DestroyShaderModule(device.Device,vert,null);}
    }
    private Pipeline Pipeline(ShaderModule vert,ShaderModule frag,PrimitiveTopology topology,bool depthTest)
    {
        byte* entry=stackalloc byte[5] {(byte)'m',(byte)'a',(byte)'i',(byte)'n',0};
        PipelineShaderStageCreateInfo* stages=stackalloc PipelineShaderStageCreateInfo[2];
        stages[0]=new() {SType=StructureType.PipelineShaderStageCreateInfo,Stage=ShaderStageFlags.VertexBit,Module=vert,PName=entry};
        stages[1]=new() {SType=StructureType.PipelineShaderStageCreateInfo,Stage=ShaderStageFlags.FragmentBit,Module=frag,PName=entry};
        var binding=new VertexInputBindingDescription(0,(uint)sizeof(GraphVertex),VertexInputRate.Vertex);
        VertexInputAttributeDescription* attributes=stackalloc VertexInputAttributeDescription[3];
        attributes[0]=new(0,0,Format.R32G32B32Sfloat,0);attributes[1]=new(1,0,Format.R8G8B8A8Unorm,12);attributes[2]=new(2,0,Format.R32G32Sfloat,16);
        var input=new PipelineVertexInputStateCreateInfo {SType=StructureType.PipelineVertexInputStateCreateInfo,VertexBindingDescriptionCount=1,PVertexBindingDescriptions=&binding,
            VertexAttributeDescriptionCount=3,PVertexAttributeDescriptions=attributes};
        var assembly=new PipelineInputAssemblyStateCreateInfo {SType=StructureType.PipelineInputAssemblyStateCreateInfo,Topology=topology};
        var viewport=new PipelineViewportStateCreateInfo {SType=StructureType.PipelineViewportStateCreateInfo,ViewportCount=1,ScissorCount=1};
        var raster=new PipelineRasterizationStateCreateInfo {SType=StructureType.PipelineRasterizationStateCreateInfo,PolygonMode=PolygonMode.Fill,
            CullMode=CullModeFlags.None,FrontFace=FrontFace.CounterClockwise,LineWidth=1};
        var multisample=new PipelineMultisampleStateCreateInfo {SType=StructureType.PipelineMultisampleStateCreateInfo,RasterizationSamples=SampleCountFlags.Count1Bit};
        var depthState=new PipelineDepthStencilStateCreateInfo {SType=StructureType.PipelineDepthStencilStateCreateInfo,DepthTestEnable=depthTest,DepthWriteEnable=depthTest,DepthCompareOp=CompareOp.LessOrEqual};
        var blendAttachment=new PipelineColorBlendAttachmentState {ColorWriteMask=ColorComponentFlags.RBit|ColorComponentFlags.GBit|ColorComponentFlags.BBit|ColorComponentFlags.ABit,
            BlendEnable=true,SrcColorBlendFactor=BlendFactor.SrcAlpha,DstColorBlendFactor=BlendFactor.OneMinusSrcAlpha,ColorBlendOp=BlendOp.Add,
            SrcAlphaBlendFactor=BlendFactor.One,DstAlphaBlendFactor=BlendFactor.OneMinusSrcAlpha,AlphaBlendOp=BlendOp.Add};
        var blend=new PipelineColorBlendStateCreateInfo {SType=StructureType.PipelineColorBlendStateCreateInfo,AttachmentCount=1,PAttachments=&blendAttachment};
        DynamicState* states=stackalloc DynamicState[2] {DynamicState.Viewport,DynamicState.Scissor};
        var dynamicInfo=new PipelineDynamicStateCreateInfo {SType=StructureType.PipelineDynamicStateCreateInfo,DynamicStateCount=2,PDynamicStates=states};
        var info=new GraphicsPipelineCreateInfo {SType=StructureType.GraphicsPipelineCreateInfo,StageCount=2,PStages=stages,PVertexInputState=&input,
            PInputAssemblyState=&assembly,PViewportState=&viewport,PRasterizationState=&raster,PMultisampleState=&multisample,PDepthStencilState=&depthState,
            PColorBlendState=&blend,PDynamicState=&dynamicInfo,Layout=layout,RenderPass=RenderPass,Subpass=0};
        VulkanDevice.Check(Vk.CreateGraphicsPipelines(device.Device,default,1,&info,null,out var pipeline));return pipeline;
    }
    public void Draw(CommandBuffer command,Framebuffer frame,GraphDrawList list)
    {
        int guideCount=list.Guides.Length,lineCount=list.Lines.Length,highlightCount=list.Highlights.Length,nodeCount=list.Neurons.Length,overlayCount=list.Overlays.Length;
        if(guideCount+lineCount+highlightCount+nodeCount+overlayCount==0)return;
        ulong bytes=(ulong)((guideCount+lineCount+highlightCount+nodeCount+overlayCount)*sizeof(GraphVertex));
        if(vertices is null||vertices.Size<bytes){vertices?.Dispose();vertices=device.CreateBuffer(Math.Max(bytes,65536),BufferUsageFlags.VertexBufferBit);}
        var floats=vertices.Floats;
        MemoryMarshal.Cast<GraphVertex,float>(list.Guides).CopyTo(floats);
        MemoryMarshal.Cast<GraphVertex,float>(list.Lines).CopyTo(floats[(guideCount*6)..]);
        MemoryMarshal.Cast<GraphVertex,float>(list.Highlights).CopyTo(floats[((guideCount+lineCount)*6)..]);
        MemoryMarshal.Cast<GraphVertex,float>(list.Neurons).CopyTo(floats[((guideCount+lineCount+highlightCount)*6)..]);
        MemoryMarshal.Cast<GraphVertex,float>(list.Overlays).CopyTo(floats[((guideCount+lineCount+highlightCount+nodeCount)*6)..]);
        ClearValue* clears=stackalloc ClearValue[2];clears[0]=default;clears[1]=new() {DepthStencil=new(1,0)};
        var begin=new RenderPassBeginInfo {SType=StructureType.RenderPassBeginInfo,RenderPass=RenderPass,Framebuffer=frame,
            RenderArea=new(default,extent),ClearValueCount=2,PClearValues=clears};
        Vk.CmdBeginRenderPass(command,in begin,SubpassContents.Inline);
        var viewport=new Viewport(0,0,extent.Width,extent.Height,0,1);Vk.CmdSetViewport(command,0,1,in viewport);
        int left=Math.Clamp((int)MathF.Ceiling(list.Clip.Left),0,(int)extent.Width),top=Math.Clamp((int)MathF.Ceiling(list.Clip.Top),0,(int)extent.Height);
        int right=Math.Clamp((int)MathF.Floor(list.Clip.Right),left,(int)extent.Width),bottom=Math.Clamp((int)MathF.Floor(list.Clip.Bottom),top,(int)extent.Height);
        var scissor=new Rect2D(new(left,top),new((uint)(right-left),(uint)(bottom-top)));Vk.CmdSetScissor(command,0,1,in scissor);
        var size=new Vector2(extent.Width,extent.Height);Vk.CmdPushConstants(command,layout,ShaderStageFlags.VertexBit,0,8,&size);
        var buffer=vertices.Buffer;ulong offset=0;Vk.CmdBindVertexBuffers(command,0,1,in buffer,in offset);
        if(guideCount>0){Vk.CmdBindPipeline(command,PipelineBindPoint.Graphics,guides);Vk.CmdDraw(command,(uint)guideCount,1,0,0);}
        if(lineCount>0){Vk.CmdBindPipeline(command,PipelineBindPoint.Graphics,lines);Vk.CmdDraw(command,(uint)lineCount,1,(uint)guideCount,0);}
        if(highlightCount>0){Vk.CmdBindPipeline(command,PipelineBindPoint.Graphics,lines);Vk.CmdDraw(command,(uint)highlightCount,1,(uint)(guideCount+lineCount),0);}
        if(nodeCount>0){Vk.CmdBindPipeline(command,PipelineBindPoint.Graphics,neurons);Vk.CmdDraw(command,(uint)nodeCount,1,(uint)(guideCount+lineCount+highlightCount),0);}
        if(overlayCount>0){Vk.CmdBindPipeline(command,PipelineBindPoint.Graphics,overlays);Vk.CmdDraw(command,(uint)overlayCount,1,(uint)(guideCount+lineCount+highlightCount+nodeCount),0);}
        Vk.CmdEndRenderPass(command);
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        vertices?.Dispose();
        foreach(var frame in frames)Vk.DestroyFramebuffer(device.Device,frame,null);
        if(guides.Handle!=0)Vk.DestroyPipeline(device.Device,guides,null);
        if(lines.Handle!=0)Vk.DestroyPipeline(device.Device,lines,null);
        if(neurons.Handle!=0)Vk.DestroyPipeline(device.Device,neurons,null);
        if(overlays.Handle!=0)Vk.DestroyPipeline(device.Device,overlays,null);
        if(layout.Handle!=0)Vk.DestroyPipelineLayout(device.Device,layout,null);
        if(RenderPass.Handle!=0)Vk.DestroyRenderPass(device.Device,RenderPass,null);
        if(depthView.Handle!=0)Vk.DestroyImageView(device.Device,depthView,null);
        if(depth.Handle!=0)Vk.DestroyImage(device.Device,depth,null);
        if(depthMemory.Handle!=0)Vk.FreeMemory(device.Device,depthMemory,null);
    }
}
