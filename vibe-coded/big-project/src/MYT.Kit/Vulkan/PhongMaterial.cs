

using System.Runtime.InteropServices;

using GlmSharp;

using MYT.IKit;
using MYT.IKit.Vulkan;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace MYT.Kit.Vulkan;


public class PhongMaterial : IMaterial, IAsset
{
    public PhongMaterial
    (
        Vk vk,
        Pipeline pipeline,
        PipelineLayout pipelineLayout,
        RenderPass renderPass,
        DescriptorSetLayout[] descriptorSetLayouts,
        DescriptorSet[] descriptorSets,
        nint[] transformMapped,
        nint[] lightingMapped
    )
    {
        _vk = vk;
        Pipeline = pipeline;
        PipelineLayout = pipelineLayout;
        RenderPass = renderPass;
        DescriptorSetLayouts = descriptorSetLayouts;
        _descriptorSets = descriptorSets;
        _transformMapped = transformMapped;
        _lightingMapped = lightingMapped;
    }

    public Pipeline Pipeline { get; }
    public PipelineLayout PipelineLayout { get; }
    public RenderPass RenderPass { get; }
    public DescriptorSetLayout[] DescriptorSetLayouts { get; }

    private readonly Vk _vk;
    private readonly DescriptorSet[] _descriptorSets;
    private readonly nint[] _transformMapped;
    private readonly nint[] _lightingMapped;


    // The Transform/Lighting UBO layouts below mirror assets/materials/phong's
    // vertex.glsl/fragment.glsl std140 blocks exactly (mat4 x3, then vec3+float
    // pairs) — any material loaded through MaterialLoader is currently assumed
    // to use this convention for descriptor set 0, bindings 0 and 1.
    [StructLayout(LayoutKind.Sequential, Size = 192)]
    private struct TransformUbo
    {
        public mat4 Model;
        public mat4 View;
        public mat4 Projection;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct LightingUbo
    {
        public vec3 LightPosition;
        public float AmbientStrength;
        public vec3 LightColor;
        public float SpecularStrength;
        public vec3 ViewPosition;
        public float Shininess;
        public vec3 ObjectColor;
    }


    public unsafe void WriteTransform(uint frameIndex, mat4 model, mat4 view, mat4 projection)
    {
        TransformUbo ubo = new() { Model = model, View = view, Projection = projection };
        System.Buffer.MemoryCopy(&ubo, (void*)_transformMapped[frameIndex], sizeof(TransformUbo), sizeof(TransformUbo));
    }

    public unsafe void WriteLighting
    (
        uint frameIndex,
        vec3 lightPosition,
        float ambientStrength,
        vec3 lightColor,
        float specularStrength,
        vec3 viewPosition,
        float shininess,
        vec3 objectColor
    )
    {
        LightingUbo ubo = new()
        {
            LightPosition = lightPosition,
            AmbientStrength = ambientStrength,
            LightColor = lightColor,
            SpecularStrength = specularStrength,
            ViewPosition = viewPosition,
            Shininess = shininess,
            ObjectColor = objectColor
        };
        System.Buffer.MemoryCopy(&ubo, (void*)_lightingMapped[frameIndex], sizeof(LightingUbo), sizeof(LightingUbo));
    }


    // IMaterial
    public unsafe void BindPipeline(CommandBuffer commandBuffer)
    {
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, Pipeline);
    }

    public unsafe void BindDescriptorSet(CommandBuffer commandBuffer, uint frameIndex)
    {
        if (_descriptorSets.Length == 0)
            return;

        DescriptorSet set = _descriptorSets[frameIndex];
        _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, PipelineLayout, 0, 1, &set, 0, null);
    }
}

public class MaterialLoader : IAssetLoader<PhongMaterial>, IDisposable
{
    private static readonly Dictionary<string, ShaderStageFlags> ShaderFileMap = new()
    {
        ["vertex.glsl"] = ShaderStageFlags.VertexBit,
        ["fragment.glsl"] = ShaderStageFlags.FragmentBit,
        ["geometry.glsl"] = ShaderStageFlags.GeometryBit,
        ["tess_control.glsl"] = ShaderStageFlags.TessellationControlBit,
        ["tess_eval.glsl"] = ShaderStageFlags.TessellationEvaluationBit,
        ["compute.glsl"] = ShaderStageFlags.ComputeBit
    };

    private static readonly Dictionary<ShaderStageFlags, ShadercShaderKind> StageToShaderKind = new()
    {
        [ShaderStageFlags.VertexBit] = ShadercShaderKind.VertexShader,
        [ShaderStageFlags.FragmentBit] = ShadercShaderKind.FragmentShader,
        [ShaderStageFlags.GeometryBit] = ShadercShaderKind.GeometryShader,
        [ShaderStageFlags.TessellationControlBit] = ShadercShaderKind.TessControlShader,
        [ShaderStageFlags.TessellationEvaluationBit] = ShadercShaderKind.TessEvaluationShader,
        [ShaderStageFlags.ComputeBit] = ShadercShaderKind.ComputeShader
    };

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly uint _framesInFlight;

    private readonly List<ShaderModule> _shaderModules = [];
    private readonly List<Pipeline> _pipelines = [];
    private readonly List<PipelineLayout> _pipelineLayouts = [];
    private readonly List<RenderPass> _renderPasses = [];
    private readonly List<DescriptorSetLayout> _descriptorSetLayouts = [];
    private readonly List<DescriptorPool> _descriptorPools = [];
    private readonly List<Buffer> _buffers = [];
    private readonly List<DeviceMemory> _memories = [];

    public MaterialLoader(Vk vk, Device device, PhysicalDevice physicalDevice, uint framesInFlight)
    {
        _vk = vk;
        _device = device;
        _physicalDevice = physicalDevice;
        _framesInFlight = framesInFlight;
    }

