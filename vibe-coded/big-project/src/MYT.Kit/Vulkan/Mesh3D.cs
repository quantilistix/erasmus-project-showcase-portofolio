

using System.Globalization;
using System.IO;

using GlmSharp;

using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

using MYT.IKit;
using MYT.IKit.Vulkan;


namespace MYT.Kit.Vulkan;


public class Mesh3D : IAsset, IDisposable
{
    public static readonly Mesh3DLoader Loader = new();

    public Mesh3D(dvec3[] vertices, ulong[] faces)
    {
        Vertices = vertices;
        Faces = faces;
    }

    public Mesh3D(dvec3[] vertices, ulong[] faces, VertexAttributes extraAttributes)
    {
        Vertices = vertices;
        Faces = faces;
        ExtraAttributes = extraAttributes;
    }

    public dvec3[] Vertices;
    public ulong[] Faces;
    public VertexAttributes? ExtraAttributes;

    public object[]? RequiredDependencies => null;
    public object[]? CreatedDependencies => null;


    private Vk? _vk;
    private Device _device;
    private Buffer _vertexBuffer;
    private DeviceMemory _vertexMemory;
    private Buffer _indexBuffer;
    private DeviceMemory _indexMemory;
    private bool _gpuAllocated;




    public unsafe void UploadToGPU(Vk vk, Device device, PhysicalDevice physicalDevice, CommandPool commandPool, Queue transferQueue)
    {
        if (_gpuAllocated)
            FreeGPUBuffers();

        _vk = vk;
        _device = device;

        // Build interleaved vertex data: x, y, z [, extra0, extra1, ...]
        int extrasPerVertex = ExtraAttributes?.Stride ?? 0;
        int floatsPerVertex = 3 + extrasPerVertex;
        int vertexCount = Vertices.Length;
        float[] vertexData = new float[vertexCount * floatsPerVertex];

        for (int i = 0; i < vertexCount; i++)
        {
            int baseIdx = i * floatsPerVertex;
            vertexData[baseIdx]     = (float)Vertices[i].x;
            vertexData[baseIdx + 1] = (float)Vertices[i].y;
            vertexData[baseIdx + 2] = (float)Vertices[i].z;

            if (ExtraAttributes != null)
            {
                int extraBase = i * extrasPerVertex;
                for (int j = 0; j < extrasPerVertex; j++)
                    vertexData[baseIdx + 3 + j] = (float)ExtraAttributes.Data[extraBase + j];
            }
        }

        uint[] indexData = new uint[Faces.Length];
        for (int i = 0; i < Faces.Length; i++)
            indexData[i] = (uint)Faces[i];

        ulong vertexSize = (ulong)(vertexData.Length * sizeof(float));
        ulong indexSize = (ulong)(indexData.Length * sizeof(uint));

        // Vertex buffer: staging → device-local
        CreateBuffer(vk, device, physicalDevice, vertexSize,
            BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer vertexStaging, out DeviceMemory vertexStagingMemory);

        void* mapped;
        vk.MapMemory(device, vertexStagingMemory, 0, vertexSize, 0, &mapped);
        fixed (float* src = vertexData)
        {
            System.Buffer.MemoryCopy(src, mapped, (long)vertexSize, (long)vertexSize);
        }
        vk.UnmapMemory(device, vertexStagingMemory);

        CreateBuffer(vk, device, physicalDevice, vertexSize,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.VertexBufferBit, MemoryPropertyFlags.DeviceLocalBit,
            out _vertexBuffer, out _vertexMemory);

        CopyBuffer(vk, device, commandPool, transferQueue, vertexStaging, _vertexBuffer, vertexSize);

        vk.DestroyBuffer(device, vertexStaging, null);
        vk.FreeMemory(device, vertexStagingMemory, null);

        // Index buffer: staging → device-local
        CreateBuffer(vk, device, physicalDevice, indexSize,
            BufferUsageFlags.TransferSrcBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer indexStaging, out DeviceMemory indexStagingMemory);

        vk.MapMemory(device, indexStagingMemory, 0, indexSize, 0, &mapped);
        fixed (uint* src = indexData)
        {
            System.Buffer.MemoryCopy(src, mapped, (long)indexSize, (long)indexSize);
        }
        vk.UnmapMemory(device, indexStagingMemory);

        CreateBuffer(vk, device, physicalDevice, indexSize,
            BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndexBufferBit, MemoryPropertyFlags.DeviceLocalBit,
            out _indexBuffer, out _indexMemory);

        CopyBuffer(vk, device, commandPool, transferQueue, indexStaging, _indexBuffer, indexSize);

        vk.DestroyBuffer(device, indexStaging, null);
        vk.FreeMemory(device, indexStagingMemory, null);

        _gpuAllocated = true;
    }


