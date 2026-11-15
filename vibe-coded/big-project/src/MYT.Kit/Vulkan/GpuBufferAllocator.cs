

using System.Runtime.CompilerServices;

using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace MYT.Kit.Vulkan;


/// <summary>
/// Creates, fills and frees buffers on one device. Uploads go through a temporary staging
/// buffer and block on the queue, so they are meant for setup, not per-frame data. Between
/// <see cref="BeginBatch"/> and <see cref="EndBatch"/>, uploads and fills are recorded into one
/// submission instead, which waits once for all of them.
/// <para>
/// Allocating and freeing device memory costs a good fraction of a millisecond a buffer, so a
/// rebuild that frees its buffers and makes them again <see cref="Recycle"/>s them instead:
/// <see cref="Create"/> hands a recycled buffer of the same kind out again when it is big
/// enough and not much bigger, until <see cref="FreeRecycled"/> frees what was left over. A
/// buffer's descriptors cover <see cref="RangeOf"/>, the size asked for, so a shader sees the
/// same array length in a recycled buffer as in a new one.
/// </para>
/// </summary>
internal sealed class GpuBufferAllocator
{
    // Vulkan rejects zero-sized buffers; empty arrays still need a valid binding.
    private const ulong MinimumSize = 16;
    // Room a buffer made to be recycled gets beyond the size asked for, a quarter of it, so
    // that one that grows a little from rebuild to rebuild is still recycled.
    private const ulong HeadroomDivisor = 4;

    /// <summary>A buffer this allocator made: what it holds, what it is for, and its persistent mapping if it has one.</summary>
    private readonly record struct Made(ulong Capacity, ulong Size, BufferUsageFlags Usage, MemoryPropertyFlags Properties, DeviceMemory Memory, nint Mapped);

    public GpuBufferAllocator(Vk vk, Device device, PhysicalDevice physicalDevice, CommandPool commandPool, Queue queue)
    {
        _vk = vk;
        _device = device;
        _physicalDevice = physicalDevice;
        _commandPool = commandPool;
        _queue = queue;
    }

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly CommandPool _commandPool;
    private readonly Queue _queue;
    private CommandBuffer _batch;
    private readonly List<(Buffer Buffer, DeviceMemory Memory)> _batchStaging = [];
    private readonly Dictionary<ulong, Made> _made = [];
    private readonly List<Buffer> _recycled = [];

    // A staging buffer kept between batches, which uploads in a batch fill one after another;
    // it grows to the largest batch seen, so a typical rebuild allocates no staging memory.
    private Buffer _arena;
    private DeviceMemory _arenaMemory;
    private nint _arenaMapped;
    private ulong _arenaSize;
    private ulong _arenaUsed;
    private ulong _batchBytes;

    // The buffer downloads come through, kept and grown like the arena.
    private Buffer _download;
    private DeviceMemory _downloadMemory;
    private nint _downloadMapped;
    private ulong _downloadSize;

    /// <summary>Starts collecting uploads and fills; nothing reaches the GPU until <see cref="EndBatch"/>.</summary>
    public void BeginBatch()
    {
        if (_batch.Handle != 0)
            throw new InvalidOperationException("A batch is already open");

        _batch = BeginOneTimeCommands();
    }

    /// <summary>Submits every upload and fill since <see cref="BeginBatch"/> and waits for them.</summary>
    public void EndBatch()
    {
        CommandBuffer cmd = _batch;
        _batch = default;
        EndOneTimeCommands(cmd);

        foreach ((Buffer buffer, DeviceMemory memory) in _batchStaging)
            Destroy(buffer, memory);
        _batchStaging.Clear();

        if (_batchBytes > _arenaSize)
        {
            Destroy(_arena, _arenaMemory);
            _arenaSize = _batchBytes + _batchBytes / 4;
            _arenaMapped = CreateMapped(_arenaSize, BufferUsageFlags.TransferSrcBit, out _arena, out _arenaMemory);
        }

        _arenaUsed = 0;
        _batchBytes = 0;
    }

    /// <summary>Frees the staging memory kept between batches and downloads, and what is recycled.</summary>
    public void ReleaseStaging()
    {
        Destroy(_arena, _arenaMemory);
        _arena = default;
        _arenaMemory = default;
        _arenaSize = 0;
        Destroy(_download, _downloadMemory);
        _download = default;
        _downloadMemory = default;
        _downloadSize = 0;
        FreeRecycled();
    }

    /// <param name="headroom">It will be recycled, and may need a little more room next time.</param>
    public void Create
    (
        ulong size,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        out Buffer buffer,
        out DeviceMemory memory,
        bool headroom = false
    )
    {
        Obtain(size, usage, properties, map: false, headroom, out buffer, out memory);
    }

    /// <summary>
    /// Hands a buffer back to be given out again by <see cref="Create"/> rather than freed. It
    /// must not be used any more, and the GPU must be done with it. Unset handles are ignored.
    /// </summary>
    public void Recycle(Buffer buffer, DeviceMemory memory)
    {
        if (buffer.Handle == 0)
            return;
        if (!_made.TryGetValue(buffer.Handle, out Made made) || made.Memory.Handle != memory.Handle)
            throw new InvalidOperationException("Only buffers made by this allocator can be recycled");

        _recycled.Add(buffer);
    }

