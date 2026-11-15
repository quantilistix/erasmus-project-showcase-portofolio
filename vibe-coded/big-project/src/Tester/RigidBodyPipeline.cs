
using System.Runtime.InteropServices;

using GlmSharp;

using Silk.NET.Vulkan;

using MYT.Kit;
using MYT.Kit.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;


namespace IntelliCraft;


public static partial class Program
{
    private static ShaderLoader shaderLoader = null!;

    private static Pipeline rigidBodyPipeline;
    private static PipelineLayout rigidBodyPipelineLayout;
    private static DescriptorSetLayout rigidBodyDescriptorSetLayout;
    private static DescriptorPool rigidBodyDescriptorPool;

    private static Buffer[] rbCameraBuffers = null!;
    private static DeviceMemory[] rbCameraMemory = null!;
    private static nint[] rbCameraMapped = null!;

    private static Buffer[] rbLightingBuffers = null!;
    private static DeviceMemory[] rbLightingMemory = null!;
    private static nint[] rbLightingMapped = null!;

    // Per-rigid-body descriptor sets (indexed by transform buffer handle)
    private static Dictionary<ulong, DescriptorSet[]> rbDescriptorSets = new();

    // Soft body vertices are already in world space, so they share one identity model matrix.
    private static Buffer identityTransformBuffer;
    private static DeviceMemory identityTransformMemory;


    [StructLayout(LayoutKind.Sequential, Size = 128)]
    private struct CameraUBO
    {
        public mat4 View;
        public mat4 Projection;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct LightingUBO
    {
        public vec3 LightPosition;
        public float AmbientStrength;
        public vec3 LightColor;
        public float SpecularStrength;
        public vec3 ViewPosition;
        public float Shininess;
        public vec3 ObjectColor;
    }


    private static unsafe void InitRigidBodyPipeline()
    {
        shaderLoader = new ShaderLoader(vk, device);

        ShaderModule vertModule = shaderLoader.LoadVertex("../../assets/materials/rigid_body/vertex.glsl");
        ShaderModule fragModule = shaderLoader.LoadFragment("../../assets/materials/rigid_body/fragment.glsl");

        CreateRigidBodyDescriptorSetLayout();
        CreateRigidBodyPipelineLayout();
        CreateRigidBodyGraphicsPipeline(vertModule, fragModule);
        CreateRigidBodyDescriptorPool();
        CreateRigidBodyUBOs();
    }

    private static unsafe void CreateRigidBodyDescriptorSetLayout()
    {
        var bindings = new DescriptorSetLayoutBinding[]
        {
            new()
            {
                Binding = 0,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.VertexBit
            },
            new()
            {
                Binding = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.VertexBit
            },
            new()
            {
                Binding = 2,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit
            }
        };

        fixed (DescriptorSetLayoutBinding* bindingsPtr = bindings)
        {
            DescriptorSetLayoutCreateInfo layoutInfo = new()
            {
                SType = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = (uint)bindings.Length,
                PBindings = bindingsPtr
            };

            if (vk.CreateDescriptorSetLayout(device, &layoutInfo, null, out rigidBodyDescriptorSetLayout) != Result.Success)
                throw new InvalidOperationException("Failed to create rigid body descriptor set layout");
        }
    }

    private static unsafe void CreateRigidBodyPipelineLayout()
    {
        DescriptorSetLayout dsl = rigidBodyDescriptorSetLayout;
        PipelineLayoutCreateInfo layoutInfo = new()
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &dsl
        };

        if (vk.CreatePipelineLayout(device, &layoutInfo, null, out rigidBodyPipelineLayout) != Result.Success)
            throw new InvalidOperationException("Failed to create rigid body pipeline layout");
    }

