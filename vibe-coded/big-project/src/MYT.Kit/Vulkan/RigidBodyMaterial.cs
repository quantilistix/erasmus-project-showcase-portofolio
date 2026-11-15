

using System.Runtime.InteropServices;

using GlmSharp;

using MYT.IKit;
using MYT.IKit.Vulkan;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace MYT.Kit.Vulkan;


public class RigidBodyMaterial : IMaterial, IAsset
{
    public RigidBodyMaterial
    (
        Vk vk,
        Device device,
        Pipeline pipeline,
        PipelineLayout pipelineLayout,
        RenderPass renderPass,
        DescriptorSetLayout[] descriptorSetLayouts,
        DescriptorPool descriptorPool,
        uint framesInFlight,
        nint[] cameraMapped,
        nint[] lightingMapped
    )
    {
        _vk = vk;
        _device = device;
        Pipeline = pipeline;
        PipelineLayout = pipelineLayout;
        RenderPass = renderPass;
        DescriptorSetLayouts = descriptorSetLayouts;
        _descriptorPool = descriptorPool;
        _framesInFlight = framesInFlight;
        _cameraMapped = cameraMapped;
        _lightingMapped = lightingMapped;
    }

    public Pipeline Pipeline { get; }
    public PipelineLayout PipelineLayout { get; }
    public RenderPass RenderPass { get; }
    public DescriptorSetLayout[] DescriptorSetLayouts { get; }

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly DescriptorPool _descriptorPool;
    private readonly uint _framesInFlight;
    private readonly nint[] _cameraMapped;
    private readonly nint[] _lightingMapped;
    private readonly Dictionary<Buffer, DescriptorSet[]> _transformBufferSets = new();


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


    public unsafe void WriteCamera(uint frameIndex, mat4 view, mat4 projection)
    {
        CameraUBO ubo = new() { View = view, Projection = projection };
        System.Buffer.MemoryCopy(&ubo, (void*)_cameraMapped[frameIndex], sizeof(CameraUBO), sizeof(CameraUBO));
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
        LightingUBO ubo = new()
        {
            LightPosition = lightPosition,
            AmbientStrength = ambientStrength,
            LightColor = lightColor,
            SpecularStrength = specularStrength,
            ViewPosition = viewPosition,
            Shininess = shininess,
            ObjectColor = objectColor
        };
        System.Buffer.MemoryCopy(&ubo, (void*)_lightingMapped[frameIndex], sizeof(LightingUBO), sizeof(LightingUBO));
    }

    // Register a transform buffer from physics system and bind it to descriptors
    public unsafe void RegisterTransformBuffer(Buffer transformBuffer, Buffer[] cameraBuffers, Buffer[] lightingBuffers)
    {
        if (_transformBufferSets.ContainsKey(transformBuffer))
            return;

        var descriptorSets = new DescriptorSet[_framesInFlight];
        var layouts = new DescriptorSetLayout[_framesInFlight];
        Array.Fill(layouts, DescriptorSetLayouts[0]);

        fixed (DescriptorSetLayout* layoutsPtr = layouts)
        fixed (DescriptorSet* setsPtr = descriptorSets)
        {
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descriptorPool,
                DescriptorSetCount = _framesInFlight,
                PSetLayouts = layoutsPtr
            };

            if (_vk.AllocateDescriptorSets(_device, &allocInfo, setsPtr) != Result.Success)
                throw new InvalidOperationException("Failed to allocate descriptor sets for transform buffer");
        }

        // Bind all three buffers (camera, transform, lighting) to each frame's descriptor set
        for (uint i = 0; i < _framesInFlight; i++)
        {
            var cameraInfo = new DescriptorBufferInfo
            {
                Buffer = cameraBuffers[i],
                Offset = 0,
                Range = 128
            };

            var transformInfo = new DescriptorBufferInfo
            {
                Buffer = transformBuffer,
                Offset = 0,
                Range = 64 // sizeof(mat4)
            };

            var lightingInfo = new DescriptorBufferInfo
            {
                Buffer = lightingBuffers[i],
                Offset = 0,
                Range = 64
            };

            var writes = new WriteDescriptorSet[]
            {
                new()
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = descriptorSets[i],
                    DstBinding = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.UniformBuffer,
                    PBufferInfo = &cameraInfo
                },
                new()
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = descriptorSets[i],
                    DstBinding = 1,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &transformInfo
                },
                new()
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = descriptorSets[i],
                    DstBinding = 2,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.UniformBuffer,
                    PBufferInfo = &lightingInfo
                }
            };

            fixed (WriteDescriptorSet* writesPtr = writes)
            {
                _vk.UpdateDescriptorSets(_device, (uint)writes.Length, writesPtr, 0, null);
            }
        }

        _transformBufferSets[transformBuffer] = descriptorSets;
    }


    // IMaterial
    public unsafe void BindPipeline(CommandBuffer commandBuffer)
    {
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, Pipeline);
    }

    public unsafe void BindDescriptorSet(CommandBuffer commandBuffer, uint frameIndex)
    {
        // Not used for RigidBodyMaterial - use BindDescriptorSetForTransform instead
    }

    public unsafe void BindDescriptorSetForTransform(CommandBuffer commandBuffer, uint frameIndex, Buffer transformBuffer)
    {
        if (!_transformBufferSets.TryGetValue(transformBuffer, out var sets))
            throw new InvalidOperationException("Transform buffer not registered. Call RegisterTransformBuffer first.");

        DescriptorSet set = sets[frameIndex];
        _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, PipelineLayout, 0, 1, &set, 0, null);
    }
}
