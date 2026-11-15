using GlmSharp;
using MYT.Kit.Vulkan;
using Silk.NET.Vulkan;
using VoxelGame;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VoxelTests;

/// <summary>Exact packed-vertex comparisons without a window or graphics device.</summary>
internal static unsafe class HeadlessMeshTests
{
    internal static bool Run()
    {
        var gpu = TestGpu.Vulkan;
        Vk vk = gpu.Vk;
        Device device = gpu.Device;
        var allocator = new GpuBufferAllocator(vk, device, gpu.PhysicalDevice, gpu.CommandPool, gpu.Queue);
        using var loader = new ComputePipelineLoader(vk, device);
        DescriptorSetLayout layout = default;
        DescriptorPool pool = default;
        CommandBuffer commands = default;
        Fence fence = default;
        List<(Buffer Buffer, DeviceMemory Memory)> allocations = [];
        int[,] layers = new int[Blocks.Count, 7];
        for (int type = 0; type < Blocks.Count; type++) for (int face = 0; face < 7; face++) layers[type, face] = (type * 7 + face) % 200;
        var buffers = new DescriptorBufferInfo[4];
        try
        {
            DescriptorSetLayoutBinding* bindings = stackalloc DescriptorSetLayoutBinding[4];
            for (uint i = 0; i < 4; i++) bindings[i] = new(i, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit);
            DescriptorSetLayoutCreateInfo create = new() { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 4, PBindings = bindings };
            Check(vk.CreateDescriptorSetLayout(device, &create, null, out layout));
            ComputePipeline pipeline = loader.Load(Path.Combine(TestGpu.Shaders, "voxel", "culled_mesh.comp"), [layout], 16, optimize: true);
            DescriptorPoolSize size = new(DescriptorType.StorageBuffer, 4);
            DescriptorPoolCreateInfo createPool = new() { SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &size };
            Check(vk.CreateDescriptorPool(device, &createPool, null, out pool));
            DescriptorSetAllocateInfo setInfo = new() { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = pool, DescriptorSetCount = 1, PSetLayouts = &layout };
            Check(vk.AllocateDescriptorSets(device, &setInfo, out DescriptorSet set));
            CommandBufferAllocateInfo commandInfo = new() { SType = StructureType.CommandBufferAllocateInfo, CommandPool = gpu.CommandPool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
            Check(vk.AllocateCommandBuffers(device, &commandInfo, out commands));
            FenceCreateInfo fenceInfo = new() { SType = StructureType.FenceCreateInfo };
            Check(vk.CreateFence(device, &fenceInfo, null, out fence));

            nint Bind(int binding, ulong bytes)
            {
                bytes = Math.Max(16, bytes);
                nint mapped = allocator.CreateReadback(bytes, BufferUsageFlags.StorageBufferBit, out Buffer buffer, out DeviceMemory memory);
                allocations.Add((buffer, memory));
                buffers[binding] = new(buffer, 0, bytes);
                return mapped;
            }
            uint[] materialData = GpuMeshInputs.Materials(layers);
            nint materialMapping = Bind(1, (ulong)materialData.Length * 4);
            materialData.AsSpan().CopyTo(new Span<uint>((void*)materialMapping, materialData.Length));
            nint cellMapping = Bind(0, GpuMeshInputs.InputWords * 4UL);
            nint countsMapping = Bind(3, 16);
            Bind(2, 16);
            void Dispatch(uint flags)
            {
                fixed (DescriptorBufferInfo* pointers = buffers)
                {
                    WriteDescriptorSet* writes = stackalloc WriteDescriptorSet[4];
                    for (uint i = 0; i < 4; i++) writes[i] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = i, DescriptorCount = 1, DescriptorType = DescriptorType.StorageBuffer, PBufferInfo = &pointers[i] };
                    vk.UpdateDescriptorSets(device, 4, writes, 0, null);
                }
                Check(vk.ResetCommandBuffer(commands, 0));
                CommandBufferBeginInfo begin = new() { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
                Check(vk.BeginCommandBuffer(commands, &begin));
                vk.CmdBindPipeline(commands, PipelineBindPoint.Compute, pipeline.Pipeline);
                DescriptorSet descriptor = set;
                vk.CmdBindDescriptorSets(commands, PipelineBindPoint.Compute, pipeline.Layout, 0, 1, &descriptor, 0, null);
                uvec4 parameters = new(0, 0, 0, flags);
                vk.CmdPushConstants(commands, pipeline.Layout, ShaderStageFlags.ComputeBit, 0, 16, &parameters);
                vk.CmdDispatch(commands, 1, 1, 1);
                MemoryBarrier barrier = new() { SType = StructureType.MemoryBarrier, SrcAccessMask = AccessFlags.ShaderWriteBit, DstAccessMask = AccessFlags.HostReadBit };
                vk.CmdPipelineBarrier(commands, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
                Check(vk.EndCommandBuffer(commands));
                Fence submissionFence = fence;
                Check(vk.ResetFences(device, 1, &submissionFence));
                CommandBuffer command = commands;
                SubmitInfo submit = new() { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &command };
                Check(vk.QueueSubmit(gpu.Queue, 1, &submit, submissionFence));
                Check(vk.WaitForFences(device, 1, &submissionFence, true, ulong.MaxValue));
            }
            bool CompareInput(uint[] input, uint flags, uint[] expected, string label)
            {
                input.AsSpan().CopyTo(new Span<uint>((void*)cellMapping, input.Length));
                new Span<uint>((void*)countsMapping, 4).Clear();
                Dispatch(flags | 4u);
                uint quads = *(uint*)countsMapping;
                int words = checked((int)quads * 8);
                nint vertices = Bind(2, (ulong)(words + 4) * 4);
                new Span<uint>((void*)vertices, words + 4).Fill(0xDEADBEEF);
                new Span<uint>((void*)countsMapping, 4).Clear();
                Dispatch(flags);
                bool matches = expected.AsSpan().SequenceEqual(new ReadOnlySpan<uint>((void*)vertices, words));
                bool guarded = new ReadOnlySpan<uint>((uint*)vertices + words, 4).ToArray().All(v => v == 0xDEADBEEF);
                new Span<uint>((void*)vertices, words + 4).Fill(0xDEADBEEF);
                new Span<uint>((void*)countsMapping, 4).Clear();
                *(uint*)countsMapping = quads == 0 ? 0 : quads - 1;
                Dispatch(flags | 32u);
                bool overflowSafe = *(uint*)countsMapping == quads
                    && new ReadOnlySpan<uint>((void*)vertices, words + 4).ToArray().All(v => v == 0xDEADBEEF);
                new Span<uint>((void*)countsMapping, 4).Clear();
                *(uint*)countsMapping = quads;
                Dispatch(flags | 32u);
                bool bounded = *(uint*)countsMapping == quads
                    && expected.AsSpan().SequenceEqual(new ReadOnlySpan<uint>((void*)vertices, words))
                    && new ReadOnlySpan<uint>((uint*)vertices + words, 4).ToArray().All(v => v == 0xDEADBEEF);
                Console.WriteLine($"   {label}: CPU {expected.Length / 8}, GPU {quads} quads, exact vertices {matches}, output guard {guarded}, bounded emission {bounded}, overflow untouched {overflowSafe}");
                if (!matches && expected.Length == words)
                {
                    ReadOnlySpan<uint> actual = new((void*)vertices, words);
                    for (int i = 0; i < words; i++) if (actual[i] != expected[i]) { Console.WriteLine($"   first mismatch word {i}: CPU {expected[i]:X8}, GPU {actual[i]:X8}"); break; }
                }
                return matches && guarded && bounded && overflowSafe;
            }
            bool Compare(Voxel[] cells, bool transparent, string label)
            {
                uint flags = (transparent ? 1u : 0u) | (cells.Any(v => BlockShapes.HasShape(v.Type)) ? 2u : 0u);
                return CompareInput(GpuMeshInputs.Pack(cells), flags, VoxelMesher.BuildRenderMeshReference(cells, layers, transparent), label);
            }
            Random random = new(6241);
            Voxel[] cells = new Voxel[VoxelMesher.PaddedVolume];
            BlockType[] cubes = [BlockType.Stone, BlockType.Glass, BlockType.Water, BlockType.Leaves, BlockType.OakLog, BlockType.Furnace];
            for (int i = 0; i < 1600; i++) cells[random.Next(cells.Length)] = new(cubes[random.Next(cubes.Length)], 0, (BlockOrientation)random.Next(6), random.Next(2) != 0);
            bool ok = Compare(cells, false, "cube chunks, border AO, water, glass, orientations, lit furnace");
            ok &= Compare(cells, true, "transparent leaves");
            BlockType[] shapes = [BlockType.StoneSlab, BlockType.StoneStairs, BlockType.StoneCornerStairs, BlockType.StoneMissingCornerStairs, BlockType.GlassSlab, BlockType.AcaciaDoorBottom];
            for (int i = 0; i < 500; i++)
            {
                BlockType type = shapes[random.Next(shapes.Length)];
                int orientation = BlockShapes.IsDoor(type) ? random.Next(4) : BlockShapes.Kind(type) == 0 ? 32 + random.Next(6) : 64 + random.Next(8);
                cells[random.Next(cells.Length)] = new(type, 0, (BlockOrientation)orientation);
            }
            ok &= Compare(cells, false, "slabs, all stair forms, doors with exact thickness and clipped neighbors");
            ok &= Compare(cells, true, "shapes with transparent leaves");
            Array.Fill(cells, new Voxel(BlockType.Leaves, 0));
            ok &= Compare(cells, true, "worst-case transparent leaves");
            Array.Clear(cells);
            ok &= Compare(cells, false, "empty chunk");
            using TerrainLod cpuLod = new(new TerrainGenerator(713), layers);
            using TerrainLod gpuLod = new(new TerrainGenerator(713), layers, gpuMeshing: true);
            foreach (TerrainLod.Tile tile in new TerrainLod.Tile[] { new(0, 0, 32, 32), new(-64, -64, 64), new(0, 0, 8192) })
            {
                TerrainLod.Mesh reference = cpuLod.Build(tile), input = gpuLod.Build(tile);
                uint flags = 9u | (tile.Resolution >= TerrainLod.WorldHeight ? 16u : 0u)
                    | (uint)System.Numerics.BitOperations.Log2((uint)tile.Resolution) << 8;
                uint[] expected = new uint[reference.Vertices.Length * 2];
                for (int i = 0; i < reference.Vertices.Length; i++)
                {
                    expected[i * 2] = reference.Vertices[i].PackedPosition;
                    expected[i * 2 + 1] = reference.Vertices[i].PackedSurface;
                }
                ok &= CompareInput(input.GpuCells ?? new uint[GpuMeshInputs.InputWords], flags, expected, $"LOD {tile.Size}, resolution {tile.Resolution}, transition hulls and horizon");
            }
            ok &= gpu.ValidationErrorCount == 0;
            Console.WriteLine($"   Vulkan validation errors: {gpu.ValidationErrorCount}");
            return ok;
        }
        finally
        {
            foreach (var allocation in allocations) allocator.Destroy(allocation.Buffer, allocation.Memory);
            allocator.ReleaseStaging();
            if (fence.Handle != 0) vk.DestroyFence(device, fence, null);
            if (commands.Handle != 0) { CommandBuffer command = commands; vk.FreeCommandBuffers(device, gpu.CommandPool, 1, &command); }
            if (pool.Handle != 0) vk.DestroyDescriptorPool(device, pool, null);
            if (layout.Handle != 0) vk.DestroyDescriptorSetLayout(device, layout, null);
        }
    }
    private static void Check(Result result) { if (result != Result.Success) throw new InvalidOperationException(result.ToString()); }
}
