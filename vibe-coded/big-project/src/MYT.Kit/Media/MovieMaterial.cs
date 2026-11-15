using System.Numerics;

namespace MYT.Kit.Media;

public enum MovieSurface { Plaster, Stone, Cloth, Skin, Hair, Metal, Wood, Water, Glass, Light }

/// <summary>Small repeating texture tiles; architecture uses world coordinates, moving models use local coordinates.</summary>
public static class MovieTextures
{
    private const int Size = 64;
    private static readonly float[][] Tiles = Enum.GetValues<MovieSurface>().Select(Build).ToArray();

    private static float[] Build(MovieSurface surface)
    {
        float[] tile = new float[Size * Size];
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            uint hash = unchecked((uint)(x * 374761393 + y * 668265263 + 17));
            hash = (hash ^ (hash >> 13)) * 1274126177u;
            float noise = (hash & 65535) / 65535f - .5f;
            tile[y * Size + x] = surface switch
            {
                MovieSurface.Cloth => .91f + .055f * ((x % 4 < 2 ? 1 : -1) + (y % 4 < 2 ? 1 : -1)) + noise * .06f,
                MovieSurface.Wood => .83f + .12f * MathF.Sin(x * .65f + 1.4f * MathF.Sin(y * MathF.Tau / Size)) + noise * .08f,
                MovieSurface.Metal => .90f + noise * .09f - (y % 23 == 0 ? .14f : 0),
                MovieSurface.Hair => .77f + .16f * MathF.Sin(x * 2.2f) + noise * .12f,
                MovieSurface.Skin => .97f + noise * .045f,
                MovieSurface.Plaster => .94f + noise * .09f - (y % 32 < 1 || (x + (y < 32 ? 0 : 32)) % 64 < 1 ? .08f : 0),
                MovieSurface.Stone => .91f + noise * .10f + .03f*MathF.Sin(x*.3f)*MathF.Sin(y*.4f),
                _ => .97f + noise * .04f
            };
        }
        return tile;
    }

    public static float Sample(MovieSurface surface, Vector3 p, Vector3 normal)
    {
        float u, v;
        Vector3 n = Vector3.Abs(normal);
        if (n.Z > n.X && n.Z > n.Y) { u = p.X; v = p.Y; }
        else if (n.Y > n.X) { u = p.X; v = p.Z; }
        else { u = p.Y; v = p.Z; }
        float frequency = surface is MovieSurface.Cloth or MovieSurface.Skin or MovieSurface.Hair ? 1.5f : .7f;
        int x = (int)MathF.Floor(u * Size * frequency) & (Size - 1);
        int y = (int)MathF.Floor(v * Size * frequency) & (Size - 1);
        return Tiles[(int)surface][y * Size + x];
    }
}
