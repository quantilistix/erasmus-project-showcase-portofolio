

using Silk.NET.Vulkan;

namespace MYT.IKit.Vulkan;


public interface IMaterial
{

    Pipeline Pipeline { get; }

    void BindPipeline(CommandBuffer commandBuffer);
    void BindDescriptorSet(CommandBuffer commandBuffer, uint frameIndex);

}