    public PhongMaterial Load(string filename)
    {
        if (!Directory.Exists(filename))
            throw new DirectoryNotFoundException($"Material directory not found: {filename}");

        PipelineConfig config = new();
        var configPath = Path.Combine(filename, "pipeline.json");
        if (File.Exists(configPath))
        {
            var json = File.ReadAllText(configPath);
            config = PipelineConfig.LoadFromJson(json);
        }

        using var compiler = new ShadercCompiler();
        var stageModules = new Dictionary<ShaderStageFlags, ShaderModule>();

        foreach (var (file, stage) in ShaderFileMap)
        {
            var shaderPath = Path.Combine(filename, file);
            if (!File.Exists(shaderPath))
                continue;

            var source = File.ReadAllText(shaderPath);
            var kind = StageToShaderKind[stage];
            var spirv = compiler.Compile(source, kind, file);
            var module = CreateShaderModule(spirv);
            stageModules[stage] = module;
        }

        if (stageModules.Count == 0)
            throw new InvalidOperationException($"No shader files found in material directory: {filename}");

        var descriptorSetLayouts = CreateDescriptorSetLayouts(config);
        var pipelineLayout = CreatePipelineLayout(config, descriptorSetLayouts);

        RenderPass renderPass = default;
        if (config.RenderMode == RenderMode.RenderPass)
            renderPass = CreateRenderPass(config);

        var pipeline = CreatePipeline(config, stageModules, pipelineLayout, renderPass);

        var (descriptorSets, transformMapped, lightingMapped) = CreateMaterialResources(descriptorSetLayouts);

        return new PhongMaterial(_vk, pipeline, pipelineLayout, renderPass, descriptorSetLayouts, descriptorSets, transformMapped, lightingMapped);
    }

    public unsafe void Dispose()
    {
        foreach (var module in _shaderModules)
            _vk.DestroyShaderModule(_device, module, null);
        _shaderModules.Clear();

        foreach (var pipeline in _pipelines)
            _vk.DestroyPipeline(_device, pipeline, null);
        _pipelines.Clear();

        foreach (var layout in _pipelineLayouts)
            _vk.DestroyPipelineLayout(_device, layout, null);
        _pipelineLayouts.Clear();

        foreach (var renderPass in _renderPasses)
            _vk.DestroyRenderPass(_device, renderPass, null);
        _renderPasses.Clear();

        foreach (var layout in _descriptorSetLayouts)
            _vk.DestroyDescriptorSetLayout(_device, layout, null);
        _descriptorSetLayouts.Clear();

        foreach (var buffer in _buffers)
            _vk.DestroyBuffer(_device, buffer, null);
        _buffers.Clear();

        foreach (var memory in _memories)
            _vk.FreeMemory(_device, memory, null);
        _memories.Clear();

        foreach (var pool in _descriptorPools)
            _vk.DestroyDescriptorPool(_device, pool, null);
        _descriptorPools.Clear();

        GC.SuppressFinalize(this);
    }


    // Any material loaded through this loader is assumed to use descriptor
    // set 0 as a per-frame-in-flight {binding 0: Transform, binding 1: Lighting}
    // pair, matching assets/materials/phong's pipeline.json/shaders. A material
    // with no descriptor set layouts gets no descriptor sets/UBOs at all.
    private unsafe (DescriptorSet[] DescriptorSets, nint[] TransformMapped, nint[] LightingMapped) CreateMaterialResources(DescriptorSetLayout[] descriptorSetLayouts)
    {
        if (descriptorSetLayouts.Length == 0)
            return ([], [], []);

        DescriptorPoolSize[] poolSizes =
        [
            new()
            {
                Type = DescriptorType.UniformBuffer,
                DescriptorCount = _framesInFlight * 2
            }
        ];

        DescriptorPool pool;
        fixed (DescriptorPoolSize* poolSizesPtr = poolSizes)
        {
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = (uint)poolSizes.Length,
                PPoolSizes = poolSizesPtr,
                MaxSets = _framesInFlight
            };

            if (_vk.CreateDescriptorPool(_device, &poolInfo, null, out pool) != Result.Success)
                throw new InvalidOperationException("Failed to create descriptor pool.");
            _descriptorPools.Add(pool);
        }

        var setLayouts = new DescriptorSetLayout[_framesInFlight];
        Array.Fill(setLayouts, descriptorSetLayouts[0]);

        var descriptorSets = new DescriptorSet[_framesInFlight];
        fixed (DescriptorSetLayout* setLayoutsPtr = setLayouts)
        fixed (DescriptorSet* descriptorSetsPtr = descriptorSets)
        {
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = pool,
                DescriptorSetCount = _framesInFlight,
                PSetLayouts = setLayoutsPtr
            };

