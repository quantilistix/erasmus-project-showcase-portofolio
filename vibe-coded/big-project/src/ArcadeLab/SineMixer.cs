namespace ArcadeLab;

public readonly record struct SoundEvent(float Frequency, float Volume, float Duration, float Pan = 0);

/// <summary>Sample-clocked sine voices. Every note starts at zero, keeps its own phase and ends at zero.</summary>
public sealed class SineMixer
{
    private readonly int sampleRate;
    private readonly List<Voice> voices = [];
    public int ActiveVoices => voices.Count;
    public SineMixer(int sampleRate)
    { if (sampleRate < 1000) throw new ArgumentOutOfRangeException(nameof(sampleRate)); this.sampleRate = sampleRate; }
    public void Add(SoundEvent sound)
    {
        if (!float.IsFinite(sound.Frequency) || sound.Frequency <= 0 || sound.Frequency >= sampleRate * .5f ||
            !float.IsFinite(sound.Volume) || sound.Volume < 0 || sound.Volume > 1 || !float.IsFinite(sound.Duration) || sound.Duration < .01f || sound.Duration > 5 || !float.IsFinite(sound.Pan) || sound.Pan < -1 || sound.Pan > 1)
            throw new ArgumentException("Invalid sine voice.");
        // Never steal an active voice abruptly. Bound exceptionally dense event bursts instead.
        if (voices.Count < 64 && sound.Volume > 0) voices.Add(new(sound, sampleRate));
    }
    public void Clear() => voices.Clear();
    public void FadeOut()
    { foreach (var voice in voices) voice.ReleaseSamples = Math.Max(2, sampleRate / 100); }
    public void Render(Span<float> destination)
    {
        for (int i = 0; i < destination.Length; i++) { Next(out double left, out double right); destination[i] = Limit((left + right) * .7071067811865476); }
    }
    public void RenderStereo(Span<float> left, Span<float> right)
    {
        if (left.Length != right.Length) throw new ArgumentException("Stereo channels must have equal lengths.");
        for (int i = 0; i < left.Length; i++) { Next(out double l, out double r); left[i] = Limit(l); right[i] = Limit(r); }
    }
    public void RenderInterleaved(Span<float> destination)
    {
        if ((destination.Length & 1) != 0) throw new ArgumentException("Stereo PCM needs complete channel pairs.");
        for (int i = 0; i < destination.Length; i += 2) { Next(out double l, out double r); destination[i] = Limit(l); destination[i + 1] = Limit(r); }
    }
    private static float Limit(double sum) { float value = (float)(sum * .45); return value / (1 + MathF.Abs(value)); }
    private void Next(out double left, out double right)
    {
        left = right = 0;
        for (int v = voices.Count - 1; v >= 0; v--)
        {
            var voice = voices[v];
            double envelope = Math.Min(1, (double)voice.Age / voice.Attack);
            envelope = Math.Min(envelope, (double)(voice.Length - 1 - voice.Age) / voice.Release);
            envelope = Math.Sin(Math.Max(0, envelope) * Math.PI / 2); envelope *= envelope;
            if (voice.ReleaseSamples >= 0)
            {
                double fade = Math.Sin(Math.Min(1, voice.ReleaseSamples / (sampleRate / 100.0)) * Math.PI / 2);
                envelope *= fade * fade; voice.ReleaseSamples--;
            }
            double wave = voice.Volume * envelope * Math.Sin(voice.Phase);
            left += wave * voice.LeftGain; right += wave * voice.RightGain;
            voice.Phase = (voice.Phase + voice.Increment) % (2 * Math.PI); voice.Age++;
            if (voice.Age >= voice.Length || voice.ReleaseSamples == 0) voices.RemoveAt(v);
        }
    }
    private sealed class Voice
    {
        public readonly int Length, Attack, Release;
        public readonly float Volume;
        public readonly double Increment, LeftGain, RightGain;
        public int Age, ReleaseSamples = -1;
        public double Phase;
        public Voice(SoundEvent sound, int rate)
        {
            LeftGain = Math.Cos((sound.Pan + 1) * Math.PI / 4); RightGain = Math.Sin((sound.Pan + 1) * Math.PI / 4);
            Length = Math.Max(3, (int)Math.Round(sound.Duration * rate)); Volume = sound.Volume;
            Attack = Math.Max(2, Math.Min(Length / 4, rate / 200));
            Release = Math.Max(2, Math.Min(Length / 3, rate / 50)); Increment = 2 * Math.PI * sound.Frequency / rate;
        }
    }
}