    public unsafe void DownloadFromGPU(PhysicalDevice physicalDevice, CommandPool commandPool, Queue transferQueue)
    {
        if (!_gpuAllocated || _vk == null)
            throw new InvalidOperationException("Mesh3D has no GPU buffers to download; call UploadToGPU first.");

        Vk vk = _vk;
        Device device = _device;

        int extrasPerVertex = ExtraAttributes?.Stride ?? 0;
        int floatsPerVertex = 3 + extrasPerVertex;
        int vertexCount = Vertices.Length;

        ulong vertexSize = (ulong)(vertexCount * floatsPerVertex * sizeof(float));
        ulong indexSize = (ulong)(Faces.Length * sizeof(uint));

        // Vertex buffer: device-local → staging
        CreateBuffer(vk, device, physicalDevice, vertexSize,
            BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer vertexStaging, out DeviceMemory vertexStagingMemory);

        CopyBuffer(vk, device, commandPool, transferQueue, _vertexBuffer, vertexStaging, vertexSize);

        float[] vertexData = new float[vertexCount * floatsPerVertex];
        void* mapped;
        vk.MapMemory(device, vertexStagingMemory, 0, vertexSize, 0, &mapped);
        fixed (float* dst = vertexData)
        {
            System.Buffer.MemoryCopy(mapped, dst, (long)vertexSize, (long)vertexSize);
        }
        vk.UnmapMemory(device, vertexStagingMemory);

        vk.DestroyBuffer(device, vertexStaging, null);
        vk.FreeMemory(device, vertexStagingMemory, null);

        // Index buffer: device-local → staging
        CreateBuffer(vk, device, physicalDevice, indexSize,
            BufferUsageFlags.TransferDstBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer indexStaging, out DeviceMemory indexStagingMemory);

        CopyBuffer(vk, device, commandPool, transferQueue, _indexBuffer, indexStaging, indexSize);

        uint[] indexData = new uint[Faces.Length];
        vk.MapMemory(device, indexStagingMemory, 0, indexSize, 0, &mapped);
        fixed (uint* dst = indexData)
        {
            System.Buffer.MemoryCopy(mapped, dst, (long)indexSize, (long)indexSize);
        }
        vk.UnmapMemory(device, indexStagingMemory);

        vk.DestroyBuffer(device, indexStaging, null);
        vk.FreeMemory(device, indexStagingMemory, null);

        // Unpack interleaved vertex data back into Vertices / ExtraAttributes
        for (int i = 0; i < vertexCount; i++)
        {
            int baseIdx = i * floatsPerVertex;
            Vertices[i] = new dvec3(vertexData[baseIdx], vertexData[baseIdx + 1], vertexData[baseIdx + 2]);

            if (ExtraAttributes != null)
            {
                int extraBase = i * extrasPerVertex;
                for (int j = 0; j < extrasPerVertex; j++)
                    ExtraAttributes.Data[extraBase + j] = vertexData[baseIdx + 3 + j];
            }
        }

        for (int i = 0; i < indexData.Length; i++)
            Faces[i] = indexData[i];
    }