    private static unsafe void CreateRigidBodyGraphicsPipeline(ShaderModule vertModule, ShaderModule fragModule)
    {
        nint entryPoint = Marshal.StringToHGlobalAnsi("main");

        var stages = new PipelineShaderStageCreateInfo[]
        {
            new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertModule,
                PName = (byte*)entryPoint
            },
            new()
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragModule,
                PName = (byte*)entryPoint
            }
        };

        VertexInputBindingDescription bindingDesc = new()
        {
            Binding = 0,
            Stride = 24,
            InputRate = VertexInputRate.Vertex
        };

        var attributeDescs = new VertexInputAttributeDescription[]
        {
            new() { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 }
        };

        fixed (PipelineShaderStageCreateInfo* stagesPtr = stages)
        fixed (VertexInputAttributeDescription* attribsPtr = attributeDescs)
        {
            PipelineVertexInputStateCreateInfo vertexInputState = new()
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &bindingDesc,
                VertexAttributeDescriptionCount = (uint)attributeDescs.Length,
                PVertexAttributeDescriptions = attribsPtr
            };

            PipelineInputAssemblyStateCreateInfo inputAssembly = new()
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList
            };

            Viewport viewport = default;
            Rect2D scissor = default;

            PipelineViewportStateCreateInfo viewportState = new()
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                PViewports = &viewport,
                ScissorCount = 1,
                PScissors = &scissor
            };

            PipelineRasterizationStateCreateInfo rasterization = new()
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.BackBit,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1.0f
            };

            PipelineMultisampleStateCreateInfo multisampling = new()
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit
            };

            PipelineDepthStencilStateCreateInfo depthStencil = new()
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthCompareOp = CompareOp.Less
            };

            PipelineColorBlendAttachmentState colorBlendAttachment = new()
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit
            };

            PipelineColorBlendStateCreateInfo colorBlend = new()
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorBlendAttachment
            };

            DynamicState* dynamicStates = stackalloc DynamicState[]
            {
                DynamicState.Viewport,
                DynamicState.Scissor
            };

            PipelineDynamicStateCreateInfo dynamicState = new()
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynamicStates
            };

            Format colorFormat = Format.B8G8R8A8Srgb;

            PipelineRenderingCreateInfo renderingInfo = new()
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ColorAttachmentCount = 1,
                PColorAttachmentFormats = &colorFormat,
                DepthAttachmentFormat = Format.D32Sfloat,
                StencilAttachmentFormat = Format.Undefined
            };

            GraphicsPipelineCreateInfo pipelineInfo = new()
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &renderingInfo,
                StageCount = (uint)stages.Length,
                PStages = stagesPtr,
                PVertexInputState = &vertexInputState,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterization,
                PMultisampleState = &multisampling,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &colorBlend,
                PDynamicState = &dynamicState,
                Layout = rigidBodyPipelineLayout,
                RenderPass = default,
                Subpass = 0
            };

            if (vk.CreateGraphicsPipelines(device, default, 1, &pipelineInfo, null, out rigidBodyPipeline) != Result.Success)
                throw new InvalidOperationException("Failed to create rigid body graphics pipeline");
        }

        Marshal.FreeHGlobal(entryPoint);
    }

    private static unsafe void CreateRigidBodyDescriptorPool()
    {
        var poolSizes = new DescriptorPoolSize[]
        {
            new() { Type = DescriptorType.UniformBuffer, DescriptorCount = FramesInFlight * 10 },
            new() { Type = DescriptorType.StorageBuffer, DescriptorCount = FramesInFlight * 10 }
        };

        fixed (DescriptorPoolSize* poolSizesPtr = poolSizes)
        {
            DescriptorPoolCreateInfo poolInfo = new()
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                MaxSets = FramesInFlight * 10,
                PoolSizeCount = (uint)poolSizes.Length,
                PPoolSizes = poolSizesPtr
            };

            if (vk.CreateDescriptorPool(device, &poolInfo, null, out rigidBodyDescriptorPool) != Result.Success)
                throw new InvalidOperationException("Failed to create rigid body descriptor pool");
        }
    }

    private static unsafe void CreateRigidBodyUBOs()
    {
        rbCameraBuffers = new Buffer[FramesInFlight];
        rbCameraMemory = new DeviceMemory[FramesInFlight];
        rbCameraMapped = new nint[FramesInFlight];

        rbLightingBuffers = new Buffer[FramesInFlight];
        rbLightingMemory = new DeviceMemory[FramesInFlight];
        rbLightingMapped = new nint[FramesInFlight];

        for (int i = 0; i < FramesInFlight; i++)
        {
            CreateMappedBuffer(128, BufferUsageFlags.UniformBufferBit, out rbCameraBuffers[i], out rbCameraMemory[i], out rbCameraMapped[i]);
            CreateMappedBuffer(64, BufferUsageFlags.UniformBufferBit, out rbLightingBuffers[i], out rbLightingMemory[i], out rbLightingMapped[i]);
        }

        CreateMappedBuffer(64, BufferUsageFlags.StorageBufferBit, out identityTransformBuffer, out identityTransformMemory, out nint identityMapped);
        mat4 identity = mat4.Identity;
        System.Buffer.MemoryCopy(&identity, (void*)identityMapped, sizeof(mat4), sizeof(mat4));
    }

    private static unsafe void CreateMappedBuffer
    (
        ulong size, BufferUsageFlags usage,
        out Buffer buffer, out DeviceMemory memory, out nint mapped
    )
    {
        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };

        if (vk.CreateBuffer(device, &bufferInfo, null, out buffer) != Result.Success)
            throw new InvalidOperationException("Failed to create uniform buffer");

        vk.GetBufferMemoryRequirements(device, buffer, out MemoryRequirements memRequirements);

        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out PhysicalDeviceMemoryProperties memProperties);
        uint memTypeIndex = uint.MaxValue;
        for (uint j = 0; j < memProperties.MemoryTypeCount; j++)
        {
            if ((memRequirements.MemoryTypeBits & (1u << (int)j)) != 0 &&
                (memProperties.MemoryTypes[(int)j].PropertyFlags &
                 (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)) ==
                (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit))
            {
                memTypeIndex = j;
                break;
            }
        }

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = memTypeIndex
        };

        if (vk.AllocateMemory(device, &allocInfo, null, out memory) != Result.Success)
            throw new InvalidOperationException("Failed to allocate uniform buffer memory");

        vk.BindBufferMemory(device, buffer, memory, 0);

        void* ptr;
        vk.MapMemory(device, memory, 0, size, 0, &ptr);
        mapped = (nint)ptr;
    }

    public static void RegisterRigidBodyForRendering(RigidBody body)
    {
        RegisterTransformForRendering(body.TransformBuffer);
    }

    public static void RegisterSoftBodyForRendering(SoftBody body)
    {
        RegisterTransformForRendering(identityTransformBuffer);
    }

    private static unsafe void RegisterTransformForRendering(Buffer transformBuffer)
    {
        ulong key = transformBuffer.Handle;
        if (rbDescriptorSets.ContainsKey(key))
            return;

        DescriptorSet[] sets = new DescriptorSet[FramesInFlight];
        DescriptorSetLayout[] layouts = new DescriptorSetLayout[FramesInFlight];
        Array.Fill(layouts, rigidBodyDescriptorSetLayout);

        fixed (DescriptorSetLayout* layoutsPtr = layouts)
        fixed (DescriptorSet* setsPtr = sets)
        {
            DescriptorSetAllocateInfo allocInfo = new()
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = rigidBodyDescriptorPool,
                DescriptorSetCount = FramesInFlight,
                PSetLayouts = layoutsPtr
            };

            if (vk.AllocateDescriptorSets(device, &allocInfo, setsPtr) != Result.Success)
                throw new InvalidOperationException("Failed to allocate rigid body rendering descriptor sets");
        }

        for (uint i = 0; i < FramesInFlight; i++)
        {
            DescriptorBufferInfo cameraInfo = new()
            {
                Buffer = rbCameraBuffers[i],
                Offset = 0,
                Range = 128
            };

            DescriptorBufferInfo transformInfo = new()
            {
                Buffer = transformBuffer,
                Offset = 0,
                Range = 64
            };

            DescriptorBufferInfo lightingInfo = new()
            {
                Buffer = rbLightingBuffers[i],
                Offset = 0,
                Range = 64
            };

            WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[]
            {
                new()
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = sets[i],
                    DstBinding = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.UniformBuffer,
                    PBufferInfo = &cameraInfo
                },
                new()
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = sets[i],
                    DstBinding = 1,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &transformInfo
                },
                new()
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = sets[i],
                    DstBinding = 2,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.UniformBuffer,
                    PBufferInfo = &lightingInfo
                }
            };

            vk.UpdateDescriptorSets(device, 3, writes, 0, null);
        }

        rbDescriptorSets[key] = sets;
    }

    private static unsafe void UpdateRigidBodyUniforms(uint frameIndex)
    {
        float aspect = (float)swapchainExtent.Width / swapchainExtent.Height;

        vec3 eye = CameraPosition();
        mat4 view = CameraViewMatrix();
        mat4 projection = mat4.Perspective(glm.Radians(45f), aspect, 0.1f, 100f);

        CameraUBO cameraUbo = new() { View = view, Projection = projection };
        System.Buffer.MemoryCopy(&cameraUbo, (void*)rbCameraMapped[frameIndex], sizeof(CameraUBO), sizeof(CameraUBO));

        LightingUBO lightingUbo = new()
        {
            LightPosition = new vec3(4f, 4f, 4f),
            AmbientStrength = 0.15f,
            LightColor = new vec3(1f, 1f, 1f),
            SpecularStrength = 0.5f,
            ViewPosition = eye,
            Shininess = 32f,
            ObjectColor = new vec3(0.85f, 0.35f, 0.2f)
        };
        System.Buffer.MemoryCopy(&lightingUbo, (void*)rbLightingMapped[frameIndex], sizeof(LightingUBO), sizeof(LightingUBO));
    }

    private static unsafe void RenderRigidBody(CommandBuffer cmd, uint frameIndex, RigidBody body)
    {
        ulong key = body.TransformBuffer.Handle;
        if (!rbDescriptorSets.TryGetValue(key, out DescriptorSet[]? sets))
            return;

        vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, rigidBodyPipeline);

        DescriptorSet set = sets[frameIndex];
        vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, rigidBodyPipelineLayout, 0, 1, &set, 0, null);

        body.Geometry.Render(cmd);
    }

    private static unsafe void RenderSoftBody(CommandBuffer cmd, uint frameIndex, SoftBody body)
    {
        if (!rbDescriptorSets.TryGetValue(identityTransformBuffer.Handle, out DescriptorSet[]? sets))
            return;

        vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, rigidBodyPipeline);

        DescriptorSet set = sets[frameIndex];
        vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, rigidBodyPipelineLayout, 0, 1, &set, 0, null);

        Buffer vertexBuffer = body.VertexBuffer;
        ulong offset = 0;
        vk.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
        vk.CmdBindIndexBuffer(cmd, body.IndexBuffer, 0, IndexType.Uint32);
        vk.CmdDrawIndexed(cmd, body.IndexCount, 1, 0, 0, 0);
    }

    private static unsafe void DestroyRigidBodyPipeline()
    {
        for (int i = 0; i < FramesInFlight; i++)
        {
            vk.UnmapMemory(device, rbCameraMemory[i]);
            vk.DestroyBuffer(device, rbCameraBuffers[i], null);
            vk.FreeMemory(device, rbCameraMemory[i], null);

            vk.UnmapMemory(device, rbLightingMemory[i]);
            vk.DestroyBuffer(device, rbLightingBuffers[i], null);
            vk.FreeMemory(device, rbLightingMemory[i], null);
        }

        vk.UnmapMemory(device, identityTransformMemory);
        vk.DestroyBuffer(device, identityTransformBuffer, null);
        vk.FreeMemory(device, identityTransformMemory, null);

        vk.DestroyDescriptorPool(device, rigidBodyDescriptorPool, null);
        vk.DestroyPipeline(device, rigidBodyPipeline, null);
        vk.DestroyPipelineLayout(device, rigidBodyPipelineLayout, null);
        vk.DestroyDescriptorSetLayout(device, rigidBodyDescriptorSetLayout, null);

        shaderLoader.Dispose();
    }
}
