using System.Numerics;

namespace ArcadeLab;

/// <summary>Two raw grayscale frames, raw PCM and Hann-windowed FFT magnitudes. No physics features.</summary>
public sealed class Sensors
{
    public const int PixelCount = ArcadeGame.Width * ArcadeGame.Height;
    public const int SpectrumSize = ArcadeGame.AudioSize / 2;
    public const int ObservationSize = PixelCount * 2 + ArcadeGame.AudioSize + SpectrumSize;
    public const int InputSize = ObservationSize + 3;
    public const int OutputSize = 16; // Three button-action Q heads, reward, twelve coarse next-frame pixels
    private readonly float[] previous = new float[PixelCount];
    private readonly Complex[] fft = new Complex[ArcadeGame.AudioSize];
    public void Reset(ArcadeGame game) => game.Pixels.CopyTo(previous, 0);
    public void Observe(ArcadeGame game, Span<float> destination)
    {
        if (destination.Length != ObservationSize) throw new ArgumentException("Observation shape.");
        game.Pixels.CopyTo(destination); previous.CopyTo(destination[PixelCount..]);
        game.Pixels.CopyTo(previous, 0); game.Audio.CopyTo(destination[(PixelCount * 2)..]);
        for (int i = 0; i < fft.Length; i++) fft[i] = game.Audio[i] * (.5 - .5 * Math.Cos(2 * Math.PI * i / (fft.Length - 1)));
        Fourier.Forward(fft);
        for (int i = 0; i < SpectrumSize; i++) destination[PixelCount * 2 + ArcadeGame.AudioSize + i] = (float)fft[i].Magnitude / fft.Length;
    }
    public static void Input(ReadOnlySpan<float> observation, int action, Span<float> destination)
    {
        observation.CopyTo(destination); destination[ObservationSize..].Clear(); destination[ObservationSize + action] = 1;
    }
    public static void CoarseFrame(ReadOnlySpan<float> observation, Span<float> destination)
    {
        destination.Clear();
        for (int y = 0; y < ArcadeGame.Height; y++) for (int x = 0; x < ArcadeGame.Width; x++)
            destination[(y / 6) * 4 + x / 6] += observation[y * ArcadeGame.Width + x] / 36;
    }
    public static Buttons Control(int action) => action switch { 1 => Buttons.Left, 2 => Buttons.Right, _ => Buttons.None };
}

public static class Fourier
{
    /// <summary>In-place radix-2 forward FFT. Unnormalized; no allocations.</summary>
    public static void Forward(Span<Complex> values)
    {
        int n = values.Length; if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("FFT length must be a power of two.");
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1; for (; (j & bit) != 0; bit >>= 1) j ^= bit; j ^= bit;
            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }
        for (int length = 2; length <= n; length <<= 1)
        {
            var root = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (int start = 0; start < n; start += length)
            {
                Complex w = 1;
                for (int j = 0; j < length / 2; j++)
                {
                    var u = values[start + j]; var v = values[start + j + length / 2] * w;
                    values[start + j] = u + v; values[start + j + length / 2] = u - v; w *= root;
                }
            }
        }
    }
}
