using GlmSharp;

using MYT.Kit.Vulkan;

namespace VoxelGame;


/// <summary>
/// Turns cells into triangles: render meshes per chunk (only faces that can be seen, with
/// ambient occlusion) and collision meshes (every face between a block and air).
/// </summary>
internal static class VoxelMesher
{
    public const int ChunkSize = Grid3D<Voxel>.ChunkSize;
    private const int Padded = ChunkSize + 2;

    /// <summary>Cells in a chunk's copy with a border of one, as <see cref="CopyPadded"/> makes.</summary>
    public const int PaddedVolume = Padded * Padded * Padded;

    // Synchronous meshing owns this copy only until it returns. Background jobs carry
    // their own snapshots, so each thread can reuse scratch without sharing live grids.
    [ThreadStatic] private static Voxel[]? _paddedCells;

    // Geometry is exact, but removing contact samples changes gameplay load distribution.
    // Keep it opt-in until contact patches have a validated mapping to cell loads.
    public static readonly bool MergeCollisionFaces = Environment.GetEnvironmentVariable("MYT_MERGED_COLLISION") == "1"
        && Environment.GetEnvironmentVariable("MYT_UNMERGED_COLLISION") != "1";

    /// <summary>
    /// Corners of each face (in <see cref="Grid3D{T}.FaceDirections"/> order) as offsets from
    /// the cell's lowest corner: bottom-left, bottom-right, top-right, top-left as seen from
    /// outside, so counter-clockwise. Side faces have +z up; top and bottom have +y up.
    /// </summary>
    public static readonly ivec3[][] FaceCorners =
    [
        [new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1)],
        [new(0, 1, 0), new(0, 0, 0), new(0, 0, 1), new(0, 1, 1)],
        [new(1, 1, 0), new(0, 1, 0), new(0, 1, 1), new(1, 1, 1)],
        [new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1)],
        [new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1)],
        [new(1, 0, 0), new(0, 0, 0), new(0, 1, 0), new(1, 1, 0)]
    ];

    internal static bool Visible(Voxel voxel, Voxel neighbor, bool transparentLeaves) =>
        // Submerged views integrate the volume in a screen pass, so contact faces
        // against solid blocks are unnecessary and must not overlap their receivers.
        voxel.Type == BlockType.Water ? neighbor.Type == BlockType.Air :
        (voxel.Type == BlockType.Leaves && transparentLeaves) || neighbor.Type == BlockType.Air ||
        (neighbor.Type == BlockType.Leaves && transparentLeaves) ||
        (neighbor.Material.Transparent && neighbor.Type != voxel.Type);

    private static int PaddedIndex(int x, int y, int z)
    {
        return (x + 1) + Padded * ((y + 1) + Padded * (z + 1));
    }

    /// <summary>
    /// The render vertices of one chunk, four per visible face, each two uints:
    /// x | y &lt;&lt; 5 | z &lt;&lt; 10 | face &lt;&lt; 15 | ambient occlusion &lt;&lt; 18 | corner &lt;&lt; 20 | merged &lt;&lt; 22 | texture rotation &lt;&lt; 23 with positions in
    /// the chunk (0..16). Bits 25..27 add half a cell to x/y/z; bit 28 projects UVs from
    /// the owning cell instead of a whole quad. Bit 29 selects exact shape coordinates, using
    /// bits 20..22 and 25..27 for fractions. The second uint holds texture layer | cell index &lt;&lt; 8,
    /// with a door flag at bit 20 and its orientation at bits 21..22. A face is visible
    /// against air, or against a transparent block of another kind.
    /// </summary>
    /// <param name="layers">Texture layer per block type and face.</param>
    public static uint[] BuildRenderMesh(Grid3D<Voxel> grid, ivec3 chunk, int[,] layers, bool transparentLeaves = false)
    {
        // Litter commonly consists of one block. With no other occupied cell there
        // can be no hidden face or ambient occluder, including across chunk borders.
        // Avoid copying/scanning an 18-cubed padded volume for each such object.
        if (grid.Count == 1 && grid.TryGetChunk(chunk, out Grid3DChunk<Voxel> only))
        {
            ReadOnlySpan<Voxel> voxels = only.Cells;
            for (int i = 0; i < voxels.Length; i++)
            {
                if (voxels[i].Type == BlockType.Air)
                    continue;
                if (BlockShapes.HasShape(voxels[i].Type))
                {
                    Voxel[] shaped = _paddedCells ??= new Voxel[PaddedVolume];
                    CopyPadded(grid, chunk, shaped);
                    return BuildShapeRenderMesh(shaped, layers, transparentLeaves);
                }
                ivec3 cell = new(i & 15, (i >> 4) & 15, i >> 8);
                List<uint> vertices = new(48);
                Span<int> ao = stackalloc int[4];
                ao.Fill(3);
                for (int face = 0; face < 6; face++)
                {
                    int axis = face >> 1;
                    EmitRenderFace(vertices, cell, face, (uint)voxels[i].TextureLayer(layers, face), ao,
                        (axis + 1) % 3, (axis + 2) % 3, 1, 1, voxels[i].TextureRotation(face));
                }
                return [.. vertices];
            }
            return [];
        }
        Voxel[] cells = _paddedCells ??= new Voxel[PaddedVolume];
        CopyPadded(grid, chunk, cells);
        return BuildRenderMesh(cells, layers, transparentLeaves);
    }

    /// <summary>Snapshot-only singleton fast path for background debris jobs.</summary>
    internal static uint[] BuildIsolatedRenderMesh(Voxel[] cells, int[,] layers, bool transparentLeaves)
    {
        for (int z = 0; z < ChunkSize; z++)
        for (int y = 0; y < ChunkSize; y++)
        for (int x = 0; x < ChunkSize; x++)
        {
            Voxel voxel = cells[PaddedIndex(x, y, z)];
            if (voxel.Type == BlockType.Air) continue;
            if (BlockShapes.HasShape(voxel.Type)) return BuildShapeRenderMesh(cells, layers, transparentLeaves);
            List<uint> vertices = new(48);
            Span<int> ao = stackalloc int[4];
            ao.Fill(3);
            for (int face = 0; face < 6; face++)
            {
                int axis = face >> 1;
                EmitRenderFace(vertices, new(x, y, z), face, (uint)voxel.TextureLayer(layers, face), ao,
                    (axis + 1) % 3, (axis + 2) % 3, 1, 1, voxel.TextureRotation(face));
            }
            return [.. vertices];
        }
        return [];
    }

    /// <summary>Copies a chunk's cells with a border of one into <paramref name="cells"/>, for meshing it elsewhere.</summary>
    public static void CopyPadded(Grid3D<Voxel> grid, ivec3 chunk, Voxel[] cells)
    {
        grid.CopyRegion(chunk * ChunkSize - 1, new ivec3(Padded), cells);
    }

    /// <summary>
    /// The render vertices of a chunk from its copy with a border of one (<see cref="CopyPadded"/>),
    /// which needs nothing else, so it can run on any thread.
    /// </summary>
    public static uint[] BuildRenderMesh(Voxel[] cells, int[,] layers, bool transparentLeaves = false)
        => MergeRenderFaces ? BuildMergedRenderMesh(cells, layers, transparentLeaves) : BuildRenderMeshReference(cells, layers, transparentLeaves);

    private static uint[] BuildShapeRenderMesh(Voxel[] cells, int[,] layers, bool transparentLeaves = false)
    {
        List<uint> vertices = [];
        Span<int> ao = stackalloc int[4];
        foreach (var (cell, half, face, voxel, low, high) in BlockShapes.RenderFaces(cells, transparentLeaves))
        {
            for (int k = 0; k < 4; k++) ao[k] = ShapeAmbientOcclusion(cells, half, face, k);
            int first = ao[0] + ao[2] >= ao[1] + ao[3] ? 0 : 1;
            for (int k = 0; k < 4; k++)
            {
                int corner = (first + k) & 3;
                ivec3 c = FaceCorners[face][corner];
                dvec3 p = new(c.x == 0 ? low.x : high.x, c.y == 0 ? low.y : high.y, c.z == 0 ? low.z : high.z);
                uint packed = PackShapePoint(p) | (uint)(face << 15 | ao[corner] << 18 | voxel.TextureRotation(face) << 23)
                    | 1u << 28 | 1u << 29;
                vertices.Add(packed);
                uint data = (uint)voxel.TextureLayer(layers, face) | (uint)(cell.x | cell.y << 4 | cell.z << 8) << 8;
                if (BlockShapes.IsDoor(voxel.Type)) data |= 1u << 20 | ((uint)voxel.Orientation & 3u) << 21;
                vertices.Add(data);
            }
        }
        return [.. vertices];
    }

    // Shaped vertices use bits 20..22 and 25..27 for fractional coordinates:
    // 0, 1/2, 3/16, 13/16. Bit 29 distinguishes this from ordinary corner/merged data.
    private static uint PackShapePoint(dvec3 point)
    {
        uint packed = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            int whole = (int)Math.Floor(point[axis]);
            double fraction = point[axis] - whole;
            bool fine = fraction == BlockShapes.DoorThickness || fraction == 1 - BlockShapes.DoorThickness;
            packed |= (uint)whole << (axis * 5);
            if (fine) packed |= 1u << (20 + axis);
            if (fraction >= 0.5) packed |= 1u << (25 + axis);
        }
        return packed;
    }

    private static int ShapeAmbientOcclusion(Voxel[] cells, ivec3 half, int face, int corner)
    {
        ivec3 front = half + Grid3D<Voxel>.FaceDirections[face];
        ivec3 offset = FaceCorners[face][corner] * 2 - 1;
        int axis = face >> 1;
        ivec3 a = ivec3.Zero, b = ivec3.Zero;
        a[(axis + 1) % 3] = offset[(axis + 1) % 3];
        b[(axis + 2) % 3] = offset[(axis + 2) % 3];
        bool OpaqueHalf(ivec3 p)
        {
            Voxel voxel = BlockShapes.AtHalf(cells, p, out ivec3 part);
            return !voxel.Material.Transparent && !BlockShapes.IsDoor(voxel.Type) && BlockShapes.Occupied(voxel, part);
        }
        bool side1 = OpaqueHalf(front + a), side2 = OpaqueHalf(front + b), diagonal = OpaqueHalf(front + a + b);
        return side1 && side2 ? 0 : 3 - (side1 ? 1 : 0) - (side2 ? 1 : 0) - (diagonal ? 1 : 0);
    }

    // Only constant-AO faces are merged. The original triangles of every gradient stay
    // intact: matching corner values alone does not preserve piecewise affine shading.
    internal static bool MergeRenderFaces { get; set; } = Environment.GetEnvironmentVariable("MYT_RENDER_MERGE") != "0";

    [ThreadStatic] private static uint[]? _renderMask;

    internal static uint[] BuildMergedRenderMesh(Voxel[] cells, int[,] layers) => BuildMergedRenderMesh(cells, layers, false);

    internal static uint[] BuildMergedRenderMesh(Voxel[] cells, int[,] layers, bool transparentLeaves)
    {
        if (cells.Any(v => BlockShapes.HasShape(v.Type))) return BuildShapeRenderMesh(cells, layers, transparentLeaves);
        List<uint> vertices = [];
        // Each mesher thread owns its scratch space; no shared mutable mesh state and
        // no per-chunk large allocation. Scan occupancy once, including sparse objects.
        Span<uint> masks = _renderMask ??= new uint[6 * ChunkSize * ChunkSize * ChunkSize];
        masks.Clear();
        Span<bool> used = stackalloc bool[6 * ChunkSize];
        used.Clear();
        Span<int> ao = stackalloc int[4];
        for (int z = 0; z < ChunkSize; z++)
        for (int y = 0; y < ChunkSize; y++)
        for (int x = 0; x < ChunkSize; x++)
        {
            Voxel voxel = cells[PaddedIndex(x, y, z)];
            if (voxel.Type == BlockType.Air)
                continue;
            ivec3 cell = new(x, y, z);
            for (int face = 0; face < 6; face++)
            {
                ivec3 normal = Grid3D<Voxel>.FaceDirections[face];
                Voxel neighbor = cells[PaddedIndex(x + normal.x, y + normal.y, z + normal.z)];
                if (!Visible(voxel, neighbor, transparentLeaves))
                    continue;
                for (int k = 0; k < 4; k++)
                    ao[k] = AmbientOcclusion(cells, cell, face, k);
                uint layer = (uint)voxel.TextureLayer(layers, face);
                int axis = face >> 1, u = (axis + 1) % 3, v = (axis + 2) % 3;
                if (ao[0] == ao[1] && ao[0] == ao[2] && ao[0] == ao[3])
                {
                    int slice = face * ChunkSize + cell[axis];
                    masks[slice * ChunkSize * ChunkSize + cell[u] + ChunkSize * cell[v]] = 1 + layer + ((uint)ao[0] << 8) + ((uint)voxel.TextureRotation(face) << 10);
                    used[slice] = true;
                }
                else
                    EmitRenderFace(vertices, cell, face, layer, ao, u, v, 1, 1, voxel.TextureRotation(face));
            }
        }
        for (int face = 0; face < 6; face++)
        {
            int axis = face >> 1, u = (axis + 1) % 3, v = (axis + 2) % 3;
            for (int slice = 0; slice < ChunkSize; slice++)
            {
                int maskIndex = face * ChunkSize + slice;
                if (!used[maskIndex])
                    continue;
                Span<uint> mask = masks.Slice(maskIndex * ChunkSize * ChunkSize, ChunkSize * ChunkSize);
                for (int y = 0; y < ChunkSize; y++)
                for (int x = 0; x < ChunkSize; x++)
                {
                    uint key = mask[x + ChunkSize * y];
                    if (key == 0)
                        continue;
                    int width = 1, height = 1;
                    while (x + width < ChunkSize && mask[x + width + ChunkSize * y] == key)
                        width++;
                    while (y + height < ChunkSize)
                    {
                        bool same = true;
                        for (int k = 0; k < width; k++)
                            same &= mask[x + k + ChunkSize * (y + height)] == key;
                        if (!same)
                            break;
                        height++;
                    }
                    for (int row = 0; row < height; row++)
                        mask.Slice(x + ChunkSize * (y + row), width).Clear();
                    ivec3 cell = ivec3.Zero;
                    cell[axis] = slice; cell[u] = x; cell[v] = y;
                    ao.Fill((int)(((key - 1) >> 8) & 3));
                    EmitRenderFace(vertices, cell, face, (key - 1) & 255, ao, u, v, width, height, (int)(((key - 1) >> 10) & 3));
                }
            }
        }
        return [.. vertices];
    }

    private static void EmitRenderFace(List<uint> vertices, ivec3 cell, int face, uint layer,
        ReadOnlySpan<int> ao, int u, int v, int width, int height, int rotation = 0)
    {
        int first = ao[0] + ao[2] >= ao[1] + ao[3] ? 0 : 1;
        uint cellIndex = (uint)(cell.x | cell.y << 4 | cell.z << 8);
        uint merged = width > 1 || height > 1 ? 1u << 22 : 0;
        for (int k = 0; k < 4; k++)
        {
            int corner = (first + k) & 3;
            ivec3 offset = FaceCorners[face][corner];
            ivec3 p = cell + offset;
            p[u] += offset[u] * (width - 1);
            p[v] += offset[v] * (height - 1);
            vertices.Add((uint)(p.x | p.y << 5 | p.z << 10 | face << 15 | ao[corner] << 18 | corner << 20 | rotation << 23) | merged);
            vertices.Add(layer | cellIndex << 8);
        }
    }

    /// <summary>The original unit-face mesher, retained for equivalence tests and profiling.</summary>
    internal static uint[] BuildRenderMeshReference(Voxel[] cells, int[,] layers) => BuildRenderMeshReference(cells, layers, false);

    internal static uint[] BuildRenderMeshReference(Voxel[] cells, int[,] layers, bool transparentLeaves)
    {
        if (cells.Any(v => BlockShapes.HasShape(v.Type))) return BuildShapeRenderMesh(cells, layers, transparentLeaves);
        List<uint> vertices = [];
        Span<int> occlusion = stackalloc int[4];

        for (int z = 0; z < ChunkSize; z++)
        for (int y = 0; y < ChunkSize; y++)
        for (int x = 0; x < ChunkSize; x++)
        {
            Voxel voxel = cells[PaddedIndex(x, y, z)];
            if (voxel.Type == BlockType.Air)
                continue;

            for (int face = 0; face < 6; face++)
            {
                ivec3 normal = Grid3D<Voxel>.FaceDirections[face];
                Voxel neighbor = cells[PaddedIndex(x + normal.x, y + normal.y, z + normal.z)];
                bool visible = Visible(voxel, neighbor, transparentLeaves);
                if (!visible)
                    continue;

                for (int corner = 0; corner < 4; corner++)
                    occlusion[corner] = AmbientOcclusion(cells, new ivec3(x, y, z), face, corner);

                // Split the quad along the diagonal whose corners are more alike, so the
                // occlusion gradient does not crease (the shared index pattern splits 0-2).
                int first = occlusion[0] + occlusion[2] >= occlusion[1] + occlusion[3] ? 0 : 1;
                uint layer = (uint)voxel.TextureLayer(layers, face);
                uint cellIndex = (uint)(x | y << 4 | z << 8);

                for (int k = 0; k < 4; k++)
                {
                    int corner = (first + k) & 3;
                    ivec3 p = new ivec3(x, y, z) + FaceCorners[face][corner];
                    vertices.Add((uint)(p.x | p.y << 5 | p.z << 10 | face << 15 | occlusion[corner] << 18 | corner << 20 | voxel.TextureRotation(face) << 23));
                    vertices.Add(layer | cellIndex << 8);
                }
            }
        }

        return [.. vertices];
    }

    /// <summary>
    /// Light reaching a face corner, 3 (open) to 0 (tucked into a crease), from the three
    /// opaque cells in front of the face around that corner.
    /// </summary>
    internal static int AmbientOcclusion(Voxel[] cells, ivec3 cell, int face, int corner)
    {
        ivec3 normal = Grid3D<Voxel>.FaceDirections[face];
        ivec3 offset = FaceCorners[face][corner] * 2 - 1;
        ivec3 front = cell + normal;

        // The two in-plane directions toward this corner.
        ivec3 side1 = ivec3.Zero;
        ivec3 side2 = ivec3.Zero;
        int axis = face >> 1;
        side1[(axis + 1) % 3] = offset[(axis + 1) % 3];
        side2[(axis + 2) % 3] = offset[(axis + 2) % 3];

        bool a = Opaque(cells, front + side1);
        bool b = Opaque(cells, front + side2);
        bool c = Opaque(cells, front + side1 + side2);
        return a && b ? 0 : 3 - ((a ? 1 : 0) + (b ? 1 : 0) + (c ? 1 : 0));
    }

    private static bool Opaque(Voxel[] cells, ivec3 p)
    {
        Voxel voxel = cells[PaddedIndex(p.x, p.y, p.z)];
        return voxel.Type != BlockType.Air && !voxel.Material.Transparent;
    }

    /// <summary>
    /// An outward-wound collision mesh of the given chunks' cells, with shared corners and
    /// per-triangle materials. When mergeFaces is set, coplanar faces of the same block type
    /// become rectangles whose boundaries split at shared corners to preserve adjacency.
    /// Closed, except that the undersides of bedrock are left out when
    /// <paramref name="openBelowBedrock"/> is set, since nothing reaches them. Null when there
    /// is no face.
    /// </summary>
    public static (Mesh3D Mesh, SurfaceMaterial[] Surfaces)? BuildCollisionMesh(Grid3D<Voxel> grid, IEnumerable<ivec3> chunks, bool openBelowBedrock = false, bool mergeFaces = false)
    {
        CollisionMeshBuilder builder = new();
        Voxel[] cells = new Voxel[PaddedVolume];
        foreach (ivec3 chunk in chunks)
        {
            if (!grid.TryGetChunk(chunk, out _))
                continue;

            CopyPadded(grid, chunk, cells);
            builder.Add(cells, chunk, openBelowBedrock, mergeFaces);
        }

        return builder.Build();
    }

    // Shared immutable cell-resolution shapes let repeated shots/drops reuse the physics
    // cooker's per-mesh BVH and feature cache. Keep the cache bounded; arbitrary fractured
    // shapes still use the normal path. Health affects mass/stress, not surface geometry.
    private const int BoxShapeLimit = 64;
    private static readonly Dictionary<(ivec3 Min, ivec3 Size, BlockType Type), (Mesh3D Mesh, SurfaceMaterial[] Surfaces)> BoxShapes = [];
    private static readonly Queue<(ivec3 Min, ivec3 Size, BlockType Type)> BoxShapeOrder = [];

    public static (Mesh3D Mesh, SurfaceMaterial[] Surfaces) BuildRigidCollisionMesh(Grid3D<Voxel> grid, ivec3 min, ivec3 max)
    {
        ivec3 size = max - min;
        long volume = (long)size.x * size.y * size.z;
        bool box = !BlockShapes.HasShape(grid[min].Type) && volume > 0 && volume <= 512 && volume == grid.Count;
        BlockType type = grid[min].Type;
        if (box)
        {
            for (int z = min.z; z < max.z && box; z++)
            for (int y = min.y; y < max.y && box; y++)
            for (int x = min.x; x < max.x; x++)
                if (grid[new ivec3(x, y, z)].Type != type)
                {
                    box = false;
                    break;
                }
        }

        if (!box)
            return BuildCollisionMesh(grid, grid.Chunks.Select(c => c.Coordinate), mergeFaces: false)!.Value;

        var key = (min, size, type);
        lock (BoxShapes)
        {
            if (BoxShapes.TryGetValue(key, out var cached))
                return cached;
            var shape = BuildCollisionMesh(grid, grid.Chunks.Select(c => c.Coordinate), mergeFaces: false)!.Value;
            if (BoxShapes.Count == BoxShapeLimit)
                BoxShapes.Remove(BoxShapeOrder.Dequeue());
            BoxShapes.Add(key, shape);
            BoxShapeOrder.Enqueue(key);
            return shape;
        }
    }

    /// <summary>
    /// The collision mesh of one chunk from its copy with a border of one (<see cref="CopyPadded"/>),
    /// which needs nothing else, so it can run on any thread.
    /// </summary>
    public static (Mesh3D Mesh, SurfaceMaterial[] Surfaces)? BuildCollisionMesh(Voxel[] cells, ivec3 chunk, bool openBelowBedrock, bool mergeFaces = false)
    {
        CollisionMeshBuilder builder = new();
        builder.Add(cells, chunk, openBelowBedrock, mergeFaces);
        return builder.Build();
    }

    /// <summary>Gathers the faces of chunks between a block and air into one mesh, corners shared.</summary>
    private sealed class CollisionMeshBuilder
    {
        private readonly record struct Rectangle(ivec3 Cell, int Face, int Width, int Height, BlockType Type);

        private readonly List<Rectangle> _rectangles = [];
        private readonly Dictionary<ivec3, int> _corners = [];
        private readonly List<dvec3> _vertices = [];
        private readonly Dictionary<dvec3, int> _doorCorners = [];
        private readonly List<ulong> _faces = [];
        private readonly List<SurfaceMaterial> _surfaces = [];

        /// <summary>Adds a chunk's faces from its padded copy.</summary>
        public void Add(Voxel[] cells, ivec3 chunk, bool openBelowBedrock, bool mergeFaces)
        {
            if (cells.Any(v => BlockShapes.HasShape(v.Type)))
            {
                AddShapeFaces(cells, chunk, openBelowBedrock);
                return;
            }
            if (!mergeFaces)
            {
                AddUnitFaces(cells, chunk, openBelowBedrock);
                return;
            }
            ivec3 origin = chunk * ChunkSize;
            Span<BlockType> mask = stackalloc BlockType[ChunkSize * ChunkSize];
            for (int face = 0; face < 6; face++)
            {
                int axis = face >> 1;
                int u = (axis + 1) % 3;
                int v = (axis + 2) % 3;
                ivec3 normal = Grid3D<Voxel>.FaceDirections[face];
                for (int slice = 0; slice < ChunkSize; slice++)
                {
                    for (int y = 0; y < ChunkSize; y++)
                    for (int x = 0; x < ChunkSize; x++)
                    {
                        ivec3 p = ivec3.Zero;
                        p[axis] = slice;
                        p[u] = x;
                        p[v] = y;
                        BlockType type = cells[PaddedIndex(p.x, p.y, p.z)].Type;
                        ivec3 neighbor = p + normal;
                        mask[x + ChunkSize * y] = Blocks.IsSolid(type)
                            && !(openBelowBedrock && face == 5 && type == BlockType.Bedrock)
                            && !Blocks.IsSolid(cells[PaddedIndex(neighbor.x, neighbor.y, neighbor.z)].Type)
                            ? type : BlockType.Air;
                    }

                    for (int y = 0; y < ChunkSize; y++)
                    for (int x = 0; x < ChunkSize; x++)
                    {
                        BlockType type = mask[x + ChunkSize * y];
                        if (type == BlockType.Air)
                            continue;
                        int width = 1;
                        while (x + width < ChunkSize && mask[x + width + ChunkSize * y] == type)
                            width++;
                        int height = 1;
                        while (y + height < ChunkSize)
                        {
                            int k = 0;
                            while (k < width && mask[x + k + ChunkSize * (y + height)] == type)
                                k++;
                            if (k != width)
                                break;
                            height++;
                        }
                        for (int j = 0; j < height; j++)
                            mask.Slice(x + ChunkSize * (y + j), width).Clear();

                        ivec3 cell = origin;
                        cell[axis] += slice;
                        cell[u] += x;
                        cell[v] += y;
                        _rectangles.Add(new Rectangle(cell, face, width, height, type));
                    }
                }
            }
        }

        public (Mesh3D Mesh, SurfaceMaterial[] Surfaces)? Build()
        {
            if (_rectangles.Count == 0)
                return _faces.Count == 0 ? null : (new Mesh3D([.. _vertices], [.. _faces]), [.. _surfaces]);
            // All rectangle corners must be known before triangulating. A smaller rectangle
            // can terminate in the middle of another's edge, including across chunk borders.
            foreach (Rectangle rectangle in _rectangles)
            for (int k = 0; k < 4; k++)
                Corner(RectangleCorner(rectangle, k));

            Dictionary<(int Axis, int U, int V), List<int>> lines = [];
            foreach (ivec3 corner in _corners.Keys)
            for (int axis = 0; axis < 3; axis++)
            {
                var key = (axis, corner[(axis + 1) % 3], corner[(axis + 2) % 3]);
                if (!lines.TryGetValue(key, out List<int>? coordinates))
                    lines[key] = coordinates = [];
                coordinates.Add(corner[axis]);
            }
            foreach (List<int> coordinates in lines.Values)
                coordinates.Sort();

            List<int> perimeter = [];
            foreach (Rectangle rectangle in _rectangles)
            {
                perimeter.Clear();
                for (int k = 0; k < 4; k++)
                {
                    ivec3 a = RectangleCorner(rectangle, k) * 2;
                    ivec3 b = RectangleCorner(rectangle, (k + 1) % 4) * 2;
                    int axis = a.x != b.x ? 0 : a.y != b.y ? 1 : 2;
                    List<int> coordinates = lines[(axis, a[(axis + 1) % 3], a[(axis + 2) % 3])];
                    int first = coordinates.BinarySearch(a[axis]);
                    int last = coordinates.BinarySearch(b[axis]);
                    int direction = first < last ? 1 : -1;
                    for (int i = first; i != last; i += direction)
                    {
                        ivec3 p = a;
                        p[axis] = coordinates[i];
                        perimeter.Add(_corners[p]);
                    }
                }

                SurfaceMaterial surface = Blocks.Get(rectangle.Type).Surface;
                if (perimeter.Count == 4)
                {
                    Triangle(perimeter[0], perimeter[1], perimeter[2], surface);
                    Triangle(perimeter[0], perimeter[2], perimeter[3], surface);
                }
                else
                {
                    Triangulate(perimeter, rectangle.Face, surface);
                }
            }
            return _faces.Count == 0 ? null : (new Mesh3D([.. _vertices], [.. _faces]), [.. _surfaces]);
        }

        private void AddShapeFaces(Voxel[] cells, ivec3 chunk, bool openBelowBedrock)
        {
            // Reuse the shared-corner map with half-grid integer keys. A builder can contain
            // both ordinary and shaped chunks, so all keys use the same scale.
            foreach (var (cell, half, face, voxel) in BlockShapes.Faces(cells, collision: true))
            {
                if (openBelowBedrock && face == 5 && voxel.Type == BlockType.Bedrock) continue;
                ivec3 p = chunk * ChunkSize * 2 + half;
                int ShapeCorner(int corner)
                {
                    ivec3 point = p + FaceCorners[face][corner];
                    if (!BlockShapes.IsDoor(voxel.Type)) return HalfCorner(point);
                    dvec3 origin = new(chunk * ChunkSize + cell);
                    dvec3 exact = origin + BlockShapes.Point(voxel, new dvec3(point) / 2 - origin);
                    if (!_doorCorners.TryGetValue(exact, out int index))
                    {
                        index = _vertices.Count;
                        _doorCorners[exact] = index;
                        _vertices.Add(exact);
                    }
                    return index;
                }
                int a = ShapeCorner(0), b = ShapeCorner(1), c = ShapeCorner(2), d = ShapeCorner(3);
                Triangle(a, b, c, voxel.Material.Surface);
                Triangle(a, c, d, voxel.Material.Surface);
            }
        }

        private void AddUnitFaces(Voxel[] cells, ivec3 chunk, bool openBelowBedrock)
        {
            ivec3 origin = chunk * ChunkSize;
            for (int z = 0; z < ChunkSize; z++)
            for (int y = 0; y < ChunkSize; y++)
            for (int x = 0; x < ChunkSize; x++)
            {
                BlockType type = cells[PaddedIndex(x, y, z)].Type;
                if (!Blocks.IsSolid(type))
                    continue;
                for (int face = 0; face < 6; face++)
                {
                    ivec3 n = Grid3D<Voxel>.FaceDirections[face];
                    if (Blocks.IsSolid(cells[PaddedIndex(x + n.x, y + n.y, z + n.z)].Type)
                        || (openBelowBedrock && face == 5 && type == BlockType.Bedrock))
                        continue;
                    ivec3 cell = origin + new ivec3(x, y, z);
                    int a = Corner(cell + FaceCorners[face][0]);
                    int b = Corner(cell + FaceCorners[face][1]);
                    int c = Corner(cell + FaceCorners[face][2]);
                    int d = Corner(cell + FaceCorners[face][3]);
                    SurfaceMaterial surface = Blocks.Get(type).Surface;
                    Triangle(a, b, c, surface);
                    Triangle(a, c, d, surface);
                }
            }
        }

        private void Triangulate(List<int> perimeter, int face, SurfaceMaterial surface)
        {
            // Ear clipping uses n-2 triangles and keeps all collinear boundary vertices.
            // An ear cannot contain another boundary point, even on its diagonal: that
            // would recreate a T-junction or leave a degenerate final triangle.
            dvec3 normal = new(Grid3D<Voxel>.FaceDirections[face]);
            while (perimeter.Count > 3)
            {
                bool clipped = false;
                for (int i = 0; i < perimeter.Count; i++)
                {
                    int previous = (i + perimeter.Count - 1) % perimeter.Count;
                    int next = (i + 1) % perimeter.Count;
                    dvec3 a = _vertices[perimeter[previous]];
                    dvec3 b = _vertices[perimeter[i]];
                    dvec3 c = _vertices[perimeter[next]];
                    if (dvec3.Dot(dvec3.Cross(b - a, c - a), normal) <= 0)
                        continue;
                    bool containsPoint = false;
                    for (int j = 0; j < perimeter.Count; j++)
                    {
                        if (j == previous || j == i || j == next)
                            continue;
                        dvec3 p = _vertices[perimeter[j]];
                        if (dvec3.Dot(dvec3.Cross(b - a, p - a), normal) >= 0
                            && dvec3.Dot(dvec3.Cross(c - b, p - b), normal) >= 0
                            && dvec3.Dot(dvec3.Cross(a - c, p - c), normal) >= 0)
                        {
                            containsPoint = true;
                            break;
                        }
                    }
                    if (containsPoint)
                        continue;
                    Triangle(perimeter[previous], perimeter[i], perimeter[next], surface);
                    perimeter.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped)
                    throw new InvalidOperationException("Collision rectangle could not be triangulated");
            }
            Triangle(perimeter[0], perimeter[1], perimeter[2], surface);
        }

        private static ivec3 RectangleCorner(Rectangle rectangle, int corner)
        {
            ivec3 offset = FaceCorners[rectangle.Face][corner];
            int axis = rectangle.Face >> 1;
            offset[(axis + 1) % 3] *= rectangle.Width;
            offset[(axis + 2) % 3] *= rectangle.Height;
            return rectangle.Cell + offset;
        }

        private void Triangle(int a, int b, int c, SurfaceMaterial surface)
        {
            _faces.Add((ulong)a);
            _faces.Add((ulong)b);
            _faces.Add((ulong)c);
            _surfaces.Add(surface);
        }

        private int Corner(ivec3 p) => HalfCorner(p * 2);

        private int HalfCorner(ivec3 p)
        {
            if (!_corners.TryGetValue(p, out int index))
            {
                index = _vertices.Count;
                _corners.Add(p, index);
                _vertices.Add(new dvec3(p) / 2);
            }

            return index;
        }
    }
}