    /// <summary>Frees the recycled buffers nothing took again.</summary>
    public void FreeRecycled()
    {
        foreach (Buffer buffer in _recycled)
            Destroy(buffer, _made[buffer.Handle].Memory);
        _recycled.Clear();
    }

    /// <summary>The size a buffer was asked for, which its descriptors cover: less than it holds when it was recycled.</summary>
    public ulong RangeOf(Buffer buffer)
    {
        return _made.TryGetValue(buffer.Handle, out Made made) ? made.Size : Vk.WholeSize;
    }

    /// <summary>Retains a resident buffer when its allocation can hold a new logical range.</summary>
    public bool TryResizeRange(Buffer buffer, ulong size)
    {
        size = Math.Max(size, MinimumSize);
        if (!_made.TryGetValue(buffer.Handle, out Made made) || made.Capacity < size)
            return false;
        _made[buffer.Handle] = made with { Size = size };
        return true;
    }

    /// <summary>A recycled buffer of this kind that holds <paramref name="size"/> bytes and not twice as many, or a new one; mapped if asked.</summary>
    private unsafe nint Obtain
    (
        ulong size,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        bool map,
        bool headroom,
        out Buffer buffer,
        out DeviceMemory memory
    )
    {
        size = Math.Max(size, MinimumSize);
        int best = -1;
        for (int i = 0; i < _recycled.Count; i++)
        {
            Made candidate = _made[_recycled[i].Handle];
            if (candidate.Usage != usage || candidate.Properties != properties || candidate.Capacity < size || candidate.Capacity > 2 * size)
                continue;
            if (best < 0 || candidate.Capacity < _made[_recycled[best].Handle].Capacity)
                best = i;
        }

        if (best >= 0)
        {
            buffer = _recycled[best];
            _recycled.RemoveAt(best);
            Made reused = _made[buffer.Handle] with { Size = size };
            memory = reused.Memory;
            if (map && reused.Mapped == 0)
                reused = reused with { Mapped = Map(memory) };

            _made[buffer.Handle] = reused;
            return reused.Mapped;
        }

        ulong capacity = headroom ? size + size / HeadroomDivisor : size;
        Allocate(capacity, usage, properties, out buffer, out memory);
        nint mapped = map ? Map(memory) : 0;
        _made[buffer.Handle] = new Made(capacity, size, usage, properties, memory, mapped);
        return mapped;
    }

    private unsafe nint Map(DeviceMemory memory)
    {
        void* mapped;
        _vk.MapMemory(_device, memory, 0, Vk.WholeSize, 0, &mapped);
        return (nint)mapped;
    }

    private unsafe void Allocate
    (
        ulong size,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        out Buffer buffer,
        out DeviceMemory memory
    )
    {
        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };

        if (_vk.CreateBuffer(_device, &bufferInfo, null, out buffer) != Result.Success)
            throw new InvalidOperationException("Failed to create buffer");