    public unsafe void Render(CommandBuffer cmd)
    {
        if (!_gpuAllocated || _vk == null)
            return;

        ulong offset = 0;
        Buffer vertexBuffer = _vertexBuffer;
        _vk.CmdBindVertexBuffers(cmd, 0, 1, &vertexBuffer, &offset);
        _vk.CmdBindIndexBuffer(cmd, _indexBuffer, 0, IndexType.Uint32);
        _vk.CmdDrawIndexed(cmd, (uint)Faces.Length, 1, 0, 0, 0);
    }


    public unsafe void Dispose()
    {
        if (_gpuAllocated)
            FreeGPUBuffers();

        GC.SuppressFinalize(this);
    }


    private unsafe void FreeGPUBuffers()
    {
        if (_vk == null)
            return;

        _vk.DestroyBuffer(_device, _vertexBuffer, null);
        _vk.FreeMemory(_device, _vertexMemory, null);
        _vk.DestroyBuffer(_device, _indexBuffer, null);
        _vk.FreeMemory(_device, _indexMemory, null);
        _gpuAllocated = false;
    }


    private static unsafe void CreateBuffer(
        Vk vk, Device device, PhysicalDevice physicalDevice,
        ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties,
        out Buffer buffer, out DeviceMemory memory)
    {
        BufferCreateInfo bufferInfo = new()
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive
        };

        if (vk.CreateBuffer(device, &bufferInfo, null, out buffer) != Result.Success)
            throw new InvalidOperationException("Failed to create Vulkan buffer.");

        vk.GetBufferMemoryRequirements(device, buffer, out MemoryRequirements memRequirements);

        MemoryAllocateInfo allocInfo = new()
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(vk, physicalDevice, memRequirements.MemoryTypeBits, properties)
        };

        if (vk.AllocateMemory(device, &allocInfo, null, out memory) != Result.Success)
            throw new InvalidOperationException("Failed to allocate Vulkan buffer memory.");

        vk.BindBufferMemory(device, buffer, memory, 0);
    }

    private static uint FindMemoryType(Vk vk, PhysicalDevice physicalDevice, uint typeFilter, MemoryPropertyFlags properties)
    {
        vk.GetPhysicalDeviceMemoryProperties(physicalDevice, out PhysicalDeviceMemoryProperties memProperties);

        for (uint i = 0; i < memProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 && (memProperties.MemoryTypes[(int)i].PropertyFlags & properties) == properties)
                return i;
        }

        throw new InvalidOperationException("Failed to find suitable memory type.");
    }

    private static unsafe void CopyBuffer(Vk vk, Device device, CommandPool commandPool, Queue queue, Buffer src, Buffer dst, ulong size)
    {
        CommandBufferAllocateInfo allocInfo = new()
        {
            SType = StructureType.CommandBufferAllocateInfo,
            Level = CommandBufferLevel.Primary,
            CommandPool = commandPool,
            CommandBufferCount = 1
        };

        vk.AllocateCommandBuffers(device, &allocInfo, out CommandBuffer cmd);

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };

        vk.BeginCommandBuffer(cmd, &beginInfo);

        BufferCopy copyRegion = new() { Size = size };
        vk.CmdCopyBuffer(cmd, src, dst, 1, &copyRegion);

        vk.EndCommandBuffer(cmd);

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd
        };

        vk.QueueSubmit(queue, 1, &submitInfo, default);
        vk.QueueWaitIdle(queue);

        vk.FreeCommandBuffers(device, commandPool, 1, &cmd);
    }
}



// ============================================================
// Mesh3DLoader
// ============================================================

public class Mesh3DLoader : IAssetLoader<Mesh3D>
{
    internal Mesh3DLoader() { }


    public Mesh3D Load(string filename)
    {
        string extension = Path.GetExtension(filename).ToLowerInvariant();

        return extension switch
        {
            ".stl" => LoadSTL(filename),
            ".ply" => LoadPLY(filename),
            _ => throw new InvalidOperationException($"Unsupported mesh file format: {extension}")
        };
    }


