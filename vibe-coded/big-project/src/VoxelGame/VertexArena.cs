using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace VoxelGame;


/// <summary>A range of a <see cref="VertexArena"/>: its page and the bytes [offset, offset + size). Empty when size is 0.</summary>
internal readonly record struct ArenaRange(int Page, ulong Offset, ulong Size)
{
    public bool IsEmpty => Size == 0;
}


/// <summary>
/// Device-local vertex memory for the chunk meshes, in a few large pages handed out in ranges.
/// A buffer and an allocation per chunk would run into the driver's limit on allocations
/// (4096 on many) well before the farthest render distances, and host memory, which a
/// buffer the CPU writes directly lives in, is read across the bus every frame. Ranges are
/// taken first fit and merged with their free neighbours when given back; filling them is up
/// to the caller, through a transfer.
/// </summary>
internal sealed unsafe class VertexArena(GraphicsContext graphics, ulong pageBytes) : IDisposable
{
    // Ranges start and end on this many bytes, a whole number of vertices of any size up to it.
    private const ulong Alignment = 256;
    private static readonly Comparer<(ulong Offset, ulong Size)> ByOffset = Comparer<(ulong Offset, ulong Size)>.Create((a, b) => a.Offset.CompareTo(b.Offset));

    private sealed class Page
    {
        public Buffer Buffer;
        public DeviceMemory Memory;
        // Free ranges by offset, none touching another.
        public readonly List<(ulong Offset, ulong Size)> Free = [];
    }

    private readonly List<Page> _pages = [];

    public ulong PageBytes { get; } = pageBytes;

    /// <summary>Bytes handed out and not given back.</summary>
    public ulong UsedBytes { get; private set; }

    /// <summary>Bytes of device memory the pages take.</summary>
    public ulong ReservedBytes => (ulong)_pages.Count * PageBytes;

    public Buffer BufferOf(int page)
    {
        return _pages[page].Buffer;
    }

    /// <summary>A range of at least <paramref name="size"/> bytes, from a new page if no page has room.</summary>
    public ArenaRange Allocate(ulong size)
    {
        ulong rounded = (size + Alignment - 1) / Alignment * Alignment;
        if (rounded > PageBytes)
            throw new ArgumentOutOfRangeException(nameof(size), $"{size} bytes do not fit in a page of {PageBytes}");

        for (int p = 0; p < _pages.Count; p++)
        {
            if (TryTake(_pages[p], rounded, out ulong offset))
            {
                UsedBytes += rounded;
                return new ArenaRange(p, offset, rounded);
            }
        }

        _pages.Add(CreatePage());
        TryTake(_pages[^1], rounded, out ulong fresh);
        UsedBytes += rounded;
        return new ArenaRange(_pages.Count - 1, fresh, rounded);
    }

    /// <summary>Gives a range back; the caller makes sure the GPU no longer reads it.</summary>
    public void Free(ArenaRange range)
    {
        if (range.IsEmpty)
            return;

        UsedBytes -= range.Size;
        List<(ulong Offset, ulong Size)> free = _pages[range.Page].Free;
        int index = free.BinarySearch((range.Offset, 0), ByOffset);
        if (index >= 0)
            throw new InvalidOperationException("The range is free already");

        index = ~index;
        ulong start = range.Offset;
        ulong end = range.Offset + range.Size;
        if (index > 0 && free[index - 1].Offset + free[index - 1].Size == start)
        {
            start = free[index - 1].Offset;
            free.RemoveAt(--index);
        }

        if (index < free.Count && free[index].Offset == end)
        {
            end = free[index].Offset + free[index].Size;
            free.RemoveAt(index);
        }

        free.Insert(index, (start, end - start));
    }

    /// <summary>Returns an unused tail after the GPU has finished writing the range.</summary>
    public ArenaRange Trim(ArenaRange range, ulong size)
    {
        ulong rounded = (size + Alignment - 1) / Alignment * Alignment;
        if (rounded > range.Size) throw new ArgumentOutOfRangeException(nameof(size));
        if (rounded < range.Size) Free(new(range.Page, range.Offset + rounded, range.Size - rounded));
        return rounded == 0 ? default : new(range.Page, range.Offset, rounded);
    }

    private static bool TryTake(Page page, ulong size, out ulong offset)
    {
        for (int i = 0; i < page.Free.Count; i++)
        {
            (ulong start, ulong length) = page.Free[i];
            if (length < size)
                continue;

            offset = start;
            if (length == size)
                page.Free.RemoveAt(i);
            else
                page.Free[i] = (start + size, length - size);
            return true;
        }

        offset = 0;
        return false;
    }

    private Page CreatePage()
    {
        Vk vk = graphics.Vk;
        BufferCreateInfo info = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = PageBytes,
            Usage = BufferUsageFlags.VertexBufferBit | BufferUsageFlags.TransferDstBit
                | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive
        };
        if (vk.CreateBuffer(graphics.Device, &info, null, out Buffer buffer) != Result.Success)
            throw new InvalidOperationException("vkCreateBuffer failed for a vertex page");

        vk.GetBufferMemoryRequirements(graphics.Device, buffer, out MemoryRequirements requirements);
        MemoryAllocateInfo allocation = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = graphics.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };
        if (vk.AllocateMemory(graphics.Device, &allocation, null, out DeviceMemory memory) != Result.Success)
            throw new InvalidOperationException("vkAllocateMemory failed for a vertex page");
        vk.BindBufferMemory(graphics.Device, buffer, memory, 0);

        Page page = new() { Buffer = buffer, Memory = memory };
        page.Free.Add((0, PageBytes));
        return page;
    }

    public void Dispose()
    {
        foreach (Page page in _pages)
        {
            graphics.Vk.DestroyBuffer(graphics.Device, page.Buffer, null);
            graphics.Vk.FreeMemory(graphics.Device, page.Memory, null);
        }

        _pages.Clear();
    }
}
