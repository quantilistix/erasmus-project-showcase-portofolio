using GlmSharp;

namespace VoxelGame;

/// <summary>A translated, repeating world. Coordinates in simulation stay continuous across seams.</summary>
internal static class WorldTopology
{
    public const int Width = 12000;
    public const int HalfWidth = Width / 2;
    public const int Columns = Width / 16;

    public static int Wrap(int value) => (int)(((long)value + HalfWidth) % Width + Width) % Width - HalfWidth;
    private static int ColumnAxis(int value) => (int)((((long)value + Columns / 2) % Columns + Columns) % Columns) - Columns / 2;
    public static ivec2 Column(ivec2 column) => new(ColumnAxis(column.x), ColumnAxis(column.y));

    // Sample translated copies at the seam; the interior retains the original noise.
    public static (double Coordinate, double Weight) Axis(double p)
    {
        const double blend = 512;
        p -= Width * Math.Floor((p + HalfWidth) / Width);
        if (p < -HalfWidth + blend) p += Width;
        return (p, Noise.SmoothStep(HalfWidth - blend, HalfWidth + blend, p));
    }

}
