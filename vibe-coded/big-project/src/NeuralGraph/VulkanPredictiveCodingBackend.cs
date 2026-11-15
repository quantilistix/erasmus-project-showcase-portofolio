using Silk.NET.Vulkan;

namespace NeuralGraph;

/// <summary>GPU-resident normal/error dynamics and local plasticity; no gradient or loss kernel.</summary>
public sealed unsafe class VulkanPredictiveCodingBackend : IPredictiveCodingBackend
{
    private readonly PredictiveCodingModel model;
    private readonly VulkanDevice context;
    private readonly List<GpuBuffer> buffers=[];
    private readonly List<GpuBuffer> residentBuffers=[];
    private const int SampleCapacity=4096;
    private bool parametersDirty=true,metadataDirty=true,stateDirty=true;
    private DescriptorSetLayout setLayout;
    private DescriptorPool pool;
    private DescriptorSet set;
    private PipelineLayout layout;
    private readonly Pipeline[] pipelines=new Pipeline[5];
    private CommandBuffer command;
    private Fence fence;
    private readonly uint maxX,maxY;
    private bool disposed;
    private float gain=1;
    private bool matchOutputErrorsToNormal;
    public bool MatchOutputErrorsToNormal
    {
        get=>matchOutputErrorsToNormal;
        set{matchOutputErrorsToNormal=value;if(value){SyncOutputErrors();stateDirty=true;}}
    }
    private void SyncOutputErrors()
    {
        int n=model.Topology.NodeCount;
        foreach(int node in model.Topology.Outputs){buffers[3].Floats[n+2*node]=buffers[3].Floats[node];buffers[3].Floats[n+2*node+1]=0;}
    }
    private Vk Vk=>context.Api;
    public string DeviceName=>context.Name+" / predictive coding / device-local weights"+(UsesSubgroupReductions?" / subgroup reductions":" / shared reductions")+(DeviceType==PhysicalDeviceType.Cpu?" (software Vulkan on CPU)":"");
    public PhysicalDeviceType DeviceType=>context.DeviceType;
    public bool UsesSubgroupReductions { get; }
    public bool UsesDeviceLocalStorage=>residentBuffers.All(b=>(b.MemoryProperties&MemoryPropertyFlags.DeviceLocalBit)!=0);
    public long PropagationSteps { get; private set; }
    public float Gain { get=>gain;set{if(!float.IsFinite(value)||value<0||value>4)throw new ArgumentOutOfRangeException(nameof(value));gain=value;} }
    public VulkanPredictiveCodingBackend(PredictiveCodingModel model,int deviceIndex=-1,bool useSubgroupReductions=true)
    {
        this.model=model;model.Validate();context=new(deviceIndex:deviceIndex);
        try
        {
            var t=model.Topology;int n=t.NodeCount,e=t.Edges.Length;
            var meta=new List<int>{n,t.InputCount,e,t.Outputs.Length};
            meta.AddRange(t.IncomingOffsets);meta.AddRange(t.IncomingEdges.Select(edge=>t.Edges[edge].Source));meta.AddRange(t.Edges.Select(v=>v.Source));meta.AddRange(t.Edges.Select(v=>v.Destination));meta.AddRange(t.OutgoingOffsets);meta.AddRange(t.OutgoingEdges.Select(edge=>t.Edges[edge].Destination));
            var outputMap=Enumerable.Repeat(-1,n).ToArray();for(int i=0;i<t.Outputs.Length;i++)outputMap[t.Outputs[i]]=i;meta.AddRange(outputMap);
            var outgoingPosition=new int[e];for(int k=0;k<e;k++)outgoingPosition[t.OutgoingEdges[k]]=k;meta.AddRange(outgoingPosition);
            var incomingPosition=new int[e];for(int k=0;k<e;k++)incomingPosition[t.IncomingEdges[k]]=k;meta.AddRange(incomingPosition);
            Vk.GetPhysicalDeviceProperties(context.PhysicalDevice,out var props);maxX=props.Limits.MaxComputeWorkGroupCount[0];maxY=props.Limits.MaxComputeWorkGroupCount[1];
            var subgroup=new PhysicalDeviceSubgroupProperties{SType=StructureType.PhysicalDeviceSubgroupProperties};
            var properties2=new PhysicalDeviceProperties2{SType=StructureType.PhysicalDeviceProperties2,PNext=&subgroup};
            Vk.GetPhysicalDeviceProperties2(context.PhysicalDevice,&properties2);
            UsesSubgroupReductions=useSubgroupReductions&&(subgroup.SupportedStages&ShaderStageFlags.ComputeBit)!=0
                &&(subgroup.SupportedOperations&SubgroupFeatureFlags.ShuffleRelativeBit)!=0;
            int[] sizes=[Math.Max(1,checked(4*e)),meta.Count,Math.Max(t.InputCount+t.Outputs.Length,2*SampleCapacity),checked(3*n)];
            foreach(int size in sizes){if((ulong)size*4>props.Limits.MaxStorageBufferRange)throw new NotSupportedException("Predictive graph exceeds GPU buffer limits.");buffers.Add(context.CreateBuffer((ulong)size*4,BufferUsageFlags.StorageBufferBit|BufferUsageFlags.TransferSrcBit|BufferUsageFlags.TransferDstBit,preferHostCached:true));}
            // Large, repeatedly traversed arrays live in GPU memory even on devices
            // without a CPU-visible VRAM heap. Mapped buffers are upload/readback staging.
            for(int i=0;i<2;i++)residentBuffers.Add(context.CreateDeviceBuffer(buffers[i].Size,BufferUsageFlags.StorageBufferBit|BufferUsageFlags.TransferDstBit|BufferUsageFlags.TransferSrcBit));
            residentBuffers.Add(context.CreateDeviceBuffer(buffers[3].Size,BufferUsageFlags.StorageBufferBit|BufferUsageFlags.TransferDstBit|BufferUsageFlags.TransferSrcBit));
            meta.ToArray().CopyTo(buffers[1].Ints);UploadParameters();buffers[3].Floats.Clear();
            var bindings=stackalloc DescriptorSetLayoutBinding[4];for(uint i=0;i<4;i++)bindings[i]=new(){Binding=i,DescriptorCount=1,DescriptorType=DescriptorType.StorageBuffer,StageFlags=ShaderStageFlags.ComputeBit};
            var setInfo=new DescriptorSetLayoutCreateInfo{SType=StructureType.DescriptorSetLayoutCreateInfo,BindingCount=4,PBindings=bindings};VulkanDevice.Check(Vk.CreateDescriptorSetLayout(context.Device,in setInfo,null,out setLayout));
            var poolSize=new DescriptorPoolSize{Type=DescriptorType.StorageBuffer,DescriptorCount=4};var poolInfo=new DescriptorPoolCreateInfo{SType=StructureType.DescriptorPoolCreateInfo,MaxSets=1,PoolSizeCount=1,PPoolSizes=&poolSize};VulkanDevice.Check(Vk.CreateDescriptorPool(context.Device,in poolInfo,null,out pool));
            var sl=setLayout;var alloc=new DescriptorSetAllocateInfo{SType=StructureType.DescriptorSetAllocateInfo,DescriptorPool=pool,DescriptorSetCount=1,PSetLayouts=&sl};VulkanDevice.Check(Vk.AllocateDescriptorSets(context.Device,in alloc,out set));
            var infos=stackalloc DescriptorBufferInfo[4];var writes=stackalloc WriteDescriptorSet[4];
            for(int i=0;i<4;i++){var storage=i<2?residentBuffers[i]:i==3?residentBuffers[2]:buffers[i];infos[i]=new(){Buffer=storage.Buffer,Range=storage.Size};writes[i]=new(){SType=StructureType.WriteDescriptorSet,DstSet=set,DstBinding=(uint)i,DescriptorCount=1,DescriptorType=DescriptorType.StorageBuffer,PBufferInfo=&infos[i]};}Vk.UpdateDescriptorSets(context.Device,4,writes,0,null);
            var push=new PushConstantRange{StageFlags=ShaderStageFlags.ComputeBit,Size=32};var lp=new PipelineLayoutCreateInfo{SType=StructureType.PipelineLayoutCreateInfo,SetLayoutCount=1,PSetLayouts=&sl,PushConstantRangeCount=1,PPushConstantRanges=&push};VulkanDevice.Check(Vk.CreatePipelineLayout(context.Device,in lp,null,out layout));
            string resource=UsesSubgroupReductions?"NeuralGraph.Shaders.predictive-subgroup.spv":"NeuralGraph.Shaders.predictive.spv";
            using var stream=typeof(VulkanPredictiveCodingBackend).Assembly.GetManifestResourceStream(resource)??throw new InvalidOperationException("Missing predictive shader.");using var ms=new MemoryStream();stream.CopyTo(ms);byte[] bytes=ms.ToArray();
            fixed(byte* code=bytes)
            {
                var shaderInfo=new ShaderModuleCreateInfo{SType=StructureType.ShaderModuleCreateInfo,CodeSize=(nuint)bytes.Length,PCode=(uint*)code};VulkanDevice.Check(Vk.CreateShaderModule(context.Device,in shaderInfo,null,out var shader));
                try
                {
                    byte* entry=stackalloc byte[5]{109,97,105,110,0};
                    var map=new SpecializationMapEntry{ConstantID=0,Offset=0,Size=4};
                    for(int phase=0;phase<pipelines.Length;phase++)
                    {
                        var specialization=new SpecializationInfo{MapEntryCount=1,PMapEntries=&map,DataSize=4,PData=&phase};
                        var info=new ComputePipelineCreateInfo{SType=StructureType.ComputePipelineCreateInfo,Layout=layout,Stage=new(){SType=StructureType.PipelineShaderStageCreateInfo,Stage=ShaderStageFlags.ComputeBit,Module=shader,PName=entry,PSpecializationInfo=&specialization}};
                        VulkanDevice.Check(Vk.CreateComputePipelines(context.Device,default,1,in info,null,out pipelines[phase]));
                    }
                }
                finally{Vk.DestroyShaderModule(context.Device,shader,null);}
            }
            command=context.AllocateCommand();var fi=new FenceCreateInfo{SType=StructureType.FenceCreateInfo};VulkanDevice.Check(Vk.CreateFence(context.Device,in fi,null,out fence));
        }
        catch{Dispose();throw;}
    }
    private struct Config{public int Mode,Clamp;public float H,Rate,Gain;public int Pad;public float WeightLimit,ActivationLimit;}
    private void Barrier(PipelineStageFlags src,AccessFlags sa,PipelineStageFlags dst,AccessFlags da){var b=new MemoryBarrier{SType=StructureType.MemoryBarrier,SrcAccessMask=sa,DstAccessMask=da};Vk.CmdPipelineBarrier(command,src,dst,0,1,&b,0,null,0,null);}
    private void Dispatch(int mode,long groups,ref Config config)
    {
        if(groups==0)return;uint gx=(uint)Math.Min(groups,maxX),gy=(uint)((groups+gx-1)/gx);if(gy>maxY)throw new NotSupportedException("Predictive dispatch exceeds GPU limits.");
        Vk.CmdBindPipeline(command,PipelineBindPoint.Compute,pipelines[mode]);
        config.Mode=mode;fixed(Config* p=&config)Vk.CmdPushConstants(command,layout,ShaderStageFlags.ComputeBit,0,32,p);Vk.CmdDispatch(command,gx,gy,1);
        Barrier(PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderWriteBit,PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit);
    }
    private void BeginCompute()
    {
        context.Begin(command);
        Barrier(PipelineStageFlags.AllCommandsBit|PipelineStageFlags.HostBit,AccessFlags.MemoryWriteBit|AccessFlags.HostWriteBit,
            PipelineStageFlags.TransferBit|PipelineStageFlags.ComputeShaderBit,AccessFlags.TransferReadBit|AccessFlags.TransferWriteBit|AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit);
        if(parametersDirty||metadataDirty||stateDirty)
        {
            for(int i=0;i<3;i++)
                if(i==0?parametersDirty:i==1?metadataDirty:stateDirty)
                {
                    var source=buffers[i==2?3:i];
                    var copy=new BufferCopy{Size=source.Size};
                    Vk.CmdCopyBuffer(command,source.Buffer,residentBuffers[i].Buffer,1,in copy);
                }
            Barrier(PipelineStageFlags.TransferBit,AccessFlags.TransferWriteBit,PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderReadBit|AccessFlags.ShaderWriteBit);
            parametersDirty=metadataDirty=stateDirty=false;
        }
        var ds=set;
        Vk.CmdBindDescriptorSets(command,PipelineBindPoint.Compute,layout,0,1,&ds,0,null);
    }
    private void Complete()
    {
        Barrier(PipelineStageFlags.ComputeShaderBit|PipelineStageFlags.TransferBit,AccessFlags.ShaderWriteBit|AccessFlags.TransferWriteBit,PipelineStageFlags.HostBit,AccessFlags.HostReadBit);
        VulkanDevice.Check(Vk.EndCommandBuffer(command));var c=command;
        var submit=new SubmitInfo{SType=StructureType.SubmitInfo,CommandBufferCount=1,PCommandBuffers=&c};var f=fence;
        VulkanDevice.Check(Vk.ResetFences(context.Device,1,&f));VulkanDevice.Check(Vk.QueueSubmit(context.Queue,1,in submit,fence));
        VulkanDevice.Check(Vk.WaitForFences(context.Device,1,&f,true,ulong.MaxValue));
    }
    public void Step(ReadOnlySpan<float> inputs,int steps,float activityStep,float weightStep,float learningRate,bool learn,ReadOnlySpan<float> outputClamps=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);var t=model.Topology;PredictiveValidation.Step(t,inputs,steps,activityStep,weightStep,learningRate,outputClamps);
        inputs.CopyTo(buffers[2].Floats);if(!outputClamps.IsEmpty)outputClamps.CopyTo(buffers[2].Floats[t.InputCount..]);
        BeginCompute();
        var config=new Config{Clamp=(outputClamps.IsEmpty?0:1)|(matchOutputErrorsToNormal?2:0),H=activityStep,Rate=weightStep*learningRate,Gain=gain,WeightLimit=model.WeightLimit,ActivationLimit=model.ActivationLimit};
        // Boundaries stay fixed for all ticks in this inference pass. Preparing
        // them once removes boundary selection and output-map gathers per edge.
        Dispatch(4,(Math.Max(t.InputCount,outputClamps.Length)+63L)/64,ref config);
        for(int tick=0;tick<steps;tick++){Dispatch(0,t.NodeCount,ref config);Dispatch(1,t.NodeCount-t.InputCount,ref config);}
        // Sync the algebraic motor error after the last normal update, before plasticity/readback.
        if(matchOutputErrorsToNormal)Dispatch(4,(t.InputCount+63L)/64,ref config);
        if(learn&&!model.IsFrozen&&config.Rate!=0)Dispatch(2,(t.Edges.Length+63L)/64,ref config);
        Barrier(PipelineStageFlags.ComputeShaderBit,AccessFlags.ShaderWriteBit,PipelineStageFlags.TransferBit,AccessFlags.TransferReadBit);
        var stateCopy=new BufferCopy{Size=buffers[3].Size};
        Vk.CmdCopyBuffer(command,residentBuffers[2].Buffer,buffers[3].Buffer,1,in stateCopy);
        Complete();
        PropagationSteps+=steps;RateEncoding.Validate(buffers[3].Floats);
    }
    public void ReadNormal(Span<float> destination){if(destination.Length!=model.Topology.NodeCount)throw new ArgumentException("Normal shape.");buffers[3].Floats[..destination.Length].CopyTo(destination);}
    public void ReadErrorRates(Span<float> destination){if(destination.Length!=2*model.Topology.NodeCount)throw new ArgumentException("Error shape.");buffers[3].Floats[model.Topology.NodeCount..].CopyTo(destination);}
    public PredictiveCodingState CaptureState(){int n=model.Topology.NodeCount;return new(buffers[3].Floats[..n].ToArray(),buffers[3].Floats[n..].ToArray(),PropagationSteps);}
    public void RestoreState(PredictiveCodingState state){PredictiveValidation.State(model.Topology,state);state.Normal.CopyTo(buffers[3].Floats);PredictiveValidation.ClampNormal(buffers[3].Floats[..model.Topology.NodeCount],model.ActivationLimit);state.ErrorRates.CopyTo(buffers[3].Floats[model.Topology.NodeCount..]);if(matchOutputErrorsToNormal)SyncOutputErrors();PropagationSteps=state.Ticks;stateDirty=true;}
    public double MeasureTotalError()=>PredictiveValidation.Sum(buffers[3].Floats[model.Topology.NodeCount..]);
    public void UploadParameters()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        model.Validate();int e=model.Weights.Length;var p=buffers[0].Floats;
        model.Weights.CopyTo(p);model.Plasticity.CopyTo(p[e..]);
        // Derived, bit-identical views in adjacency order; never learned parameters.
        for(int k=0;k<e;k++)
        {
            p[2*e+k]=model.Weights[model.Topology.OutgoingEdges[k]];
            p[3*e+k]=model.Weights[model.Topology.IncomingEdges[k]];
        }
        parametersDirty=true;
    }
    public void ReadWeights(ReadOnlySpan<int> edges,Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        PredictiveValidation.WeightSelection(model.Weights.Length,edges,destination.Length);
        for(int offset=0;offset<edges.Length;offset+=SampleCapacity)
        {
            int count=Math.Min(SampleCapacity,edges.Length-offset);
            // The boundary buffer is idle between Step calls. Reuse it for a
            // compact gather request; the next Step rewrites every live boundary.
            edges.Slice(offset,count).CopyTo(buffers[2].Ints);
            BeginCompute();var config=new Config{Pad=count};Dispatch(3,(count+63L)/64,ref config);Complete();
            buffers[2].Floats.Slice(count,count).CopyTo(destination[offset..]);
        }
    }
    public void DownloadParameters()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(model.Weights.Length>0)
        {
            BeginCompute();
            Barrier(PipelineStageFlags.ComputeShaderBit|PipelineStageFlags.TransferBit,AccessFlags.ShaderWriteBit|AccessFlags.TransferWriteBit,PipelineStageFlags.TransferBit,AccessFlags.TransferReadBit);
            var copy=new BufferCopy{Size=(ulong)model.Weights.Length*4};
            Vk.CmdCopyBuffer(command,residentBuffers[0].Buffer,buffers[0].Buffer,1,in copy);Complete();
            buffers[0].Floats[..model.Weights.Length].CopyTo(model.Weights);
        }
        model.Validate();
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;Vk.DeviceWaitIdle(context.Device);
        if(fence.Handle!=0)Vk.DestroyFence(context.Device,fence,null);
        foreach(var pipeline in pipelines)if(pipeline.Handle!=0)Vk.DestroyPipeline(context.Device,pipeline,null);
        if(layout.Handle!=0)Vk.DestroyPipelineLayout(context.Device,layout,null);
        if(pool.Handle!=0)Vk.DestroyDescriptorPool(context.Device,pool,null);
        if(setLayout.Handle!=0)Vk.DestroyDescriptorSetLayout(context.Device,setLayout,null);
        foreach(var b in residentBuffers)b.Dispose();
        foreach(var b in buffers)b.Dispose();
        context.Dispose();
    }
}