    // --------------------------------------------------------
    // STL
    // --------------------------------------------------------

    private static Mesh3D LoadSTL(string filename)
    {
        byte[] fileBytes = File.ReadAllBytes(filename);

        if (IsBinarySTL(fileBytes))
            return LoadBinarySTL(fileBytes);

        return LoadAsciiSTL(filename);
    }

    private static bool IsBinarySTL(byte[] data)
    {
        if (data.Length < 84)
            return false;

        uint triangleCount = BitConverter.ToUInt32(data, 80);
        long expectedSize = 84L + triangleCount * 50L;
        return data.Length == expectedSize;
    }

    private static Mesh3D LoadBinarySTL(byte[] data)
    {
        uint triangleCount = BitConverter.ToUInt32(data, 80);

        dvec3[] vertices = new dvec3[triangleCount * 3];
        ulong[] faces = new ulong[triangleCount * 3];
        double[] normals = new double[triangleCount * 3 * 3]; // 3 components per vertex (duplicated per-face)

        int offset = 84;
        for (uint t = 0; t < triangleCount; t++)
        {
            double nx = BitConverter.ToSingle(data, offset);
            double ny = BitConverter.ToSingle(data, offset + 4);
            double nz = BitConverter.ToSingle(data, offset + 8);
            offset += 12;

            uint baseVertex = t * 3;
            for (int v = 0; v < 3; v++)
            {
                double x = BitConverter.ToSingle(data, offset);
                double y = BitConverter.ToSingle(data, offset + 4);
                double z = BitConverter.ToSingle(data, offset + 8);
                offset += 12;

                vertices[baseVertex + (uint)v] = new dvec3(x, y, z);
                faces[baseVertex + (uint)v] = baseVertex + (uint)v;

                int normalBase = (int)(baseVertex + (uint)v) * 3;
                normals[normalBase]     = nx;
                normals[normalBase + 1] = ny;
                normals[normalBase + 2] = nz;
            }

            offset += 2; // attribute byte count
        }

        VertexAttributeDescriptor[] layout = [new("nx", 1), new("ny", 1), new("nz", 1)];
        VertexAttributes extras = new(layout, normals);
        return new Mesh3D(vertices, faces, extras);
    }

