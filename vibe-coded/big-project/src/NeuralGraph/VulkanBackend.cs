// Legacy GraphModel backend. The active paired-error GPU backend is VulkanPredictiveCodingBackend.
using Silk.NET.Vulkan;

namespace NeuralGraph;

/// <summary>GPU-resident parameters, batched graph evaluation and full finite-unroll backpropagation.
/// Cyclic mode uses a workgroup per sample; layered mode cooperatively reduces each neuron's fan-in.
/// A separate kernel reduces gradients by parameter without atomics.</summary>
public sealed unsafe partial class VulkanBackend : IGraphBackend
{
    private readonly GraphModel model;
    private readonly VulkanDevice context;
    private readonly bool ownsContext;
    private readonly int capacity;
    private readonly LayerSchedule? schedule;
    private int lastBatch;
    private readonly float[] inputRates, outputRates, targetRates;
    private float gain = 1;
    public float Gain
    {
        get => gain;
        set { if (!float.IsFinite(value) || value < 0 || value > 4) throw new ArgumentOutOfRangeException(nameof(value)); gain = value; }
    }
    public bool IsLayered => schedule is not null;
    private readonly List<GpuBuffer> buffers = [];
    private DescriptorSetLayout setLayout;
    private DescriptorPool descriptorPool;
    private DescriptorSet descriptorSet;
    private PipelineLayout pipelineLayout;
    private Pipeline pipeline;
    private CommandBuffer command;
    private Fence fence;
    private bool disposed;
    public string DeviceName => context.Name;
    public PhysicalDeviceType DeviceType => context.DeviceType;
    private Vk Vk => context.Api;
    private Device Device => context.Device;

