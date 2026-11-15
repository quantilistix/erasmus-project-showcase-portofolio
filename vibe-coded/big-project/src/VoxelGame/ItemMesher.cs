using System.Runtime.InteropServices;

using GlmSharp;

using MYT.Kit.Vulkan;

namespace VoxelGame;


/// <summary>A vertex of an item's model: where it is and which way it faces in the model's frame, and where it samples the block texture array (u, v, layer).</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct ItemVertex(vec3 Position, vec3 Normal, vec3 UvLayer);


/// <summary>
/// Models of items, centered on their origin. A block is a unit cube with the block's faces. An
/// item that is not a block is its image a pixel thick, cut as voxels are, a cube per pixel
/// that shows, with only the faces between it and a pixel that does not: wholly transparent
/// pixels are left out, and the edges of the pixels that show are their own color. Drawn, it
/// spans a unit square in x and z, the image upright in z, a sixteenth thick in y; as a body,
/// whatever width it is given.
/// </summary>
internal static class ItemMesher
{
    private const int Size = ItemSprites.Size;
    // How far into a texel (texels) a face's corners sample, so that none picks its neighbour's.
    private const float TexelInset = 0.002f;
    private static readonly vec2[] CornerUvs = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)];

    /// <summary>A block as a unit cube, six faces with the block's textures, two triangles each.</summary>
    /// <param name="faceLayers">Texture layer per block type and face (<see cref="Renderer.FaceLayers"/>).</param>
    public static ItemVertex[] BlockModel(BlockType type, int[,] faceLayers)
    {
        List<ItemVertex> vertices = [];
        if (BlockShapes.HasShape(type))
        {
            Voxel voxel = new(type, 0);
            foreach (ivec3 part in BlockShapes.Parts(voxel))
            for (int face = 0; face < 6; face++)
            {
                ivec3 next = part + Grid3D<Voxel>.FaceDirections[face];
                if (next.x >= 0 && next.y >= 0 && next.z >= 0 && next.x < 2 && next.y < 2 && next.z < 2
                    && BlockShapes.Occupied(voxel, next)) continue;
                int f = face;
                Quad(vertices, corner => (vec3)BlockShapes.Point(voxel, new dvec3(part + VoxelMesher.FaceCorners[f][corner]) / 2) - 0.5f,
                    new vec3(Grid3D<Voxel>.FaceDirections[f]), corner =>
                    {
                        vec3 p = (vec3)BlockShapes.Point(voxel, new dvec3(part + VoxelMesher.FaceCorners[f][corner]) / 2);
                        vec2 uv = f switch { 0 => new(p.y, 1-p.z), 1 => new(1-p.y, 1-p.z),
                            2 => new(1-p.x, 1-p.z), 3 => new(p.x, 1-p.z), 4 => new(p.x, 1-p.y), _ => new(1-p.x, 1-p.y) };
                        if (BlockShapes.IsDoor(type) && f is 0 or 1 or 4 or 5)
                            uv = new(p.y, 1 - (f >= 4 ? p.x : p.z));
                        return new vec3(uv, faceLayers[(int)type, f]);
                    });
            }
            return [.. vertices];
        }
        for (int face = 0; face < 6; face++)
        {
            vec3 normal = new(Grid3D<Voxel>.FaceDirections[face]);
            int layer = faceLayers[(int)type, face];
            Quad(vertices, corner => new vec3(VoxelMesher.FaceCorners[face][corner]) - 0.5f, normal, corner => new vec3(CornerUvs[corner], layer));
        }

        return [.. vertices];
    }

    /// <summary>An image a pixel thick, a cube per pixel that shows, faces between the pixels left out.</summary>
    /// <param name="opaque">Which pixels show (<see cref="ItemSprites.Opaque"/>).</param>
    /// <param name="layer">The image's layer in the block texture array.</param>
    public static ItemVertex[] SpriteModel(bool[,] opaque, int layer)
    {
        List<ItemVertex> vertices = [];
        vec3 center = new(Size / 2f, 0.5f, Size / 2f);
        foreach ((ivec3 cell, int face) in VisibleFaces(opaque))
        {
            int x = cell.x;
            int y = Size - 1 - cell.z;
            vec3 normal = new(Grid3D<Voxel>.FaceDirections[face]);
            Quad
            (
                vertices,
                corner => (new vec3(cell + VoxelMesher.FaceCorners[face][corner]) - center) / Size,
                normal,
                corner =>
                {
                    // The front and back show the pixel; the sides, its color.
                    if (face is 2 or 3)
                    {
                        ivec3 offset = VoxelMesher.FaceCorners[face][corner];
                        vec2 inset = new(offset.x == 0 ? TexelInset : -TexelInset, offset.z == 0 ? -TexelInset : TexelInset);
                        return new vec3((new vec2(x + offset.x, y + 1 - offset.z) + inset) / Size, layer);
                    }

                    return new vec3(new vec2(x + 0.5f, y + 0.5f) / Size, layer);
                }
            );
        }

        return [.. vertices];
    }

    /// <summary>A cube of a given edge (m), centered on its origin, closed and wound outward.</summary>
    public static Mesh3D BlockShape(double edge)
    {
        return Shape([new ivec3(0, 0, 0)], new dvec3(0.5), edge);
    }

    /// <summary>A shaped block item in the same model frame as BlockModel, including its empty space.</summary>
    public static Mesh3D BlockShape(BlockType type, double edge)
    {
        if (!BlockShapes.HasShape(type)) return BlockShape(edge);
        Mesh3D shape = Shape([.. BlockShapes.Parts(new Voxel(type, 0))], new dvec3(1), edge / 2);
        if (!BlockShapes.IsDoor(type)) return shape;
        Voxel voxel = new(type, 0);
        return new Mesh3D([.. shape.Vertices.Select(p => (BlockShapes.Point(voxel, p / edge + 0.5) - 0.5) * edge)], shape.Faces);
    }

    /// <summary>
    /// The body of an item cut from its image: its pixels that show, <paramref name="width"/> (m)
    /// across the whole image, a pixel thick, centered on its origin, closed and wound outward.
    /// Where two pixels touch only at a corner, a pixel beside both is filled in, so that every
    /// edge of the surface joins exactly two faces, as the collision geometry needs.
    /// </summary>
    public static Mesh3D SpriteShape(bool[,] opaque, double width)
    {
        bool[,] solid = JoinCorners(opaque);
        List<ivec3> cells = [];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            if (solid[x, y])
                cells.Add(new ivec3(x, 0, Size - 1 - y));
        }

        return Shape(cells, new dvec3(Size / 2.0, 0.5, Size / 2.0), width / Size);
    }

    /// <summary>The faces of the image's cubes that face a pixel that does not show, or out of the image.</summary>
    private static IEnumerable<(ivec3 Cell, int Face)> VisibleFaces(bool[,] opaque)
    {
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            if (!opaque[x, y])
                continue;

            ivec3 cell = new(x, 0, Size - 1 - y);
            for (int face = 0; face < 6; face++)
            {
                ivec3 next = cell + Grid3D<Voxel>.FaceDirections[face];
                int nextY = Size - 1 - next.z;
                bool covered = next.y == 0 && next.x >= 0 && next.x < Size && nextY >= 0 && nextY < Size && opaque[next.x, nextY];
                if (!covered)
                    yield return (cell, face);
            }
        }
    }

    /// <summary>The image with a pixel filled in beside every pair that touches only at a corner.</summary>
    private static bool[,] JoinCorners(bool[,] opaque)
    {
        bool[,] solid = (bool[,])opaque.Clone();
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int y = 0; y + 1 < Size; y++)
            for (int x = 0; x + 1 < Size; x++)
            {
                bool a = solid[x, y], b = solid[x + 1, y], c = solid[x, y + 1], d = solid[x + 1, y + 1];
                if (a && d && !b && !c)
                {
                    solid[x + 1, y] = true;
                    changed = true;
                }
                else if (b && c && !a && !d)
                {
                    solid[x, y] = true;
                    changed = true;
                }
            }
        }

        return solid;
    }

    /// <summary>The faces of unit cells between a cell and no cell, corners shared, moved by <paramref name="center"/> and scaled.</summary>
    private static Mesh3D Shape(List<ivec3> cells, dvec3 center, double scale)
    {
        HashSet<ivec3> filled = [.. cells];
        Dictionary<ivec3, int> corners = [];
        List<dvec3> vertices = [];
        List<ulong> faces = [];
        Span<ulong> quad = stackalloc ulong[4];
        foreach (ivec3 cell in cells)
        {
            for (int face = 0; face < 6; face++)
            {
                if (filled.Contains(cell + Grid3D<Voxel>.FaceDirections[face]))
                    continue;

                for (int corner = 0; corner < 4; corner++)
                {
                    ivec3 at = cell + VoxelMesher.FaceCorners[face][corner];
                    if (!corners.TryGetValue(at, out int index))
                    {
                        corners[at] = index = vertices.Count;
                        vertices.Add((new dvec3(at) - center) * scale);
                    }

                    quad[corner] = (ulong)index;
                }

                faces.AddRange([quad[0], quad[1], quad[2], quad[0], quad[2], quad[3]]);
            }
        }

        return new Mesh3D([.. vertices], [.. faces]);
    }

    /// <summary>Two triangles, corners 0-1-2 and 0-2-3, counter-clockwise from outside.</summary>
    private static void Quad(List<ItemVertex> vertices, Func<int, vec3> position, vec3 normal, Func<int, vec3> uvLayer)
    {
        foreach (int corner in (int[])[0, 1, 2, 0, 2, 3])
            vertices.Add(new ItemVertex(position(corner), normal, uvLayer(corner)));
    }
}