        _vk.GetBufferMemoryRequirements(_device, buffer, out MemoryRequirements requirements);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, properties)
        };

        if (_vk.AllocateMemory(_device, &allocInfo, null, out memory) != Result.Success)
            throw new InvalidOperationException("Failed to allocate buffer memory");

        _vk.BindBufferMemory(_device, buffer, memory, 0);
    }

    /// <param name="headroom">It will be recycled, and may need a little more room next time.</param>
    public void CreateDeviceLocal<T>
    (
        ReadOnlySpan<T> data,
        BufferUsageFlags usage,
        out Buffer buffer,
        out DeviceMemory memory,
        bool headroom = false
    ) where T : unmanaged
    {
        ulong size = (ulong)(data.Length * Unsafe.SizeOf<T>());
        Create(size, usage | BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.DeviceLocalBit, out buffer, out memory, headroom);

        if (data.Length > 0)
            Upload(data, buffer);
    }

    /// <summary>Creates a host-visible, coherent buffer and returns its persistent mapping.</summary>
    public nint CreateMapped(ulong size, BufferUsageFlags usage, out Buffer buffer, out DeviceMemory memory, bool headroom = false)
    {
        return Obtain(size, usage, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, map: true, headroom, out buffer, out memory);
    }

    /// <summary>
    /// Like <see cref="CreateMapped"/>, in memory the CPU caches when the device has it, for
    /// buffers the CPU reads: uncached host memory is written fast but read very slowly, a few
    /// hundred kilobytes taking milliseconds.
    /// </summary>
    public nint CreateReadback(ulong size, BufferUsageFlags usage, out Buffer buffer, out DeviceMemory memory)
    {
        MemoryPropertyFlags visible = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memProperties);
        bool cached = false;
        for (int i = 0; i < memProperties.MemoryTypeCount; i++)
            cached |= (memProperties.MemoryTypes[i].PropertyFlags & (visible | MemoryPropertyFlags.HostCachedBit)) == (visible | MemoryPropertyFlags.HostCachedBit);

        return Obtain(size, usage, cached ? visible | MemoryPropertyFlags.HostCachedBit : visible, map: true, headroom: false, out buffer, out memory);
    }

    public unsafe void Upload<T>(ReadOnlySpan<T> data, Buffer destination, ulong destinationOffset = 0) where T : unmanaged
    {
        ulong size = (ulong)(data.Length * Unsafe.SizeOf<T>());
        if (_batch.Handle != 0)
        {
            _batchBytes += (size + 15) & ~15UL;
            if (_arenaUsed + size <= _arenaSize)
            {
                fixed (T* source = data)
                {
                    System.Buffer.MemoryCopy(source, (void*)(_arenaMapped + (nint)_arenaUsed), (long)size, (long)size);
                }

                BufferCopy fromArena = new() { SrcOffset = _arenaUsed, DstOffset = destinationOffset, Size = size };
                _vk.CmdCopyBuffer(_batch, _arena, destination, 1, &fromArena);
                _arenaUsed = (_arenaUsed + size + 15) & ~15UL;
                return;
            }
        }

        nint mapped = CreateMapped(size, BufferUsageFlags.TransferSrcBit, out Buffer staging, out DeviceMemory stagingMemory);

        fixed (T* source = data)
        {
            System.Buffer.MemoryCopy(source, (void*)mapped, (long)size, (long)size);
        }

        BufferCopy region = new() { DstOffset = destinationOffset, Size = size };
        if (_batch.Handle != 0)
        {
            _vk.CmdCopyBuffer(_batch, staging, destination, 1, &region);
            _batchStaging.Add((staging, stagingMemory));
            return;
        }

        CommandBuffer cmd = BeginOneTimeCommands();
        _vk.CmdCopyBuffer(cmd, staging, destination, 1, &region);
        EndOneTimeCommands(cmd);

        Destroy(staging, stagingMemory);
    }

    /// <summary>
    /// Copies a buffer back to the CPU, blocking until the queue is idle. The source needs
    /// TransferSrc usage. Meant for rare events such as rebuilding the simulation, not per frame.
    /// </summary>
    public unsafe T[] Download<T>(Buffer source, int count) where T : unmanaged
    {
        if (count == 0)
            return [];
        if (_batch.Handle != 0)
            throw new InvalidOperationException("Downloads cannot be part of a batch");

        ulong size = (ulong)(count * Unsafe.SizeOf<T>());
        if (size > _downloadSize)
        {
            Destroy(_download, _downloadMemory);
            _downloadSize = size + size / HeadroomDivisor;
            _downloadMapped = CreateReadback(_downloadSize, BufferUsageFlags.TransferDstBit, out _download, out _downloadMemory);
        }

        CommandBuffer cmd = BeginOneTimeCommands();
        BufferCopy region = new() { Size = size };
        _vk.CmdCopyBuffer(cmd, source, _download, 1, &region);
        EndOneTimeCommands(cmd);

        return new ReadOnlySpan<T>((void*)_downloadMapped, count).ToArray();
    }

    /// <summary>Fills a whole buffer (created with TransferDst usage) with a repeated uint, blocking.</summary>
    public void Fill(Buffer buffer, uint value)
    {
        if (_batch.Handle != 0)
        {
            _vk.CmdFillBuffer(_batch, buffer, 0, Vk.WholeSize, value);
            return;
        }

        CommandBuffer cmd = BeginOneTimeCommands();
        _vk.CmdFillBuffer(cmd, buffer, 0, Vk.WholeSize, value);
        EndOneTimeCommands(cmd);
    }

    /// <summary>Frees a buffer and its memory; unset handles are ignored. Freeing also unmaps.</summary>
    public unsafe void Destroy(Buffer buffer, DeviceMemory memory)
    {
        if (buffer.Handle != 0)
        {
            _made.Remove(buffer.Handle);
            _vk.DestroyBuffer(_device, buffer, null);
        }

        if (memory.Handle != 0)
            _vk.FreeMemory(_device, memory, null);
    }

    private unsafe CommandBuffer BeginOneTimeCommands()
    {
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = _commandPool,
            CommandBufferCount = 1
        };

        _vk.AllocateCommandBuffers(_device, &allocInfo, out CommandBuffer cmd);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        _vk.BeginCommandBuffer(cmd, &beginInfo);
        return cmd;
    }

    private unsafe void EndOneTimeCommands(CommandBuffer cmd)
    {
        _vk.EndCommandBuffer(cmd);

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd
        };

        _vk.QueueSubmit(_queue, 1, &submitInfo, default);
        _vk.QueueWaitIdle(_queue);
        _vk.FreeCommandBuffers(_device, _commandPool, 1, &cmd);
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out PhysicalDeviceMemoryProperties memProperties);

        for (uint i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 &&
                (memProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
            {
                return i;
            }
        }

        throw new InvalidOperationException("Failed to find suitable memory type");
    }
}
