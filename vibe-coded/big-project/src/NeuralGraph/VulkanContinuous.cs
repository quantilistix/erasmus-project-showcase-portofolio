using Silk.NET.Vulkan;

namespace NeuralGraph;

public sealed unsafe partial class VulkanBackend
{
    private Pipeline continuousPipeline;
    private readonly int persistentBase;
    private readonly uint continuousGroupsX,continuousGroupsY;
    private int stateBank;
    public long PropagationSteps { get; private set; }
    private Pipeline CreateComputePipeline(string resource)
    {
        using var stream=typeof(VulkanBackend).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Missing shader {resource}");
        using var ms=new MemoryStream();stream.CopyTo(ms);byte[] bytes=ms.ToArray();
        fixed(byte* code=bytes)
        {
            var info=new ShaderModuleCreateInfo { SType=StructureType.ShaderModuleCreateInfo,CodeSize=(nuint)bytes.Length,PCode=(uint*)code };
            VulkanDevice.Check(Vk.CreateShaderModule(Device,in info,null,out var shader));
            try
            {
                byte* entry=stackalloc byte[5] {109,97,105,110,0};
                var create=new ComputePipelineCreateInfo { SType=StructureType.ComputePipelineCreateInfo,Layout=pipelineLayout,Stage=new PipelineShaderStageCreateInfo {SType=StructureType.PipelineShaderStageCreateInfo,Stage=ShaderStageFlags.ComputeBit,Module=shader,PName=entry} };
                VulkanDevice.Check(Vk.CreateComputePipelines(Device,default,1,in create,null,out var result));return result;
            }
            finally { Vk.DestroyShaderModule(Device,shader,null); }
        }
    }
    public void Advance(ReadOnlySpan<float> inputs,Span<float> outputs,int steps=1)
    {
        var t=model.Topology;
        if(inputs.Length!=t.InputCount || outputs.Length!=t.Outputs.Length)throw new ArgumentException("Continuous input/output shape.");
        var x=inputRates.AsSpan(0,t.EncodedInputCount);var y=outputRates.AsSpan(0,t.EncodedOutputCount);
        RateEncoding.Encode(inputs,x);AdvanceEncoded(x,y,steps);RateEncoding.Decode(y,outputs);
    }
    public void AdvanceEncoded(ReadOnlySpan<float> inputs,Span<float> outputs,int steps=1)
    {
        ObjectDisposedException.ThrowIf(disposed,this);var t=model.Topology;
        if(inputs.Length!=t.EncodedInputCount || outputs.Length!=t.EncodedOutputCount || steps<1 || steps>1024)throw new ArgumentException("Continuous input/output shape or step count.");
        RateEncoding.Validate(inputs);
        uint nodes=(uint)(t.NodeCount-t.InputCount),gx=Math.Min(nodes,continuousGroupsX),gy=(nodes+gx-1)/gx;
        if(gy>continuousGroupsY)throw new NotSupportedException("Continuous graph exceeds dispatch limits.");
        inputs.CopyTo(buffers[2].Floats);context.Begin(command);Vk.CmdBindPipeline(command,PipelineBindPoint.Compute,continuousPipeline);
        var set=descriptorSet;Vk.CmdBindDescriptorSets(command,PipelineBindPoint.Compute,pipelineLayout,0,1,&set,0,null);
        int bank=stateBank;
        for(int tick=0;tick<steps;tick++)
        {
            var config=new Config { Gain=gain,Start=persistentBase+bank*t.ActivationCount,Count=persistentBase+(1-bank)*t.ActivationCount };
            Vk.CmdPushConstants(command,pipelineLayout,ShaderStageFlags.ComputeBit,0,24,&config);
            Vk.CmdDispatch(command,gx,gy,1);ComputeBarrier();bank=1-bank;
        }
        Barrier(PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderWriteBit,PipelineStageFlags.HostBit,AccessFlags.HostReadBit);
        VulkanDevice.Check(Vk.EndCommandBuffer(command));var cmd=command;
        var submit=new SubmitInfo { SType=StructureType.SubmitInfo,CommandBufferCount=1,PCommandBuffers=&cmd };var f=fence;
        VulkanDevice.Check(Vk.ResetFences(Device,1,&f));VulkanDevice.Check(Vk.QueueSubmit(context.Queue,1,in submit,fence));VulkanDevice.Check(Vk.WaitForFences(Device,1,&f,true,ulong.MaxValue));
        stateBank=bank;PropagationSteps+=steps;buffers[7].Floats[..outputs.Length].CopyTo(outputs);
    }
    public void ReadStateActivations(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(destination.Length!=model.Topology.ActivationCount)throw new ArgumentException("State activation shape.");
        buffers[5].Floats.Slice(persistentBase+stateBank*destination.Length,destination.Length).CopyTo(destination);
    }
    public void RestoreStateActivations(ReadOnlySpan<float> state,long propagationSteps=0)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(state.Length!=model.Topology.ActivationCount || propagationSteps<0)throw new ArgumentException("State activation shape or step count.");
        RateEncoding.Validate(state);state.CopyTo(buffers[5].Floats[persistentBase..]);state.CopyTo(buffers[5].Floats[(persistentBase+state.Length)..]);stateBank=0;PropagationSteps=propagationSteps;
    }
    public void ResetState()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        buffers[5].Floats.Slice(persistentBase,2*model.Topology.ActivationCount).Clear();stateBank=0;PropagationSteps=0;
    }
}