            if (_vk.AllocateDescriptorSets(_device, &allocInfo, descriptorSetsPtr) != Result.Success)
                throw new InvalidOperationException("Failed to allocate descriptor sets.");
        }

        var transformMapped = new nint[_framesInFlight];
        var lightingMapped = new nint[_framesInFlight];

        for (int i = 0; i < _framesInFlight; i++)
        {
            CreateUniformBuffer(192, out Buffer transformBuffer, out transformMapped[i]);
            CreateUniformBuffer(64, out Buffer lightingBuffer, out lightingMapped[i]);

            var transformInfo = new DescriptorBufferInfo { Buffer = transformBuffer, Offset = 0, Range = 192 };
            var lightingInfo = new DescriptorBufferInfo { Buffer = lightingBuffer, Offset = 0, Range = 64 };

            var writes = new WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSets[i],
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &transformInfo
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = descriptorSets[i],
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &lightingInfo
            };

            fixed (WriteDescriptorSet* writesPtr = writes)
                _vk.UpdateDescriptorSets(_device, 2, writesPtr, 0, null);
        }

        return (descriptorSets, transformMapped, lightingMapped);
    }

    private unsafe void CreateUniformBuffer(ulong size, out Buffer buffer, out nint mapped)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.UniformBufferBit,
            SharingMode = SharingMode.Exclusive
        };

        if (_vk.CreateBuffer(_device, &bufferInfo, null, out buffer) != Result.Success)
            throw new InvalidOperationException("Failed to create uniform buffer.");
        _buffers.Add(buffer);

        _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements memRequirements);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };

        if (_vk.AllocateMemory(_device, &allocInfo, null, out DeviceMemory memory) != Result.Success)
            throw new InvalidOperationException("Failed to allocate uniform buffer memory.");
        _memories.Add(memory);

        _vk.BindBufferMemory(_device, buffer, memory, 0);

        void* mappedPtr;
        _vk.MapMemory(_device, memory, 0, size, 0, &mappedPtr);
        mapped = (nint)mappedPtr;
    }

    private unsafe uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memProperties);

        for (uint i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 && (memProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        }

        throw new InvalidOperationException("Failed to find suitable memory type.");
    }


    private unsafe ShaderModule CreateShaderModule(byte[] spirv)
    {
        fixed (byte* codePtr = spirv)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)codePtr
            };

            ShaderModule module;
            if (_vk.CreateShaderModule(_device, &createInfo, null, &module) != Result.Success)
                throw new InvalidOperationException("Failed to create shader module.");

            _shaderModules.Add(module);
            return module;
        }
    }

    private unsafe DescriptorSetLayout[] CreateDescriptorSetLayouts(PipelineConfig config)
    {
        var layouts = new DescriptorSetLayout[config.DescriptorSetLayouts.Count];

        for (int i = 0; i < config.DescriptorSetLayouts.Count; i++)
        {
            var layoutConfig = config.DescriptorSetLayouts[i];
            var bindings = new DescriptorSetLayoutBinding[layoutConfig.Bindings.Count];

            for (int j = 0; j < layoutConfig.Bindings.Count; j++)
            {
                var b = layoutConfig.Bindings[j];
                bindings[j] = new DescriptorSetLayoutBinding
                {
                    Binding = b.Binding,
                    DescriptorType = MapDescriptorType(b.DescriptorType),
                    DescriptorCount = b.DescriptorCount,
                    StageFlags = MapShaderStageFlags(b.StageFlags)
                };
            }

            fixed (DescriptorSetLayoutBinding* bindingsPtr = bindings)
            {
                var createInfo = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = (uint)bindings.Length,
                    PBindings = bindingsPtr
                };

                DescriptorSetLayout layout;
                if (_vk.CreateDescriptorSetLayout(_device, &createInfo, null, &layout) != Result.Success)
                    throw new InvalidOperationException($"Failed to create descriptor set layout {i}.");
                layouts[i] = layout;
                _descriptorSetLayouts.Add(layout);
            }
        }

        return layouts;
    }

    private unsafe PipelineLayout CreatePipelineLayout(PipelineConfig config, DescriptorSetLayout[] descriptorSetLayouts)
    {
        var pushConstantRanges = config.PushConstantRanges
            .Select(pc => new PushConstantRange
            {
                StageFlags = MapShaderStageFlags(pc.StageFlags),
                Offset = pc.Offset,
                Size = pc.Size
            }).ToArray();

        fixed (DescriptorSetLayout* layoutsPtr = descriptorSetLayouts)
        fixed (PushConstantRange* pushConstantsPtr = pushConstantRanges)
        {
            var createInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)descriptorSetLayouts.Length,
                PSetLayouts = layoutsPtr,
                PushConstantRangeCount = (uint)pushConstantRanges.Length,
                PPushConstantRanges = pushConstantsPtr
            };

            PipelineLayout layout;
            if (_vk.CreatePipelineLayout(_device, &createInfo, null, &layout) != Result.Success)
                throw new InvalidOperationException("Failed to create pipeline layout.");
            _pipelineLayouts.Add(layout);
            return layout;
        }
    }

    private unsafe RenderPass CreateRenderPass(PipelineConfig config)
    {
        var attachments = config.RenderPass.Attachments.Select(a => new AttachmentDescription
        {
            Format = MapFormat(a.Format),
            Samples = MapSampleCount(a.Samples),
            LoadOp = MapLoadOp(a.LoadOp),
            StoreOp = MapStoreOp(a.StoreOp),
            StencilLoadOp = MapLoadOp(a.StencilLoadOp),
            StencilStoreOp = MapStoreOp(a.StencilStoreOp),
            InitialLayout = MapImageLayout(a.InitialLayout),
            FinalLayout = MapImageLayout(a.FinalLayout)
        }).ToArray();

        var colorRefsList = new List<AttachmentReference[]>();
        var depthRefs = new AttachmentReference?[config.RenderPass.Subpasses.Count];
        var inputRefsList = new List<AttachmentReference[]>();

        for (int i = 0; i < config.RenderPass.Subpasses.Count; i++)
        {
            var sub = config.RenderPass.Subpasses[i];
            colorRefsList.Add(sub.ColorAttachments
                .Select(idx => new AttachmentReference { Attachment = (uint)idx, Layout = ImageLayout.ColorAttachmentOptimal })
                .ToArray());
            inputRefsList.Add(sub.InputAttachments
                .Select(idx => new AttachmentReference { Attachment = (uint)idx, Layout = ImageLayout.ShaderReadOnlyOptimal })
                .ToArray());
            if (sub.DepthStencilAttachment >= 0)
                depthRefs[i] = new AttachmentReference { Attachment = (uint)sub.DepthStencilAttachment, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        }

        var dependencies = config.RenderPass.Dependencies.Select(d => new SubpassDependency
        {
            SrcSubpass = d.SrcSubpass < 0 ? Vk.SubpassExternal : (uint)d.SrcSubpass,
            DstSubpass = d.DstSubpass < 0 ? Vk.SubpassExternal : (uint)d.DstSubpass,
            SrcStageMask = MapPipelineStageFlags(d.SrcStageMask),
            DstStageMask = MapPipelineStageFlags(d.DstStageMask),
            SrcAccessMask = MapAccessFlags(d.SrcAccessMask),
            DstAccessMask = MapAccessFlags(d.DstAccessMask)
        }).ToArray();

        var subpassDescriptions = new SubpassDescription[config.RenderPass.Subpasses.Count];
        var handles = new List<System.Runtime.InteropServices.GCHandle>();

        try
        {
            for (int i = 0; i < config.RenderPass.Subpasses.Count; i++)
            {
                var sub = config.RenderPass.Subpasses[i];
                var colorRefs = colorRefsList[i];
                var inputRefs = inputRefsList[i];

                var colorHandle = System.Runtime.InteropServices.GCHandle.Alloc(colorRefs, System.Runtime.InteropServices.GCHandleType.Pinned);
                handles.Add(colorHandle);

                subpassDescriptions[i] = new SubpassDescription
                {
                    PipelineBindPoint = sub.BindPoint == VkPipelineBindPoint.Compute
                        ? PipelineBindPoint.Compute : PipelineBindPoint.Graphics,
                    ColorAttachmentCount = (uint)colorRefs.Length,
                    PColorAttachments = (AttachmentReference*)colorHandle.AddrOfPinnedObject()
                };

                if (inputRefs.Length > 0)
                {
                    var inputHandle = System.Runtime.InteropServices.GCHandle.Alloc(inputRefs, System.Runtime.InteropServices.GCHandleType.Pinned);
                    handles.Add(inputHandle);
                    subpassDescriptions[i].InputAttachmentCount = (uint)inputRefs.Length;
                    subpassDescriptions[i].PInputAttachments = (AttachmentReference*)inputHandle.AddrOfPinnedObject();
                }

                if (depthRefs[i].HasValue)
                {
                    var depthRef = depthRefs[i]!.Value;
                    var depthArr = new[] { depthRef };
                    var depthHandle = System.Runtime.InteropServices.GCHandle.Alloc(depthArr, System.Runtime.InteropServices.GCHandleType.Pinned);
                    handles.Add(depthHandle);
                    subpassDescriptions[i].PDepthStencilAttachment = (AttachmentReference*)depthHandle.AddrOfPinnedObject();
                }

                if (sub.PreserveAttachments.Count > 0)
                {
                    var preserveArr = sub.PreserveAttachments.Select(x => (uint)x).ToArray();
                    var preserveHandle = System.Runtime.InteropServices.GCHandle.Alloc(preserveArr, System.Runtime.InteropServices.GCHandleType.Pinned);
                    handles.Add(preserveHandle);
                    subpassDescriptions[i].PreserveAttachmentCount = (uint)preserveArr.Length;
                    subpassDescriptions[i].PPreserveAttachments = (uint*)preserveHandle.AddrOfPinnedObject();
                }
            }

            fixed (AttachmentDescription* attachmentsPtr = attachments)
            fixed (SubpassDescription* subpassesPtr = subpassDescriptions)
            fixed (SubpassDependency* dependenciesPtr = dependencies)
            {
                var createInfo = new RenderPassCreateInfo
                {
                    SType = StructureType.RenderPassCreateInfo,
                    AttachmentCount = (uint)attachments.Length,
                    PAttachments = attachmentsPtr,
                    SubpassCount = (uint)subpassDescriptions.Length,
                    PSubpasses = subpassesPtr,
                    DependencyCount = (uint)dependencies.Length,
                    PDependencies = dependenciesPtr
                };

                RenderPass rp;
                if (_vk.CreateRenderPass(_device, &createInfo, null, &rp) != Result.Success)
                    throw new InvalidOperationException("Failed to create render pass.");
                _renderPasses.Add(rp);
                return rp;
            }
        }
        finally
        {
            foreach (var handle in handles)
                handle.Free();
        }
    }

    private unsafe Pipeline CreatePipeline(
        PipelineConfig config,
        Dictionary<ShaderStageFlags, ShaderModule> stageModules,
        PipelineLayout pipelineLayout,
        RenderPass renderPass)
    {
        var stageCreateInfos = new PipelineShaderStageCreateInfo[stageModules.Count];
        var entryPointPtr = System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi("main");
        int i = 0;
        foreach (var (stage, module) in stageModules)
        {
            stageCreateInfos[i++] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = stage,
                Module = module,
                PName = (byte*)entryPointPtr
            };
        }

        var vertexInput = BuildVertexInputState(config);
        var inputAssembly = BuildInputAssemblyState(config);
        var rasterization = BuildRasterizationState(config);
        var multisampling = BuildMultisamplingState(config);
        var depthStencil = BuildDepthStencilState(config);
        var colorBlendAttachments = BuildColorBlendAttachments(config);
        var viewport = BuildViewport(config);
        var scissor = BuildScissor(config);
        var dynamicStates = BuildDynamicStates(config);

        fixed (PipelineShaderStageCreateInfo* stagesPtr = stageCreateInfos)
        fixed (VertexInputBindingDescription* bindingsPtr = vertexInput.Bindings)
        fixed (VertexInputAttributeDescription* attributesPtr = vertexInput.Attributes)
        fixed (PipelineColorBlendAttachmentState* blendAttachmentsPtr = colorBlendAttachments)
        fixed (DynamicState* dynamicStatesPtr = dynamicStates)
        {
            var vertexInputState = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)vertexInput.Bindings.Length,
                PVertexBindingDescriptions = bindingsPtr,
                VertexAttributeDescriptionCount = (uint)vertexInput.Attributes.Length,
                PVertexAttributeDescriptions = attributesPtr
            };

            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                PViewports = &viewport,
                ScissorCount = 1,
                PScissors = &scissor
            };

            var colorBlendState = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = config.ColorBlend.LogicOpEnable,
                LogicOp = MapLogicOp(config.ColorBlend.LogicOp),
                AttachmentCount = (uint)colorBlendAttachments.Length,
                PAttachments = blendAttachmentsPtr
            };
            colorBlendState.BlendConstants[0] = config.ColorBlend.BlendConstants[0];
            colorBlendState.BlendConstants[1] = config.ColorBlend.BlendConstants[1];
            colorBlendState.BlendConstants[2] = config.ColorBlend.BlendConstants[2];
            colorBlendState.BlendConstants[3] = config.ColorBlend.BlendConstants[3];

            var dynamicStateInfo = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = (uint)dynamicStates.Length,
                PDynamicStates = dynamicStatesPtr
            };

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = (uint)stageCreateInfos.Length,
                PStages = stagesPtr,
                PVertexInputState = &vertexInputState,
                PInputAssemblyState = &inputAssembly,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisampling,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlendState,
                PViewportState = &viewportState,
                PDynamicState = &dynamicStateInfo,
                Layout = pipelineLayout,
                RenderPass = renderPass,
                Subpass = 0
            };

            Pipeline result;

            if (config.RenderMode == RenderMode.DynamicRendering)
            {
                var colorFormats = config.DynamicRendering.ColorAttachmentFormats
                    .Select(f => MapFormat(f)).ToArray();

                fixed (Format* colorFormatsPtr = colorFormats)
                {
                    var renderingInfo = new PipelineRenderingCreateInfo
                    {
                        SType = StructureType.PipelineRenderingCreateInfo,
                        ColorAttachmentCount = (uint)colorFormats.Length,
                        PColorAttachmentFormats = colorFormatsPtr,
                        DepthAttachmentFormat = MapFormat(config.DynamicRendering.DepthAttachmentFormat),
                        StencilAttachmentFormat = MapFormat(config.DynamicRendering.StencilAttachmentFormat)
                    };

                    pipelineInfo.PNext = &renderingInfo;

                    if (_vk.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, &result) != Result.Success)
                        throw new InvalidOperationException("Failed to create graphics pipeline.");
                }
            }
            else
            {
                if (_vk.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, &result) != Result.Success)
                    throw new InvalidOperationException("Failed to create graphics pipeline.");
            }

            System.Runtime.InteropServices.Marshal.FreeHGlobal(entryPointPtr);
            _pipelines.Add(result);
            return result;
        }
    }


    private static (VertexInputBindingDescription[] Bindings, VertexInputAttributeDescription[] Attributes) BuildVertexInputState(PipelineConfig config)
    {
        var bindings = config.VertexInput.Bindings.Select(b => new VertexInputBindingDescription
        {
            Binding = b.Binding,
            Stride = b.Stride,
            InputRate = b.InputRate == VkVertexInputRate.Instance ? Silk.NET.Vulkan.VertexInputRate.Instance : Silk.NET.Vulkan.VertexInputRate.Vertex
        }).ToArray();

        var attributes = config.VertexInput.Attributes.Select(a => new VertexInputAttributeDescription
        {
            Location = a.Location,
            Binding = a.Binding,
            Format = MapFormat(a.Format),
            Offset = a.Offset
        }).ToArray();

        return (bindings, attributes);
    }

    private static PipelineInputAssemblyStateCreateInfo BuildInputAssemblyState(PipelineConfig config) => new()
    {
        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
        Topology = MapTopology(config.InputAssembly.Topology),
        PrimitiveRestartEnable = config.InputAssembly.PrimitiveRestartEnable
    };

    private static PipelineRasterizationStateCreateInfo BuildRasterizationState(PipelineConfig config) => new()
    {
        SType = StructureType.PipelineRasterizationStateCreateInfo,
        DepthClampEnable = config.Rasterization.DepthClampEnable,
        RasterizerDiscardEnable = config.Rasterization.RasterizerDiscardEnable,
        PolygonMode = MapPolygonMode(config.Rasterization.PolygonMode),
        CullMode = MapCullMode(config.Rasterization.CullMode),
        FrontFace = config.Rasterization.FrontFace == VkFrontFace.Clockwise
            ? Silk.NET.Vulkan.FrontFace.Clockwise : Silk.NET.Vulkan.FrontFace.CounterClockwise,
        DepthBiasEnable = config.Rasterization.DepthBiasEnable,
        DepthBiasConstantFactor = config.Rasterization.DepthBiasConstantFactor,
        DepthBiasClamp = config.Rasterization.DepthBiasClamp,
        DepthBiasSlopeFactor = config.Rasterization.DepthBiasSlopeFactor,
        LineWidth = config.Rasterization.LineWidth
    };

    private static PipelineMultisampleStateCreateInfo BuildMultisamplingState(PipelineConfig config) => new()
    {
        SType = StructureType.PipelineMultisampleStateCreateInfo,
        RasterizationSamples = MapSampleCount(config.Multisampling.RasterizationSamples),
        SampleShadingEnable = config.Multisampling.SampleShadingEnable,
        MinSampleShading = config.Multisampling.MinSampleShading,
        AlphaToCoverageEnable = config.Multisampling.AlphaToCoverageEnable,
        AlphaToOneEnable = config.Multisampling.AlphaToOneEnable
    };

    private static PipelineDepthStencilStateCreateInfo BuildDepthStencilState(PipelineConfig config) => new()
    {
        SType = StructureType.PipelineDepthStencilStateCreateInfo,
        DepthTestEnable = config.DepthStencil.DepthTestEnable,
        DepthWriteEnable = config.DepthStencil.DepthWriteEnable,
        DepthCompareOp = MapCompareOp(config.DepthStencil.DepthCompareOp),
        DepthBoundsTestEnable = config.DepthStencil.DepthBoundsTestEnable,
        MinDepthBounds = config.DepthStencil.MinDepthBounds,
        MaxDepthBounds = config.DepthStencil.MaxDepthBounds,
        StencilTestEnable = config.DepthStencil.StencilTestEnable,
        Front = MapStencilOp(config.DepthStencil.Front),
        Back = MapStencilOp(config.DepthStencil.Back)
    };

    private static PipelineColorBlendAttachmentState[] BuildColorBlendAttachments(PipelineConfig config)
    {
        return config.ColorBlend.Attachments.Select(a =>
        {
            ColorComponentFlags mask = 0;
            if (a.ColorWriteR) mask |= ColorComponentFlags.RBit;
            if (a.ColorWriteG) mask |= ColorComponentFlags.GBit;
            if (a.ColorWriteB) mask |= ColorComponentFlags.BBit;
            if (a.ColorWriteA) mask |= ColorComponentFlags.ABit;

            return new PipelineColorBlendAttachmentState
            {
                BlendEnable = a.BlendEnable,
                SrcColorBlendFactor = MapBlendFactor(a.SrcColorBlendFactor),
                DstColorBlendFactor = MapBlendFactor(a.DstColorBlendFactor),
                ColorBlendOp = MapBlendOp(a.ColorBlendOp),
                SrcAlphaBlendFactor = MapBlendFactor(a.SrcAlphaBlendFactor),
                DstAlphaBlendFactor = MapBlendFactor(a.DstAlphaBlendFactor),
                AlphaBlendOp = MapBlendOp(a.AlphaBlendOp),
                ColorWriteMask = mask
            };
        }).ToArray();
    }

    private static Viewport BuildViewport(PipelineConfig config) => new()
    {
        X = config.Viewport.X,
        Y = config.Viewport.Y,
        Width = config.Viewport.Width,
        Height = config.Viewport.Height,
        MinDepth = config.Viewport.MinDepth,
        MaxDepth = config.Viewport.MaxDepth
    };

    private static Rect2D BuildScissor(PipelineConfig config) => new()
    {
        Offset = new Offset2D(config.Viewport.ScissorX, config.Viewport.ScissorY),
        Extent = new Extent2D(config.Viewport.ScissorWidth, config.Viewport.ScissorHeight)
    };

    private static DynamicState[] BuildDynamicStates(PipelineConfig config)
    {
        return config.DynamicStates.Select(MapDynamicState).ToArray();
    }


    private static PrimitiveTopology MapTopology(VkTopology t) => t switch
    {
        VkTopology.PointList => PrimitiveTopology.PointList,
        VkTopology.LineList => PrimitiveTopology.LineList,
        VkTopology.LineStrip => PrimitiveTopology.LineStrip,
        VkTopology.TriangleList => PrimitiveTopology.TriangleList,
        VkTopology.TriangleStrip => PrimitiveTopology.TriangleStrip,
        VkTopology.TriangleFan => PrimitiveTopology.TriangleFan,
        VkTopology.LineListWithAdjacency => PrimitiveTopology.LineListWithAdjacency,
        VkTopology.LineStripWithAdjacency => PrimitiveTopology.LineStripWithAdjacency,
        VkTopology.TriangleListWithAdjacency => PrimitiveTopology.TriangleListWithAdjacency,
        VkTopology.TriangleStripWithAdjacency => PrimitiveTopology.TriangleStripWithAdjacency,
        VkTopology.PatchList => PrimitiveTopology.PatchList,
        _ => PrimitiveTopology.TriangleList
    };

    private static Silk.NET.Vulkan.PolygonMode MapPolygonMode(VkPolygonMode m) => m switch
    {
        VkPolygonMode.Fill => Silk.NET.Vulkan.PolygonMode.Fill,
        VkPolygonMode.Line => Silk.NET.Vulkan.PolygonMode.Line,
        VkPolygonMode.Point => Silk.NET.Vulkan.PolygonMode.Point,
        _ => Silk.NET.Vulkan.PolygonMode.Fill
    };

    private static CullModeFlags MapCullMode(VkCullMode m) => m switch
    {
        VkCullMode.None => CullModeFlags.None,
        VkCullMode.Front => CullModeFlags.FrontBit,
        VkCullMode.Back => CullModeFlags.BackBit,
        VkCullMode.FrontAndBack => CullModeFlags.FrontAndBack,
        _ => CullModeFlags.BackBit
    };

    private static Silk.NET.Vulkan.CompareOp MapCompareOp(VkCompareOp op) => (Silk.NET.Vulkan.CompareOp)(int)op;

    private static StencilOpState MapStencilOp(StencilOpConfig c) => new()
    {
        FailOp = (Silk.NET.Vulkan.StencilOp)(int)c.FailOp,
        PassOp = (Silk.NET.Vulkan.StencilOp)(int)c.PassOp,
        DepthFailOp = (Silk.NET.Vulkan.StencilOp)(int)c.DepthFailOp,
        CompareOp = MapCompareOp(c.CompareOp),
        CompareMask = c.CompareMask,
        WriteMask = c.WriteMask,
        Reference = c.Reference
    };

    private static Silk.NET.Vulkan.BlendFactor MapBlendFactor(VkBlendFactor f) => (Silk.NET.Vulkan.BlendFactor)(int)f;
    private static Silk.NET.Vulkan.BlendOp MapBlendOp(VkBlendOp op) => (Silk.NET.Vulkan.BlendOp)(int)op;
    private static Silk.NET.Vulkan.LogicOp MapLogicOp(VkLogicOp op) => (Silk.NET.Vulkan.LogicOp)(int)op;

    private static SampleCountFlags MapSampleCount(VkSampleCount s) => s switch
    {
        VkSampleCount.Count1 => SampleCountFlags.Count1Bit,
        VkSampleCount.Count2 => SampleCountFlags.Count2Bit,
        VkSampleCount.Count4 => SampleCountFlags.Count4Bit,
        VkSampleCount.Count8 => SampleCountFlags.Count8Bit,
        VkSampleCount.Count16 => SampleCountFlags.Count16Bit,
        VkSampleCount.Count32 => SampleCountFlags.Count32Bit,
        VkSampleCount.Count64 => SampleCountFlags.Count64Bit,
        _ => SampleCountFlags.Count1Bit
    };

    private static Format MapFormat(VkFormat f) => f switch
    {
        VkFormat.Undefined => Format.Undefined,
        VkFormat.R8Unorm => Format.R8Unorm,
        VkFormat.R8Snorm => Format.R8SNorm,
        VkFormat.R8G8Unorm => Format.R8G8Unorm,
        VkFormat.R8G8Snorm => Format.R8G8SNorm,
        VkFormat.R8G8B8Unorm => Format.R8G8B8Unorm,
        VkFormat.R8G8B8Snorm => Format.R8G8B8SNorm,
        VkFormat.R8G8B8A8Unorm => Format.R8G8B8A8Unorm,
        VkFormat.R8G8B8A8Snorm => Format.R8G8B8A8SNorm,
        VkFormat.R8G8B8A8Srgb => Format.R8G8B8A8Srgb,
        VkFormat.B8G8R8A8Unorm => Format.B8G8R8A8Unorm,
        VkFormat.B8G8R8A8Srgb => Format.B8G8R8A8Srgb,
        VkFormat.R16Sfloat => Format.R16Sfloat,
        VkFormat.R16G16Sfloat => Format.R16G16Sfloat,
        VkFormat.R16G16B16Sfloat => Format.R16G16B16Sfloat,
        VkFormat.R16G16B16A16Sfloat => Format.R16G16B16A16Sfloat,
        VkFormat.R32Sfloat => Format.R32Sfloat,
        VkFormat.R32G32Sfloat => Format.R32G32Sfloat,
        VkFormat.R32G32B32Sfloat => Format.R32G32B32Sfloat,
        VkFormat.R32G32B32A32Sfloat => Format.R32G32B32A32Sfloat,
        VkFormat.R32Sint => Format.R32Sint,
        VkFormat.R32G32Sint => Format.R32G32Sint,
        VkFormat.R32G32B32Sint => Format.R32G32B32Sint,
        VkFormat.R32G32B32A32Sint => Format.R32G32B32A32Sint,
        VkFormat.R32Uint => Format.R32Uint,
        VkFormat.R32G32Uint => Format.R32G32Uint,
        VkFormat.R32G32B32Uint => Format.R32G32B32Uint,
        VkFormat.R32G32B32A32Uint => Format.R32G32B32A32Uint,
        VkFormat.D16Unorm => Format.D16Unorm,
        VkFormat.D32Sfloat => Format.D32Sfloat,
        VkFormat.D24UnormS8Uint => Format.D24UnormS8Uint,
        VkFormat.D32SfloatS8Uint => Format.D32SfloatS8Uint,
        _ => Format.Undefined
    };

    private static Silk.NET.Vulkan.AttachmentLoadOp MapLoadOp(VkAttachmentLoadOp op) => (Silk.NET.Vulkan.AttachmentLoadOp)(int)op;
    private static Silk.NET.Vulkan.AttachmentStoreOp MapStoreOp(VkAttachmentStoreOp op) => (Silk.NET.Vulkan.AttachmentStoreOp)(int)op;

    private static ImageLayout MapImageLayout(VkImageLayout l) => l switch
    {
        VkImageLayout.Undefined => ImageLayout.Undefined,
        VkImageLayout.General => ImageLayout.General,
        VkImageLayout.ColorAttachmentOptimal => ImageLayout.ColorAttachmentOptimal,
        VkImageLayout.DepthStencilAttachmentOptimal => ImageLayout.DepthStencilAttachmentOptimal,
        VkImageLayout.DepthStencilReadOnlyOptimal => ImageLayout.DepthStencilReadOnlyOptimal,
        VkImageLayout.ShaderReadOnlyOptimal => ImageLayout.ShaderReadOnlyOptimal,
        VkImageLayout.TransferSrcOptimal => ImageLayout.TransferSrcOptimal,
        VkImageLayout.TransferDstOptimal => ImageLayout.TransferDstOptimal,
        VkImageLayout.Preinitialized => ImageLayout.Preinitialized,
        VkImageLayout.PresentSrcKhr => ImageLayout.PresentSrcKhr,
        _ => ImageLayout.Undefined
    };

    private static Silk.NET.Vulkan.DescriptorType MapDescriptorType(VkDescriptorType t) => (Silk.NET.Vulkan.DescriptorType)(int)t;

    private static ShaderStageFlags MapShaderStageFlags(List<VkShaderStage> stages)
    {
        ShaderStageFlags flags = 0;
        foreach (var s in stages)
        {
            flags |= s switch
            {
                VkShaderStage.Vertex => ShaderStageFlags.VertexBit,
                VkShaderStage.Fragment => ShaderStageFlags.FragmentBit,
                VkShaderStage.Geometry => ShaderStageFlags.GeometryBit,
                VkShaderStage.TessellationControl => ShaderStageFlags.TessellationControlBit,
                VkShaderStage.TessellationEvaluation => ShaderStageFlags.TessellationEvaluationBit,
                VkShaderStage.Compute => ShaderStageFlags.ComputeBit,
                VkShaderStage.AllGraphics => ShaderStageFlags.AllGraphics,
                VkShaderStage.All => ShaderStageFlags.All,
                _ => 0
            };
        }
        return flags;
    }

    private static PipelineStageFlags MapPipelineStageFlags(List<VkPipelineStageFlag> stages)
    {
        PipelineStageFlags flags = 0;
        foreach (var s in stages)
        {
            flags |= s switch
            {
                VkPipelineStageFlag.TopOfPipe => PipelineStageFlags.TopOfPipeBit,
                VkPipelineStageFlag.DrawIndirect => PipelineStageFlags.DrawIndirectBit,
                VkPipelineStageFlag.VertexInput => PipelineStageFlags.VertexInputBit,
                VkPipelineStageFlag.VertexShader => PipelineStageFlags.VertexShaderBit,
                VkPipelineStageFlag.TessellationControlShader => PipelineStageFlags.TessellationControlShaderBit,
                VkPipelineStageFlag.TessellationEvaluationShader => PipelineStageFlags.TessellationEvaluationShaderBit,
                VkPipelineStageFlag.GeometryShader => PipelineStageFlags.GeometryShaderBit,
                VkPipelineStageFlag.FragmentShader => PipelineStageFlags.FragmentShaderBit,
                VkPipelineStageFlag.EarlyFragmentTests => PipelineStageFlags.EarlyFragmentTestsBit,
                VkPipelineStageFlag.LateFragmentTests => PipelineStageFlags.LateFragmentTestsBit,
                VkPipelineStageFlag.ColorAttachmentOutput => PipelineStageFlags.ColorAttachmentOutputBit,
                VkPipelineStageFlag.ComputeShader => PipelineStageFlags.ComputeShaderBit,
                VkPipelineStageFlag.Transfer => PipelineStageFlags.TransferBit,
                VkPipelineStageFlag.BottomOfPipe => PipelineStageFlags.BottomOfPipeBit,
                VkPipelineStageFlag.AllGraphics => PipelineStageFlags.AllGraphicsBit,
                VkPipelineStageFlag.AllCommands => PipelineStageFlags.AllCommandsBit,
                _ => 0
            };
        }
        return flags;
    }

    private static AccessFlags MapAccessFlags(List<VkAccessFlag> accessFlags)
    {
        AccessFlags flags = 0;
        foreach (var a in accessFlags)
        {
            flags |= a switch
            {
                VkAccessFlag.None => AccessFlags.None,
                VkAccessFlag.IndirectCommandRead => AccessFlags.IndirectCommandReadBit,
                VkAccessFlag.IndexRead => AccessFlags.IndexReadBit,
                VkAccessFlag.VertexAttributeRead => AccessFlags.VertexAttributeReadBit,
                VkAccessFlag.UniformRead => AccessFlags.UniformReadBit,
                VkAccessFlag.InputAttachmentRead => AccessFlags.InputAttachmentReadBit,
                VkAccessFlag.ShaderRead => AccessFlags.ShaderReadBit,
                VkAccessFlag.ShaderWrite => AccessFlags.ShaderWriteBit,
                VkAccessFlag.ColorAttachmentRead => AccessFlags.ColorAttachmentReadBit,
                VkAccessFlag.ColorAttachmentWrite => AccessFlags.ColorAttachmentWriteBit,
                VkAccessFlag.DepthStencilAttachmentRead => AccessFlags.DepthStencilAttachmentReadBit,
                VkAccessFlag.DepthStencilAttachmentWrite => AccessFlags.DepthStencilAttachmentWriteBit,
                VkAccessFlag.TransferRead => AccessFlags.TransferReadBit,
                VkAccessFlag.TransferWrite => AccessFlags.TransferWriteBit,
                VkAccessFlag.HostRead => AccessFlags.HostReadBit,
                VkAccessFlag.HostWrite => AccessFlags.HostWriteBit,
                VkAccessFlag.MemoryRead => AccessFlags.MemoryReadBit,
                VkAccessFlag.MemoryWrite => AccessFlags.MemoryWriteBit,
                _ => 0
            };
        }
        return flags;
    }

    private static DynamicState MapDynamicState(VkDynamicState s) => s switch
    {
        VkDynamicState.Viewport => DynamicState.Viewport,
        VkDynamicState.Scissor => DynamicState.Scissor,
        VkDynamicState.LineWidth => DynamicState.LineWidth,
        VkDynamicState.DepthBias => DynamicState.DepthBias,
        VkDynamicState.BlendConstants => DynamicState.BlendConstants,
        VkDynamicState.DepthBounds => DynamicState.DepthBounds,
        VkDynamicState.StencilCompareMask => DynamicState.StencilCompareMask,
        VkDynamicState.StencilWriteMask => DynamicState.StencilWriteMask,
        VkDynamicState.StencilReference => DynamicState.StencilReference,
        VkDynamicState.CullMode => DynamicState.CullMode,
        VkDynamicState.FrontFace => DynamicState.FrontFace,
        VkDynamicState.PrimitiveTopology => DynamicState.PrimitiveTopology,
        VkDynamicState.ViewportWithCount => DynamicState.ViewportWithCount,
        VkDynamicState.ScissorWithCount => DynamicState.ScissorWithCount,
        VkDynamicState.DepthTestEnable => DynamicState.DepthTestEnable,
        VkDynamicState.DepthWriteEnable => DynamicState.DepthWriteEnable,
        VkDynamicState.DepthCompareOp => DynamicState.DepthCompareOp,
        VkDynamicState.DepthBoundsTestEnable => DynamicState.DepthBoundsTestEnable,
        VkDynamicState.StencilTestEnable => DynamicState.StencilTestEnable,
        VkDynamicState.StencilOp => DynamicState.StencilOp,
        VkDynamicState.RasterizerDiscardEnable => DynamicState.RasterizerDiscardEnable,
        VkDynamicState.DepthBiasEnable => DynamicState.DepthBiasEnable,
        VkDynamicState.PrimitiveRestartEnable => DynamicState.PrimitiveRestartEnable,
        _ => DynamicState.Viewport
    };
}