    public VulkanBackend(GraphModel model, int capacity = 256, VulkanDevice? context = null, int deviceIndex = -1, bool layered = false)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.model = model; this.capacity = capacity; ownsContext = context is null;
        inputRates = new float[checked(capacity * model.Topology.EncodedInputCount)];
        outputRates = new float[checked(capacity * model.Topology.EncodedOutputCount)]; targetRates = new float[outputRates.Length];
        this.context = context ?? new VulkanDevice(deviceIndex: deviceIndex);
        try
        {
            var t = model.Topology;
            if (layered) schedule = new(t);
            var meta = new List<int> { t.NodeCount, t.InputCount, t.Edges.Length, t.Outputs.Length, t.Steps };
            meta.AddRange(t.IncomingOffsets); meta.AddRange(t.IncomingEdges);
            meta.AddRange(t.Edges.Select(e => e.Source)); meta.AddRange(t.Edges.Select(e => e.Destination));
            var outputMap = Enumerable.Repeat(-1, t.NodeCount).ToArray();
            for (int i = 0; i < t.Outputs.Length; i++) outputMap[t.Outputs[i]] = i;
            meta.AddRange(outputMap); meta.AddRange(t.Outputs); meta.AddRange(t.OutgoingOffsets); meta.AddRange(t.OutgoingEdges);
            if (schedule is not null) meta.AddRange(schedule.Nodes);
            int stateSize = checked(capacity * (layered ? 1 : t.Steps + 1) * t.NodeCount);
            persistentBase = checked(stateSize * 2);
            int[] sizes = [checked(t.ParameterCount + t.Edges.Length), meta.Count, checked(capacity * t.EncodedInputCount), checked(capacity * t.EncodedOutputCount), checked(capacity * t.Outputs.Length), checked(persistentBase + 2 * t.ActivationCount), stateSize, checked(capacity * (t.EncodedOutputCount + 1))];
            this.context.Api.GetPhysicalDeviceProperties(this.context.PhysicalDevice, out var properties);
            continuousGroupsX = properties.Limits.MaxComputeWorkGroupCount[0];
            continuousGroupsY = properties.Limits.MaxComputeWorkGroupCount[1];
            if (properties.Limits.MaxPerStageDescriptorStorageBuffers < 8 || properties.Limits.MaxDescriptorSetStorageBuffers < 8)
                throw new NotSupportedException("The graph kernels require at least eight storage-buffer bindings.");
            foreach (int size in sizes)
                if ((ulong)size * 4 > properties.Limits.MaxStorageBufferRange)
                    throw new NotSupportedException("Graph/batch exceeds the device's storage-buffer range; reduce batch capacity or graph size.");
            if ((uint)capacity > properties.Limits.MaxComputeWorkGroupCount[0] || (ulong)(t.ParameterCount + 63) / 64 > properties.Limits.MaxComputeWorkGroupCount[0])
                throw new NotSupportedException("Graph/batch exceeds the device's compute dispatch limits.");
            if (schedule is not null)
                foreach (var layer in schedule.Layers)
                    if ((long)layer.Count * capacity > properties.Limits.MaxComputeWorkGroupCount[0])
                        throw new NotSupportedException("Layer/batch exceeds the device's compute dispatch limits.");
            foreach (int size in sizes) buffers.Add(this.context.CreateBuffer(checked((ulong)size * 4)));
            meta.ToArray().CopyTo(buffers[1].Ints); UploadParameters(); ResetState();
            var bindings = stackalloc DescriptorSetLayoutBinding[8];
            for (uint i = 0; i < 8; i++) bindings[i] = new() { Binding = i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, StageFlags = ShaderStageFlags.ComputeBit };
            var layoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 8, PBindings = bindings };
            VulkanDevice.Check(Vk.CreateDescriptorSetLayout(Device, in layoutInfo, null, out setLayout));
            var poolSize = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 8 };
            var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &poolSize };
            VulkanDevice.Check(Vk.CreateDescriptorPool(Device, in poolInfo, null, out descriptorPool));
            var sl = setLayout;
            var setInfo = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = descriptorPool, DescriptorSetCount = 1, PSetLayouts = &sl };
            VulkanDevice.Check(Vk.AllocateDescriptorSets(Device, in setInfo, out descriptorSet));
            var infos = stackalloc DescriptorBufferInfo[8]; var writes = stackalloc WriteDescriptorSet[8];
            for (int i = 0; i < 8; i++)
            {
                infos[i] = new() { Buffer = buffers[i].Buffer, Range = buffers[i].Size };
                writes[i] = new() { SType = StructureType.WriteDescriptorSet, DstSet = descriptorSet, DstBinding = (uint)i,
                    DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &infos[i] };
            }
            Vk.UpdateDescriptorSets(Device, 8, writes, 0, null);
            var push = new PushConstantRange { StageFlags = ShaderStageFlags.ComputeBit, Size = 24 };
            var pipelineInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &sl, PushConstantRangeCount = 1, PPushConstantRanges = &push };
            VulkanDevice.Check(Vk.CreatePipelineLayout(Device, in pipelineInfo, null, out pipelineLayout));
            pipeline = CreateComputePipeline(layered ? "NeuralGraph.Shaders.layered.spv" : "NeuralGraph.Shaders.graph.spv");
            continuousPipeline = CreateComputePipeline("NeuralGraph.Shaders.continuous.spv");
            command = this.context.AllocateCommand();
            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            VulkanDevice.Check(Vk.CreateFence(Device, in fenceInfo, null, out fence));
        }
        catch { Dispose(); throw; }
    }
    public void UploadParameters() { ObjectDisposedException.ThrowIf(disposed, this); model.ValidateValues();
        model.Parameters.CopyTo(buffers[0].Floats); model.Plasticity.CopyTo(buffers[0].Floats[model.Parameters.Length..]); }
    public void DownloadParameters() { ObjectDisposedException.ThrowIf(disposed, this); buffers[0].Floats[..model.Parameters.Length].CopyTo(model.Parameters); }
    public void Predict(ReadOnlySpan<float> inputs, Span<float> outputs, int batchSize)
    {
        BatchValidation.Check(model.Topology, capacity, batchSize, inputs.Length, outputs.Length);
        var encodedInput = inputRates.AsSpan(0, checked(inputs.Length * 2)); var encodedOutput = outputRates.AsSpan(0, checked(outputs.Length * 2));
        RateEncoding.Encode(inputs, encodedInput); PredictEncoded(encodedInput, encodedOutput, batchSize); RateEncoding.Decode(encodedOutput, outputs);
    }
    public void PredictEncoded(ReadOnlySpan<float> inputs, Span<float> outputs, int batchSize)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if ((inputs.Length & 1) != 0 || (outputs.Length & 1) != 0) throw new ArgumentException("Rate channels must come in pairs.");
        BatchValidation.Check(model.Topology, capacity, batchSize, inputs.Length / 2, outputs.Length / 2); RateEncoding.Validate(inputs);
        inputs.CopyTo(buffers[2].Floats); Run(batchSize, 0, false); buffers[7].Floats[..outputs.Length].CopyTo(outputs);
    }
    public float Train(ReadOnlySpan<float> inputs, ReadOnlySpan<float> targets, ReadOnlySpan<float> weights, int batchSize, float learningRate)
    {
        BatchValidation.Check(model.Topology, capacity, batchSize, inputs.Length, targets.Length); BatchValidation.Training(targets, weights, learningRate);
        var encodedInput = inputRates.AsSpan(0, checked(inputs.Length * 2)); var encodedTarget = targetRates.AsSpan(0, checked(targets.Length * 2));
        RateEncoding.Encode(inputs, encodedInput); RateEncoding.Encode(targets, encodedTarget); return TrainEncoded(encodedInput, encodedTarget, weights, batchSize, learningRate);
    }
    public float TrainEncoded(ReadOnlySpan<float> inputs, ReadOnlySpan<float> targets, ReadOnlySpan<float> weights, int batchSize, float learningRate)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if ((inputs.Length & 1) != 0 || (targets.Length & 1) != 0) throw new ArgumentException("Rate channels must come in pairs.");
        BatchValidation.Check(model.Topology, capacity, batchSize, inputs.Length / 2, targets.Length / 2);
        if (weights.Length != targets.Length / 2 || !float.IsFinite(learningRate) || learningRate <= 0) throw new ArgumentException("Invalid training weights/rate.");
        RateEncoding.Validate(inputs); RateEncoding.Validate(targets); RateEncoding.Validate(weights);
        if (model.IsFrozen)
        {
            var prediction = outputRates.AsSpan(0, targets.Length); PredictEncoded(inputs, prediction, batchSize); float loss = 0;
            for (int i = 0; i < weights.Length; i++) { float error = (prediction[2*i]-prediction[2*i+1])-(targets[2*i]-targets[2*i+1]); loss += .5f*weights[i]*error*error; }
            return loss / batchSize;
        }
        inputs.CopyTo(buffers[2].Floats); targets.CopyTo(buffers[3].Floats); weights.CopyTo(buffers[4].Floats); Run(batchSize, learningRate, true);
        float sum = 0; int offset = batchSize * model.Topology.EncodedOutputCount;
        for (int b = 0; b < batchSize; b++) sum += buffers[7].Floats[offset + b]; return sum / batchSize;
    }
    private void Run(int batch, float rate, bool train)
    {
        context.Begin(command); Vk.CmdBindPipeline(command, PipelineBindPoint.Compute, pipeline);
        var set = descriptorSet; Vk.CmdBindDescriptorSets(command, PipelineBindPoint.Compute, pipelineLayout, 0, 1, &set, 0, null);
        if (schedule is null)
        {
            Dispatch(0, batch, rate, batch);
            if (train) { ComputeBarrier(); Dispatch(1, batch, rate, batch); ComputeBarrier(); Dispatch(2, batch, rate, model.Parameters.Length); }
        }
        else
        {
            foreach (var layer in schedule.Layers) { Dispatch(0, batch, rate, layer.Count, layer.Start); ComputeBarrier(); }
            if (train)
            {
                for (int layer = schedule.Layers.Length - 1; layer >= 0; layer--)
                {
                    var range = schedule.Layers[layer]; Dispatch(1, batch, rate, range.Count, range.Start); ComputeBarrier();
                }
                Dispatch(3, batch, rate, batch); ComputeBarrier(); Dispatch(2, batch, rate, model.Parameters.Length);
            }
        }
        Barrier(PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit, PipelineStageFlags.HostBit | PipelineStageFlags.ComputeShaderBit, AccessFlags.HostReadBit | AccessFlags.ShaderReadBit);
        VulkanDevice.Check(Vk.EndCommandBuffer(command));
        var cmd = command; var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
        var f = fence; VulkanDevice.Check(Vk.ResetFences(Device, 1, &f));
        VulkanDevice.Check(Vk.QueueSubmit(context.Queue, 1, in submit, fence));
        VulkanDevice.Check(Vk.WaitForFences(Device, 1, &f, true, ulong.MaxValue));
        lastBatch = batch;
    }
    public void ReadActivations(Span<float> destination, int sampleIndex = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this); var t = model.Topology;
        if (destination.Length != t.ActivationCount || sampleIndex < 0 || sampleIndex >= lastBatch) throw new ArgumentException("Expected two nonnegative activation channels per logical node.");
        int stride = (schedule is null ? t.Steps + 1 : 1) * t.NodeCount;
        int offset = 2 * (sampleIndex * stride + (schedule is null ? t.Steps * t.NodeCount : 0));
        buffers[5].Floats.Slice(offset, t.ActivationCount).CopyTo(destination);
        if (schedule is not null) buffers[2].Floats.Slice(sampleIndex * t.EncodedInputCount, t.EncodedInputCount).CopyTo(destination);
    }
    public void ReadValues(Span<float> destination, int sampleIndex = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this); var t = model.Topology;
        if (destination.Length != t.NodeCount || sampleIndex < 0 || sampleIndex >= lastBatch) throw new ArgumentException("Logical readout shape.");
        int stride = (schedule is null ? t.Steps + 1 : 1) * t.NodeCount;
        int offset = 2 * (sampleIndex * stride + (schedule is null ? t.Steps * t.NodeCount : 0));
        for (int n = 0; n < t.NodeCount; n++)
        {
            if (schedule is not null && n < t.InputCount) { int at = sampleIndex * t.EncodedInputCount + 2*n; destination[n] = buffers[2].Floats[at]-buffers[2].Floats[at+1]; }
            else destination[n] = buffers[5].Floats[offset+2*n]-buffers[5].Floats[offset+2*n+1];
        }
    }
    private void ComputeBarrier() => Barrier(PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit, PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit);
    private void Dispatch(int mode, int batch, float rate, int count, int start = 0)
    {
        var config = new Config { Mode = mode, Batch = batch, Rate = rate, Gain = gain, Start = start, Count = count };
        Vk.CmdPushConstants(command, pipelineLayout, ShaderStageFlags.ComputeBit, 0, 24, &config);
        uint groups = schedule is not null && mode is 0 or 1 ? checked((uint)(count * batch)) :
            mode is 2 or 3 ? (uint)((count + 63) / 64) : (uint)count;
        Vk.CmdDispatch(command, groups, 1, 1);
    }
    private void Barrier(PipelineStageFlags src, AccessFlags srcAccess, PipelineStageFlags dst, AccessFlags dstAccess)
    {
        var barrier = new MemoryBarrier { SType = StructureType.MemoryBarrier, SrcAccessMask = srcAccess, DstAccessMask = dstAccess };
        Vk.CmdPipelineBarrier(command, src, dst, 0, 1, &barrier, 0, null, 0, null);
    }
    private struct Config { public int Mode, Batch; public float Rate, Gain; public int Start, Count; }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        Vk.DeviceWaitIdle(Device);
        if (fence.Handle != 0) Vk.DestroyFence(Device, fence, null);
        if (command.Handle != 0) { var c = command; Vk.FreeCommandBuffers(Device, context.CommandPool, 1, &c); }
        if (continuousPipeline.Handle != 0) Vk.DestroyPipeline(Device, continuousPipeline, null);
        if (pipeline.Handle != 0) Vk.DestroyPipeline(Device, pipeline, null);
        if (pipelineLayout.Handle != 0) Vk.DestroyPipelineLayout(Device, pipelineLayout, null);
        if (descriptorPool.Handle != 0) Vk.DestroyDescriptorPool(Device, descriptorPool, null);
        if (setLayout.Handle != 0) Vk.DestroyDescriptorSetLayout(Device, setLayout, null);
        foreach (var buffer in buffers) buffer.Dispose();
        if (ownsContext) context.Dispose();
    }
}
