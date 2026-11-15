namespace VoxelGame;


/// <summary>
/// Seeded hashing and 2D gradient noise for the terrain: the same seed and coordinates give
/// the same values on every thread and in every run, so any part of the world can be
/// generated on its own, in any order, and match its neighbours.
/// </summary>
internal static class Noise
{
    private const int Directions = 16;

    // Unit gradients spread evenly around the circle, so the noise has no favoured axis.
    private static readonly (double X, double Y)[] Gradients =
    [
        .. Enumerable.Range(0, Directions).Select(k => (Math.Cos(2.0 * Math.PI * k / Directions), Math.Sin(2.0 * Math.PI * k / Directions)))
    ];

    /// <summary>A well-mixed 32-bit hash of a lattice point and a seed.</summary>
    public static uint Hash(int x, int y, int seed)
    {
        uint h = Mix((uint)seed ^ 0x9E3779B9u);
        h = Mix(h ^ (uint)x * 0x85EBCA6Bu);
        return Mix(h ^ (uint)y * 0xC2B2AE35u);
    }

    /// <summary>A value in [0, 1) hashed from a lattice point and a seed.</summary>
    public static double Unit(int x, int y, int seed)
    {
        return (Hash(x, y, seed) >> 8) / (double)(1 << 24);
    }

    /// <summary>Perlin's gradient noise, from about −1 to 1, varying over about one unit.</summary>
    public static double Gradient(double x, double y, int seed)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        double fx = x - x0;
        double fy = y - y0;

        double n00 = Corner(x0, y0, fx, fy, seed);
        double n10 = Corner(x0 + 1, y0, fx - 1.0, fy, seed);
        double n01 = Corner(x0, y0 + 1, fx, fy - 1.0, seed);
        double n11 = Corner(x0 + 1, y0 + 1, fx - 1.0, fy - 1.0, seed);

        double u = Fade(fx);
        double v = Fade(fy);
        double bottom = n00 + u * (n10 - n00);
        double top = n01 + u * (n11 - n01);
        return Math.Sqrt(2.0) * (bottom + v * (top - bottom));
    }

    /// <summary>
    /// Fractal noise: octaves of <see cref="Gradient"/>, each twice as fine and half as strong
    /// as the one before, normalized to about −1 to 1. <paramref name="scale"/> is the size of
    /// the coarsest features, in the units of x and y.
    /// </summary>
    public static double Fractal(double x, double y, double scale, int octaves, int seed)
    {
        var a = WorldTopology.Axis(x); var b = WorldTopology.Axis(y);
        double low = FractalRaw(a.Coordinate, b.Coordinate, scale, octaves, seed);
        if (a.Weight > 0) low += a.Weight * (FractalRaw(a.Coordinate - WorldTopology.Width, b.Coordinate, scale, octaves, seed) - low);
        if (b.Weight == 0) return low;
        double high = FractalRaw(a.Coordinate, b.Coordinate - WorldTopology.Width, scale, octaves, seed);
        if (a.Weight > 0) high += a.Weight * (FractalRaw(a.Coordinate - WorldTopology.Width, b.Coordinate - WorldTopology.Width, scale, octaves, seed) - high);
        return low + b.Weight * (high - low);
    }

    private static double FractalRaw(double x, double y, double scale, int octaves, int seed)
    {
        double sum = 0.0;
        double amplitude = 1.0;
        double total = 0.0;
        double frequency = 1.0 / scale;
        for (int octave = 0; octave < octaves; octave++)
        {
            sum += amplitude * Gradient(x * frequency, y * frequency, seed + 7919 * octave);
            total += amplitude;
            amplitude *= 0.5;
            frequency *= 2.0;
        }

        return sum / total;
    }

    /// <summary>
    /// Ridged fractal noise, from 0 to 1: high along the lines where the noise crosses zero, so
    /// it draws sharp crests with valleys between, as mountain ranges have.
    /// </summary>
    public static double Ridged(double x, double y, double scale, int octaves, int seed)
    {
        var a = WorldTopology.Axis(x); var b = WorldTopology.Axis(y);
        double low = RidgedRaw(a.Coordinate, b.Coordinate, scale, octaves, seed);
        if (a.Weight > 0) low += a.Weight * (RidgedRaw(a.Coordinate - WorldTopology.Width, b.Coordinate, scale, octaves, seed) - low);
        if (b.Weight == 0) return low;
        double high = RidgedRaw(a.Coordinate, b.Coordinate - WorldTopology.Width, scale, octaves, seed);
        if (a.Weight > 0) high += a.Weight * (RidgedRaw(a.Coordinate - WorldTopology.Width, b.Coordinate - WorldTopology.Width, scale, octaves, seed) - high);
        return low + b.Weight * (high - low);
    }

    private static double RidgedRaw(double x, double y, double scale, int octaves, int seed)
    {
        double sum = 0.0;
        double amplitude = 1.0;
        double total = 0.0;
        double frequency = 1.0 / scale;
        for (int octave = 0; octave < octaves; octave++)
        {
            double ridge = 1.0 - Math.Abs(Gradient(x * frequency, y * frequency, seed + 104729 * octave));
            sum += amplitude * ridge * ridge;
            total += amplitude;
            amplitude *= 0.5;
            frequency *= 2.0;
        }

        return sum / total;
    }

    public static double SmoothStep(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>Smooth seeded 3D value noise, continuous across chunk boundaries.</summary>
    public static double Volume(double x, double y, double z, int seed)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y), iz = (int)Math.Floor(z);
        double u = Fade(x - ix), v = Fade(y - iy), w = Fade(z - iz);
        double Sample(int dx, int dy, int dz) => 2 * Unit(ix + dx, iy + dy, seed ^ ((iz + dz) * 7919)) - 1;
        double Lerp(double a, double b, double t) => a + (b - a) * t;
        return Lerp(Lerp(Lerp(Sample(0, 0, 0), Sample(1, 0, 0), u), Lerp(Sample(0, 1, 0), Sample(1, 1, 0), u), v),
            Lerp(Lerp(Sample(0, 0, 1), Sample(1, 0, 1), u), Lerp(Sample(0, 1, 1), Sample(1, 1, 1), u), v), w);
    }

    private static double Corner(int x, int y, double dx, double dy, int seed)
    {
        (double gx, double gy) = Gradients[Hash(x, y, seed) & (Directions - 1)];
        return gx * dx + gy * dy;
    }

    private static double Fade(double t)
    {
        return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
    }

    // The finalizer of MurmurHash3: every input bit flips each output bit with odds near ½.
    private static uint Mix(uint h)
    {
        h ^= h >> 16;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2AE35u;
        h ^= h >> 16;
        return h;
    }
}
