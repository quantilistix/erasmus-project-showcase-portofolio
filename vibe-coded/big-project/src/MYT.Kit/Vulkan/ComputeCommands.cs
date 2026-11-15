using System.Runtime.InteropServices;

using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace MYT.Kit.Vulkan;


/// <summary>Push constants shared by every physics compute pass; matches the GLSL DispatchRange block.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DispatchRange
{
    public uint Offset;
    public uint Count;
    public uint Iteration;
    public uint Flags;
}


/// <summary>Records the dispatch-then-barrier pattern every physics pass uses.</summary>
internal static class ComputeCommands
{
    public const uint WorkgroupSize = 64;

    /// <summary>
    /// Binds the pipeline and descriptor sets, pushes the range, dispatches one thread per
    /// element of <c>range.Count</c>, then makes the writes visible to the next compute pass.
    /// </summary>
    public static void Dispatch
    (
        Vk vk,
        CommandBuffer cmd,
        ComputePipeline pipeline,
        ReadOnlySpan<DescriptorSet> sets,
        DispatchRange range
    )
    {
        if (range.Count == 0)
            return;

        DispatchGroups(vk, cmd, pipeline, sets, range, (range.Count + WorkgroupSize - 1) / WorkgroupSize);
    }

    /// <summary>Like <see cref="Dispatch"/>, with an explicit workgroup count.</summary>
    public static void DispatchGroups
    (
        Vk vk,
        CommandBuffer cmd,
        ComputePipeline pipeline,
        ReadOnlySpan<DescriptorSet> sets,
        DispatchRange range,
        uint groupCount
    )
    {
        Bind(vk, cmd, pipeline, sets, range);
        vk.CmdDispatch(cmd, groupCount, 1, 1);
        ComputeBarrier(vk, cmd);
    }

    /// <summary>Like <see cref="Dispatch"/>, with the workgroup count read from a buffer the GPU wrote.</summary>
    public static void DispatchIndirect
    (
        Vk vk,
        CommandBuffer cmd,
        ComputePipeline pipeline,
        ReadOnlySpan<DescriptorSet> sets,
        DispatchRange range,
        Buffer arguments,
        ulong offset
    )
    {
        Bind(vk, cmd, pipeline, sets, range);
        vk.CmdDispatchIndirect(cmd, arguments, offset);
        ComputeBarrier(vk, cmd);
    }

    public static unsafe void Barrier
    (
        Vk vk,
        CommandBuffer cmd,
        PipelineStageFlags srcStage, AccessFlags srcAccess,
        PipelineStageFlags dstStage, AccessFlags dstAccess
    )
    {
        MemoryBarrier barrier = new()
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess
        };

        vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 1, &barrier, 0, null, 0, null);
    }

    /// <summary>Makes compute writes visible to a following transfer command.</summary>
    public static void ComputeToTransfer(Vk vk, CommandBuffer cmd)
    {
        Barrier
        (
            vk, cmd,
            PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit | AccessFlags.ShaderReadBit,
            PipelineStageFlags.TransferBit, AccessFlags.TransferReadBit | AccessFlags.TransferWriteBit
        );
    }

    /// <summary>Makes transfer writes visible to following compute passes.</summary>
    public static void TransferToCompute(Vk vk, CommandBuffer cmd)
    {
        Barrier
        (
            vk, cmd,
            PipelineStageFlags.TransferBit, AccessFlags.TransferWriteBit,
            PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit
        );
    }

    private static unsafe void Bind
    (
        Vk vk,
        CommandBuffer cmd,
        ComputePipeline pipeline,
        ReadOnlySpan<DescriptorSet> sets,
        DispatchRange range
    )
    {
        vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline.Pipeline);

        fixed (DescriptorSet* setsPtr = sets)
        {
            vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, pipeline.Layout, 0, (uint)sets.Length, setsPtr, 0, null);
        }

        vk.CmdPushConstants(cmd, pipeline.Layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(DispatchRange), &range);
    }

    // Indirect dispatches read their arguments in the draw-indirect stage.
    private static void ComputeBarrier(Vk vk, CommandBuffer cmd)
    {
        Barrier
        (
            vk, cmd,
            PipelineStageFlags.ComputeShaderBit, AccessFlags.ShaderWriteBit,
            PipelineStageFlags.ComputeShaderBit | PipelineStageFlags.DrawIndirectBit,
            AccessFlags.ShaderReadBit | AccessFlags.ShaderWriteBit | AccessFlags.IndirectCommandReadBit
        );
    }
}
