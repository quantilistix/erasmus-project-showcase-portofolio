using System.Numerics;
using MYT.Kit.Vulkan;

namespace MYT.Kit.Media;

public readonly record struct MovieFace(Vector3 A, Vector3 B, Vector3 C, Vector3 D, Vector3 Normal, uint Color, MovieSurface Surface = MovieSurface.Plaster);

/// <summary>
/// Display-independent orthographic rasterizer for textured sets and articulated low polygon models.
/// Uses smooth normals, material shaders, a shadow map and contact shading. Draw calls are resolved by Finish. All time is supplied by the caller.
/// </summary>
public sealed class OfflineVoxelRenderer
{
    private readonly float[] _depth;
    private Vector3 _eye, _right, _up, _forward;
    private float _scale;
    private Vector3 _sky;
    private readonly List<ShadedTriangle> _triangles = [];
    private readonly float[] _shadow = new float[384 * 384];
    private readonly Vector3[] _colors;
    private bool _night;
    private double _time;
    private static readonly Vector3 Light = Vector3.Normalize(new(-.5f, -.7f, 1));
    private static readonly Vector3 ShadowRight = Vector3.Normalize(Vector3.Cross(-Light, Vector3.UnitZ));
    private static readonly Vector3 ShadowUp = Vector3.Cross(ShadowRight, -Light);
    private readonly record struct Vertex(Vector3 Position, Vector3 Normal, Vector3 Texture, Vector3 TextureNormal);
    private readonly record struct ShadedTriangle(Vertex A, Vertex B, Vertex C, uint Color, MovieSurface Surface);
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    private static readonly Vector3[] Corners =
    [new(0,0,0), new(1,0,0), new(1,1,0), new(0,1,0), new(0,0,1), new(1,0,1), new(1,1,1), new(0,1,1)];
    private static readonly int[][] Faces = [[1,2,6,5], [3,0,4,7], [2,3,7,6], [0,1,5,4], [4,5,6,7], [3,2,1,0]];
    private static readonly Vector3[] Normals = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];

    public OfflineVoxelRenderer(int width, int height)
    {
        if (width < 2 || height < 2) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width; Height = height;
        _colors = new Vector3[checked(width * height)];
        Pixels = new byte[checked(width * height * 4)]; _depth = new float[checked(width * height)];
    }

    public void Begin(Vector3 eye, Vector3 target, float viewWidth, uint sky)
    {
        _eye = eye; _forward = Vector3.Normalize(target - eye);
        _right = Vector3.Normalize(Vector3.Cross(_forward, Vector3.UnitZ));
        _up = Vector3.Cross(_right, _forward); _scale = Width / viewWidth;
        _sky = Rgb(sky); _night = _sky.Length() < .4f;
        _triangles.Clear();
        Array.Fill(_depth, float.PositiveInfinity);
        for (int y = 0; y < Height; y++)
        {
            Vector3 color = _sky * (1.08f - .28f * y / Height);
            for (int x = 0; x < Width; x++) { Put(y * Width + x, color); _colors[y * Width + x] = color; }
        }
    }

    public static MovieFace[] Mesh(Grid3D<uint> grid)
    {
        List<MovieFace> mesh = [];
        foreach (var (cell, color) in grid.Occupied())
        {
            Vector3 origin = new(cell.x, cell.y, cell.z);
            for (int side = 0; side < 6; side++)
            {
                if (grid[cell + Grid3D<uint>.FaceDirections[side]] != grid.Empty) continue;
                int[] face = Faces[side];
                mesh.Add(new(origin + Corners[face[0]], origin + Corners[face[1]],
                    origin + Corners[face[2]], origin + Corners[face[3]], Normals[side], color));
            }
        }
        return [.. mesh];
    }

    public void Draw(ReadOnlySpan<MovieFace> mesh)
    {
        foreach (MovieFace face in mesh) Face(face, face.Surface);
    }

    public void Box(Vector3 center, Vector3 size, uint color, float pitch = 0, float yaw = 0, MovieSurface surface = MovieSurface.Plaster)
    {
        Matrix4x4 rotation = Matrix4x4.CreateRotationX(pitch) * Matrix4x4.CreateRotationZ(yaw);
        Span<Vector3> points = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++) points[i] = center + Vector3.Transform((Corners[i] - new Vector3(.5f)) * size, rotation);
        for (int side = 0; side < 6; side++)
        {
            int[] f = Faces[side];
            Face(new(points[f[0]], points[f[1]], points[f[2]], points[f[3]], Vector3.TransformNormal(Normals[side], rotation), color), surface);
        }
    }

    private Vector3 Project(Vector3 p)
    {
        Vector3 delta = p - _eye;
        return new(Width * .5f + Vector3.Dot(delta, _right) * _scale,
            Height * .48f - Vector3.Dot(delta, _up) * _scale, Vector3.Dot(delta, _forward));
    }

    private void Face(MovieFace face, MovieSurface surface = MovieSurface.Plaster)
    {
        Vertex a = new(face.A, face.Normal, face.A, face.Normal), b = new(face.B, face.Normal, face.B, face.Normal),
            c = new(face.C, face.Normal, face.C, face.Normal), d = new(face.D, face.Normal, face.D, face.Normal);
        _triangles.Add(new(a,b,c,face.Color,surface));
        _triangles.Add(new(a,c,d,face.Color,surface));
    }

    /// <summary>Low polygon ellipsoid with smooth vertex normals; anatomy and curved props.</summary>
    public void Ellipsoid(Vector3 center, Vector3 radii, uint color, MovieSurface surface = MovieSurface.Cloth,
        Quaternion rotation = default, int segments = 12, int rings = 8)
    {
        if (rotation == default) rotation = Quaternion.Identity;
        Vertex At(int x, int y)
        {
            float latitude = MathF.PI * y / rings, longitude = MathF.Tau * x / segments;
            Vector3 unit = new(MathF.Sin(latitude) * MathF.Cos(longitude), MathF.Sin(latitude) * MathF.Sin(longitude), MathF.Cos(latitude));
            return new(center + Vector3.Transform(unit * radii, rotation),
                Vector3.Normalize(Vector3.Transform(unit / radii, rotation)), unit*radii, Vector3.Normalize(unit/radii));
        }
        for (int y = 0; y < rings; y++)
        for (int x = 0; x < segments; x++)
        {
            Vertex a = At(x,y), b = At(x+1,y), c = At(x+1,y+1), d = At(x,y+1);
            if (y > 0) _triangles.Add(new(a,c,b,color,surface));
            if (y < rings-1) _triangles.Add(new(a,d,c,color,surface));
        }
    }

    /// <summary>Tailored cross sections for torsos, hips and hulls, rather than stacked spheres.</summary>
    public void Profile(Vector3 origin, ReadOnlySpan<Vector3> sections, uint color, MovieSurface surface, Quaternion rotation = default)
    {
        if (rotation == default) rotation = Quaternion.Identity;
        const int sides = 12;
        for (int ring=0; ring<sections.Length-1; ring++)
        for (int side=0; side<sides; side++)
        {
            Vector3 lower=sections[ring], upper=sections[ring+1];
            float slope=(lower.X-upper.X)/Math.Max(.001f,upper.Z-lower.Z);
            Vertex At(int r, int j)
            {
                // Local copy avoids capturing a span in the vertex helper.
                Vector3 section = r == 0 ? lower : upper;
                float angle = MathF.Tau*j/sides;
                Vector3 n = Vector3.Normalize(new Vector3(MathF.Cos(angle)/section.X,MathF.Sin(angle)/section.Y,slope));
                Vector3 p = new(section.X*MathF.Cos(angle),section.Y*MathF.Sin(angle),section.Z);
                return new(origin+Vector3.Transform(p,rotation),Vector3.Transform(n,rotation),p,n);
            }
            Vertex a=At(0,side), b=At(0,side+1), c=At(1,side+1), d=At(1,side);
            _triangles.Add(new(a,b,c,color,surface)); _triangles.Add(new(a,c,d,color,surface));
        }
    }

    public void Limb(Vector3 start, Vector3 end, float radius, uint color, MovieSurface surface = MovieSurface.Cloth)
    {
        Vector3 direction = Vector3.Normalize(end - start);
        Quaternion rotation = direction.Z < -.999f ? Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI)
            : Quaternion.Normalize(new Quaternion(Vector3.Cross(Vector3.UnitZ, direction), 1 + direction.Z));
        float length = Vector3.Distance(start,end);
        // Tapered sleeves and trouser legs meet at actual joints; no bead-like limb pieces.
        Profile(start,new Vector3[] {
            new(radius*.85f,radius*.85f,-radius*.35f),
            new(radius,radius,length*.20f),
            new(radius*.82f,radius*.82f,length*.72f),
            new(radius*.70f,radius*.70f,length+radius*.25f)
        },color,surface,rotation);
        Ellipsoid(end,new(radius*.72f),color,surface,segments:8,rings:4);
    }

    private static Vector3 ShadowProject(Vector3 p) => new(192 + Vector3.Dot(p, ShadowRight) * 6,
        192 - Vector3.Dot(p, ShadowUp) * 6, -Vector3.Dot(p, Light));

    private void Raster(ShadedTriangle triangle, bool shadow)
    {
        Vertex va = triangle.A, vb = triangle.B, vc = triangle.C;
        if (!shadow && Vector3.Dot(Vector3.Cross(vb.Position-va.Position, vc.Position-va.Position), _forward) >= 0) return;
        Vector3 a = shadow ? ShadowProject(va.Position) : Project(va.Position);
        Vector3 b = shadow ? ShadowProject(vb.Position) : Project(vb.Position);
        Vector3 c = shadow ? ShadowProject(vc.Position) : Project(vc.Position);
        if (!shadow && (a.Z <= 0 || b.Z <= 0 || c.Z <= 0)) return;
        float area = Edge(a,b,c.X,c.Y);
        if (MathF.Abs(area) < .001f) return;
        int width = shadow ? 384 : Width, height = shadow ? 384 : Height;
        int minX = Math.Max(0, (int)MathF.Floor(Math.Min(a.X, Math.Min(b.X,c.X))));
        int maxX = Math.Min(width-1, (int)MathF.Ceiling(Math.Max(a.X, Math.Max(b.X,c.X))));
        int minY = Math.Max(0, (int)MathF.Floor(Math.Min(a.Y, Math.Min(b.Y,c.Y))));
        int maxY = Math.Min(height-1, (int)MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y,c.Y))));
        Vector3 baseColor = Rgb(triangle.Color);
        Vector3 planeNormal = Vector3.Cross(vb.Position-va.Position,vc.Position-va.Position);
        float denominator = Vector3.Dot(planeNormal,Light);
        float shadowDx = MathF.Abs(denominator) > .001f ? Vector3.Dot(planeNormal,ShadowRight)/(6*denominator) : 0;
        float shadowDy = MathF.Abs(denominator) > .001f ? -Vector3.Dot(planeNormal,ShadowUp)/(6*denominator) : 0;
        shadowDx=Math.Clamp(shadowDx,-1,1); shadowDy=Math.Clamp(shadowDy,-1,1);
        float inverse = 1 / area;
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            float u = Edge(b,c,x+.5f,y+.5f) * inverse, v = Edge(c,a,x+.5f,y+.5f) * inverse, w = 1-u-v;
            if (u < -.00001f || v < -.00001f || w < -.00001f) continue;
            float z = u*a.Z + v*b.Z + w*c.Z;
            int index = y*width+x;
            if (shadow) { if (z < _shadow[index]) _shadow[index] = z; continue; }
            if (z >= _depth[index]) continue;
            _depth[index] = z;
            Vector3 p = va.Position*u + vb.Position*v + vc.Position*w;
            Vector3 normal = Vector3.Normalize(va.Normal*u + vb.Normal*v + vc.Normal*w);
            if (triangle.Surface == MovieSurface.Water)
                normal = Vector3.Normalize(normal + new Vector3(.09f*MathF.Sin(p.X*2+(float)_time), .07f*MathF.Cos(p.Y*3+(float)_time*1.4f), 0));
            float diffuse = Math.Max(0, Vector3.Dot(normal, Light));
            Vector3 sp = ShadowProject(p);
            float visibility = 1;
            int sx = (int)sp.X, sy = (int)sp.Y;
            if (sx > 0 && sy > 0 && sx < 383 && sy < 383)
            {
                float bias = .065f + .09f*(1-diffuse);
                float lit = 0;
                for (int oy=0;oy<2;oy++) for (int ox=0;ox<2;ox++)
                {
                    // Compare at the receiver plane's depth at each filter tap, avoiding striped self shadows.
                    float receiverDepth=sp.Z + shadowDx*(sx+ox+.5f-sp.X) + shadowDy*(sy+oy+.5f-sp.Y);
                    lit += receiverDepth-bias <= _shadow[(sy+oy)*384+sx+ox] ? 1 : 0;
                }
                visibility = .25f + .75f*lit/4;
            }
            float texture = MovieTextures.Sample(triangle.Surface, va.Texture*u+vb.Texture*v+vc.Texture*w,
                va.TextureNormal*u+vb.TextureNormal*v+vc.TextureNormal*w);
            Vector3 ambient = new Vector3(.30f,.34f,.40f) * (.8f+.2f*normal.Z);
            Vector3 key = _night ? new(.32f,.43f,.62f) : new(.85f,.76f,.61f);
            Vector3 color = baseColor * texture * (ambient + key * diffuse * visibility);
            Vector3 view = Vector3.Normalize(_eye-p);
            float spec = MathF.Pow(Math.Max(0,Vector3.Dot(normal,Vector3.Normalize(Light+view))),
                triangle.Surface is MovieSurface.Metal or MovieSurface.Glass or MovieSurface.Water ? 48 : 12);
            float strength = triangle.Surface switch { MovieSurface.Metal => .55f, MovieSurface.Water => .65f, MovieSurface.Glass => .5f, MovieSurface.Skin => .09f, _ => .025f };
            color += key * (spec * strength * visibility);
            if (triangle.Surface == MovieSurface.Light) color = baseColor * 1.35f;
            float fog = Math.Clamp((z-30)/150,0,.5f);
            _colors[index] = Vector3.Lerp(color,_sky,fog);
        }
    }

    public void Finish(float exposure = 1, bool rain = false, double time = 0)
    {
        _time = time;
        Array.Fill(_shadow, float.PositiveInfinity);
        foreach (var triangle in _triangles)
            if (triangle.Surface != MovieSurface.Water && triangle.Surface != MovieSurface.Light) Raster(triangle, true);
        foreach (var triangle in _triangles) Raster(triangle, false);
        // A small depth based contact term anchors feet, furniture and wall junctions.
        for (int y=0;y<Height;y++)
        for (int x=0;x<Width;x++)
        {
            int index=y*Width+x;
            float occlusion=0;
            if (float.IsFinite(_depth[index]))
            {
                int radius=Math.Max(1,Height/180);
                foreach (var offset in ContactOffsets)
                {
                    int sx=x+offset.X*radius, sy=y+offset.Y*radius;
                    if (sx<0 || sy<0 || sx>=Width || sy>=Height) continue;
                    float delta=_depth[index]-_depth[sy*Width+sx];
                    if (delta>.08f && delta<1.2f) occlusion+=.045f;
                }
            }
            Vector3 color=_colors[index]*(1-occlusion);
            // Gentle highlight compression preserves the painted, late-1990s render palette.
            color = color / (Vector3.One + color * .22f) * 1.13f;
            Put(index,color);
        }
        if (rain)
        {
            for (int i = 0; i < 150; i++)
            {
                int x = (int)((i * 73.37 + time * 90) % Width);
                int y = (int)((i * 137.79 + time * 340) % Height);
                for (int j = 0; j < 5; j++)
                    if (x + j / 2 < Width && y + j < Height) Put((y + j) * Width + x + j / 2, new(.5f,.64f,.69f));
            }
        }
        int bar = Height / 16;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            int p = (y * Width + x) * 4;
            float dx = x / (float)Width - .5f, dy = y / (float)Height - .5f;
            float shade = exposure * (1 - .55f * (dx * dx + dy * dy));
            if (y < bar || y >= Height - bar) shade = 0;
            Pixels[p] = (byte)Math.Clamp(Pixels[p] * shade, 0, 255);
            Pixels[p+1] = (byte)Math.Clamp(Pixels[p+1] * shade, 0, 255);
            Pixels[p+2] = (byte)Math.Clamp(Pixels[p+2] * shade, 0, 255);
        }
    }

    private static readonly (int X,int Y)[] ContactOffsets = [(-1,0),(1,0),(0,-1),(0,1),(-2,-1),(2,1)];

    private static float Edge(Vector3 a, Vector3 b, float x, float y) => (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);
    private static Vector3 Rgb(uint color) => new((color >> 16 & 255) / 255f, (color >> 8 & 255) / 255f, (color & 255) / 255f);
    private void Put(int index, Vector3 color)
    {
        int p = index * 4;
        Pixels[p] = (byte)Math.Clamp(color.X * 255, 0, 255);
        Pixels[p+1] = (byte)Math.Clamp(color.Y * 255, 0, 255);
        Pixels[p+2] = (byte)Math.Clamp(color.Z * 255, 0, 255); Pixels[p+3] = 255;
    }
}