    private static Mesh3D LoadAsciiSTL(string filename)
    {
        List<dvec3> vertices = [];
        List<double> normals = [];
        double nx = 0, ny = 0, nz = 0;

        foreach (string rawLine in File.ReadLines(filename))
        {
            string line = rawLine.Trim();

            if (line.StartsWith("facet normal ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                nx = double.Parse(parts[2], CultureInfo.InvariantCulture);
                ny = double.Parse(parts[3], CultureInfo.InvariantCulture);
                nz = double.Parse(parts[4], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("vertex ", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                double x = double.Parse(parts[1], CultureInfo.InvariantCulture);
                double y = double.Parse(parts[2], CultureInfo.InvariantCulture);
                double z = double.Parse(parts[3], CultureInfo.InvariantCulture);
                vertices.Add(new dvec3(x, y, z));

                normals.Add(nx);
                normals.Add(ny);
                normals.Add(nz);
            }
        }

        ulong[] faces = new ulong[vertices.Count];
        for (int i = 0; i < faces.Length; i++)
            faces[i] = (ulong)i;

        VertexAttributeDescriptor[] layout = [new("nx", 1), new("ny", 1), new("nz", 1)];
        VertexAttributes extras = new(layout, normals.ToArray());
        return new Mesh3D(vertices.ToArray(), faces, extras);
    }


    // --------------------------------------------------------
    // PLY
    // --------------------------------------------------------

    private enum PLYFormat : byte
    {
        Ascii,
        BinaryLittleEndian,
        BinaryBigEndian
    }

    private readonly struct PLYProperty
    {
        public readonly string Name;
        public readonly string Type;
        public readonly bool IsList;
        public readonly string ListCountType;
        public readonly string ListValueType;

        public PLYProperty(string name, string type)
        {
            Name = name;
            Type = type;
            IsList = false;
            ListCountType = "";
            ListValueType = "";
        }

        public PLYProperty(string name, string countType, string valueType)
        {
            Name = name;
            Type = "";
            IsList = true;
            ListCountType = countType;
            ListValueType = valueType;
        }
    }

    private readonly struct PLYHeader
    {
        public readonly PLYFormat Format;
        public readonly ulong VertexCount;
        public readonly ulong FaceCount;
        public readonly List<PLYProperty> VertexProperties;
        public readonly List<PLYProperty> FaceProperties;
        public readonly long DataOffset;

        public PLYHeader
        (
            PLYFormat format,
            ulong vertexCount,
            ulong faceCount,
            List<PLYProperty> vertexProperties,
            List<PLYProperty> faceProperties,
            long dataOffset
        )
        {
            Format = format;
            VertexCount = vertexCount;
            FaceCount = faceCount;
            VertexProperties = vertexProperties;
            FaceProperties = faceProperties;
            DataOffset = dataOffset;
        }
    }


    private static PLYHeader ParsePLYHeader(Stream stream)
    {
        PLYFormat format = PLYFormat.Ascii;
        ulong vertexCount = 0;
        ulong faceCount = 0;
        List<PLYProperty> vertexProperties = [];
        List<PLYProperty> faceProperties = [];
        bool inVertexElement = false;
        bool inFaceElement = false;

        // Read header byte-by-byte to track exact offset for binary formats.
        // The header is always ASCII text terminated by "end_header\n".
        List<byte> lineBuffer = [];
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0)
                throw new InvalidOperationException("Unexpected end of PLY file while reading header.");

            if (b == '\n')
            {
                string line = System.Text.Encoding.ASCII.GetString(lineBuffer.ToArray()).Trim();
                lineBuffer.Clear();

                if (line == "end_header")
                    break;

                if (line.StartsWith("format "))
                {
                    string fmt = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
                    format = fmt switch
                    {
                        "ascii" => PLYFormat.Ascii,
                        "binary_little_endian" => PLYFormat.BinaryLittleEndian,
                        "binary_big_endian" => PLYFormat.BinaryBigEndian,
                        _ => throw new InvalidOperationException($"Unsupported PLY format: {fmt}")
                    };
                }
                else if (line.StartsWith("element vertex "))
                {
                    vertexCount = ulong.Parse(line[15..]);
                    inVertexElement = true;
                    inFaceElement = false;
                }
                else if (line.StartsWith("element face "))
                {
                    faceCount = ulong.Parse(line[13..]);
                    inVertexElement = false;
                    inFaceElement = true;
                }
                else if (line.StartsWith("element "))
                {
                    inVertexElement = false;
                    inFaceElement = false;
                }
                else if (line.StartsWith("property list "))
                {
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string name = parts[^1];
                    string countType = parts[2];
                    string valueType = parts[3];

                    if (inFaceElement)
                        faceProperties.Add(new PLYProperty(name, countType, valueType));
                }
                else if (line.StartsWith("property "))
                {
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string type = parts[1];
                    string name = parts[2];

                    if (inVertexElement)
                        vertexProperties.Add(new PLYProperty(name, type));
                }
            }
            else if (b != '\r')
            {
                lineBuffer.Add((byte)b);
            }
        }

        return new PLYHeader(format, vertexCount, faceCount, vertexProperties, faceProperties, stream.Position);
    }


    private static void ResolveVertexLayout
    (
        List<PLYProperty> vertexProperties,
        out int xIdx, out int yIdx, out int zIdx,
        out List<int> extraIndices,
        out List<VertexAttributeDescriptor> extraLayout
    )
    {
        xIdx = -1;
        yIdx = -1;
        zIdx = -1;
        extraIndices = [];
        extraLayout = [];

        for (int i = 0; i < vertexProperties.Count; i++)
        {
            string name = vertexProperties[i].Name.ToLowerInvariant();
            if (name == "x") xIdx = i;
            else if (name == "y") yIdx = i;
            else if (name == "z") zIdx = i;
            else if (name == "nx" || name == "ny" || name == "nz")
            {
                extraIndices.Add(i);
                extraLayout.Add(new VertexAttributeDescriptor(vertexProperties[i].Name, 1));
            }
        }

        if (xIdx < 0 || yIdx < 0 || zIdx < 0)
            throw new InvalidOperationException("PLY file missing required x, y, z vertex properties.");
    }


    private static Mesh3D LoadPLY(string filename)
    {
        using FileStream stream = File.OpenRead(filename);
        PLYHeader header = ParsePLYHeader(stream);

        ResolveVertexLayout
        (
            header.VertexProperties,
            out int xIdx, out int yIdx, out int zIdx,
            out List<int> extraIndices, out List<VertexAttributeDescriptor> extraLayout
        );

        if (header.Format == PLYFormat.Ascii)
            return LoadAsciiPLY(stream, header, xIdx, yIdx, zIdx, extraIndices, extraLayout);

        bool bigEndian = header.Format == PLYFormat.BinaryBigEndian;
        return LoadBinaryPLY(stream, header, xIdx, yIdx, zIdx, extraIndices, extraLayout, bigEndian);
    }


    private static Mesh3D LoadAsciiPLY
    (
        Stream stream,
        PLYHeader header,
        int xIdx, int yIdx, int zIdx,
        List<int> extraIndices,
        List<VertexAttributeDescriptor> extraLayout
    )
    {
        using StreamReader reader = new(stream);

        dvec3[] vertices = new dvec3[header.VertexCount];
        double[] extraData = extraIndices.Count > 0
            ? new double[header.VertexCount * (ulong)extraIndices.Count]
            : [];

        for (ulong v = 0; v < header.VertexCount; v++)
        {
            string? line = reader.ReadLine();
            if (line == null)
                throw new InvalidOperationException("Unexpected end of PLY file while reading vertices.");

            string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            double x = ParsePLYValue(parts[xIdx], header.VertexProperties[xIdx].Type);
            double y = ParsePLYValue(parts[yIdx], header.VertexProperties[yIdx].Type);
            double z = ParsePLYValue(parts[zIdx], header.VertexProperties[zIdx].Type);
            vertices[v] = new dvec3(x, y, z);

            for (int e = 0; e < extraIndices.Count; e++)
            {
                int propIdx = extraIndices[e];
                extraData[v * (ulong)extraIndices.Count + (ulong)e] =
                    ParsePLYValue(parts[propIdx], header.VertexProperties[propIdx].Type);
            }
        }

        List<ulong> faces = [];
        for (ulong f = 0; f < header.FaceCount; f++)
        {
            string? line = reader.ReadLine();
            if (line == null)
                throw new InvalidOperationException("Unexpected end of PLY file while reading faces.");

            string[] parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            int count = int.Parse(parts[0], CultureInfo.InvariantCulture);
            if (count != 3)
                throw new InvalidOperationException($"Only triangular faces are supported. Face {f} has {count} vertices.");

            for (int i = 1; i <= 3; i++)
                faces.Add(ulong.Parse(parts[i], CultureInfo.InvariantCulture));
        }

        if (extraIndices.Count > 0)
        {
            VertexAttributes extras = new(extraLayout.ToArray(), extraData);
            return new Mesh3D(vertices, faces.ToArray(), extras);
        }

        return new Mesh3D(vertices, faces.ToArray());
    }


    private static Mesh3D LoadBinaryPLY
    (
        Stream stream,
        PLYHeader header,
        int xIdx, int yIdx, int zIdx,
        List<int> extraIndices,
        List<VertexAttributeDescriptor> extraLayout,
        bool bigEndian
    )
    {
        using BinaryReader reader = new(stream);

        dvec3[] vertices = new dvec3[header.VertexCount];
        double[] extraData = extraIndices.Count > 0
            ? new double[header.VertexCount * (ulong)extraIndices.Count]
            : [];

        for (ulong v = 0; v < header.VertexCount; v++)
        {
            double[] values = new double[header.VertexProperties.Count];
            for (int p = 0; p < header.VertexProperties.Count; p++)
                values[p] = ReadBinaryPLYValue(reader, header.VertexProperties[p].Type, bigEndian);

            vertices[v] = new dvec3(values[xIdx], values[yIdx], values[zIdx]);

            for (int e = 0; e < extraIndices.Count; e++)
                extraData[v * (ulong)extraIndices.Count + (ulong)e] = values[extraIndices[e]];
        }

        List<ulong> faces = [];
        for (ulong f = 0; f < header.FaceCount; f++)
        {
            PLYProperty faceProp = header.FaceProperties[0];
            int count = (int)ReadBinaryPLYValue(reader, faceProp.ListCountType, bigEndian);
            if (count != 3)
                throw new InvalidOperationException($"Only triangular faces are supported. Face {f} has {count} vertices.");

            for (int i = 0; i < 3; i++)
                faces.Add((ulong)ReadBinaryPLYValue(reader, faceProp.ListValueType, bigEndian));
        }

        if (extraIndices.Count > 0)
        {
            VertexAttributes extras = new(extraLayout.ToArray(), extraData);
            return new Mesh3D(vertices, faces.ToArray(), extras);
        }

        return new Mesh3D(vertices, faces.ToArray());
    }


    private static double ReadBinaryPLYValue(BinaryReader reader, string plyType, bool bigEndian)
    {
        // For little-endian, BinaryReader's native methods work directly.
        // For big-endian, read raw bytes and reverse before conversion.
        return plyType switch
        {
            "float" or "float32" => bigEndian
                ? BitConverter.ToSingle(ReadReversed(reader, 4))
                : reader.ReadSingle(),

            "double" or "float64" => bigEndian
                ? BitConverter.ToDouble(ReadReversed(reader, 8))
                : reader.ReadDouble(),

            "uchar" or "uint8" => reader.ReadByte(),

            "char" or "int8" => reader.ReadSByte(),

            "ushort" or "uint16" => bigEndian
                ? BitConverter.ToUInt16(ReadReversed(reader, 2))
                : reader.ReadUInt16(),

            "short" or "int16" => bigEndian
                ? BitConverter.ToInt16(ReadReversed(reader, 2))
                : reader.ReadInt16(),

            "uint" or "uint32" => bigEndian
                ? BitConverter.ToUInt32(ReadReversed(reader, 4))
                : reader.ReadUInt32(),

            "int" or "int32" => bigEndian
                ? BitConverter.ToInt32(ReadReversed(reader, 4))
                : reader.ReadInt32(),

            _ => throw new InvalidOperationException($"Unsupported PLY binary type: {plyType}")
        };
    }


    private static byte[] ReadReversed(BinaryReader reader, int count)
    {
        byte[] bytes = reader.ReadBytes(count);
        Array.Reverse(bytes);
        return bytes;
    }


    private static double ParsePLYValue(string value, string plyType)
    {
        return plyType switch
        {
            "float" or "float32" => (double)float.Parse(value, CultureInfo.InvariantCulture),
            "double" or "float64" => double.Parse(value, CultureInfo.InvariantCulture),
            "uchar" or "uint8" => byte.Parse(value, CultureInfo.InvariantCulture),
            "char" or "int8" => (double)sbyte.Parse(value, CultureInfo.InvariantCulture),
            "ushort" or "uint16" => ushort.Parse(value, CultureInfo.InvariantCulture),
            "short" or "int16" => (double)short.Parse(value, CultureInfo.InvariantCulture),
            "uint" or "uint32" => uint.Parse(value, CultureInfo.InvariantCulture),
            "int" or "int32" => (double)int.Parse(value, CultureInfo.InvariantCulture),
            _ => double.Parse(value, CultureInfo.InvariantCulture)
        };
    }
}
