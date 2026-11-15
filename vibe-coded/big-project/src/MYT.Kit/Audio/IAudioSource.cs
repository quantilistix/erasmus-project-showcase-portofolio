namespace MYT.Kit.Audio;

/// <summary>A deterministic stereo source evaluated on the movie clock, in seconds.</summary>
public interface IAudioSource
{
    (double Left, double Right) Sample(double time);
}

public enum SoundKind { Wind, Water, Rain, Hum, Radio, Beep, Impact, Pump, Bell }

/// <summary>A scheduled non-voice sound. Randomness is derived from sample time, never wall time.</summary>
public sealed record SoundCue(SoundKind Kind, double Start, double Duration, double Gain = .2,
    double Pan = 0, double Pitch = 1) : IAudioSource
{
    public (double Left, double Right) Sample(double time)
    {
        double t = time - Start;
        if (t < 0 || t >= Duration) return default;
        double envelope = Math.Min(1, t / .04) * Math.Min(1, (Duration - t) / .2);
        double noise = Noise(t, 12000), slow = Noise(t, 500);
        double tone = Math.Sin(2 * Math.PI * t * 220 * Pitch);
        double value = Kind switch
        {
            SoundKind.Wind => slow * (.4 + .2 * Math.Sin(t * .7)) + noise * .06,
            SoundKind.Water => slow * (.45 + .3 * Math.Sin(t * 1.9)) + noise * .18,
            SoundKind.Rain => noise * .6 + slow * .15,
            SoundKind.Hum => tone * .35 + Math.Sin(t * 2 * Math.PI * 60) * .2,
            SoundKind.Radio => noise * (.2 + .6 * Math.Pow(Math.Sin(t * 7), 8)),
            SoundKind.Beep => Math.Sin(2 * Math.PI * 880 * Pitch * t) * Math.Exp(-t * 9),
            SoundKind.Impact => (noise + Math.Sin(t * 170)) * Math.Exp(-t * 7),
            SoundKind.Pump => slow * .3 + Math.Sin(t * 2 * Math.PI * 90) * Math.Pow(Math.Sin(t * 5), 4),
            SoundKind.Bell => (tone + .4 * Math.Sin(t * 2 * Math.PI * 553 * Pitch)) * Math.Exp(-t * 2),
            _ => 0
        };
        value *= Gain * envelope;
        double pan = Math.Clamp(Pan, -1, 1);
        return (value * Math.Sqrt((1 - pan) / 2), value * Math.Sqrt((1 + pan) / 2));
    }

    private static double Noise(double time, int rate)
    {
        // Integer hash: stable across runs and platforms, with no mutable generator state.
        uint n = unchecked((uint)(time * rate) + 0x9e3779b9u);
        n ^= n >> 16; n *= 0x7feb352du; n ^= n >> 15; n *= 0x846ca68bu; n ^= n >> 16;
        return n / (double)uint.MaxValue * 2 - 1;
    }
}

public sealed class AudioMix : IAudioSource
{
    public List<IAudioSource> Sources { get; } = [];
    /// <summary>Freezes this mix into one-second buckets, so long films visit only active cues.</summary>
    public IAudioSource Compile(double duration)
    {
        if (!double.IsFinite(duration) || duration <= 0 || duration > int.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(duration));
        IAudioSource[][] buckets = new IAudioSource[(int)Math.Ceiling(duration)][];
        for (int second = 0; second < buckets.Length; second++)
            buckets[second] = Sources.Where(source => source is not SoundCue cue || cue.Start < second + 1 && cue.Start + cue.Duration > second).ToArray();
        return new CompiledMix(buckets);
    }
    public (double Left, double Right) Sample(double time)
    {
        double left = 0, right = 0;
        foreach (IAudioSource source in Sources)
        {
            var sample = source.Sample(time);
            left += sample.Left; right += sample.Right;
        }
        return (Math.Tanh(left), Math.Tanh(right));
    }

    private sealed class CompiledMix(IAudioSource[][] buckets) : IAudioSource
    {
        public (double Left, double Right) Sample(double time)
        {
            if (time < 0 || time >= buckets.Length) return default;
            double left = 0, right = 0;
            foreach (IAudioSource source in buckets[(int)time])
            {
                var sample = source.Sample(time); left += sample.Left; right += sample.Right;
            }
            return (Math.Tanh(left), Math.Tanh(right));
        }
    }
}

/// <summary>Bounded-memory PCM output, usable by FFmpeg or a realtime audio backend.</summary>
public static class WaveAudio
{
    public static void Write(string path, IAudioSource source, double duration, int sampleRate = 48000,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(duration) || duration <= 0 || sampleRate < 8000 || sampleRate > 192000)
            throw new ArgumentOutOfRangeException(nameof(duration));
        long samples = (long)Math.Ceiling(duration * sampleRate);
        long bytes = checked(samples * 4);
        if (bytes > uint.MaxValue - 36) throw new ArgumentOutOfRangeException(nameof(duration), "PCM exceeds RIFF size limit.");
        using BinaryWriter writer = new(File.Create(path));
        writer.Write("RIFF"u8); writer.Write((uint)(36 + bytes)); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(sampleRate);
        writer.Write(sampleRate * 4); writer.Write((short)4); writer.Write((short)16);
        writer.Write("data"u8); writer.Write((uint)bytes);
        byte[] block = new byte[4096 * 4];
        for (long start = 0; start < samples; start += 4096)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(4096, samples - start);
            for (int i = 0; i < count; i++)
            {
                var (left, right) = source.Sample((start + i) / (double)sampleRate);
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(i * 4), Pcm(left));
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan(i * 4 + 2), Pcm(right));
            }
            writer.Write(block, 0, count * 4);
        }
    }

    private static short Pcm(double value) => (short)Math.Round(Math.Clamp(value, -1, 1) * short.MaxValue);
}
