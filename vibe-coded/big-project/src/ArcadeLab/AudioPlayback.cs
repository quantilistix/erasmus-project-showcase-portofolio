using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ArcadeLab;

/// <summary>Continuous 48 kHz playback, independent of rendering and GPU learning.
/// Maze playback synthesizes the same zero-phase events as the lower-rate neural audio sensor.</summary>
internal sealed unsafe partial class AudioPlayback : IDisposable
{
    private const int PlaybackRate = 48000, BlockSamples = 480, QueueDepth = 3;
    private nint device, context;
    private uint source;
    private readonly uint[] buffers = new uint[6];
    private readonly ConcurrentQueue<SoundEvent> pending = new();
    private readonly ConcurrentQueue<float[]> rawBlocks = new();
    private readonly SineMixer mixer = new(PlaybackRate);
    private Thread? worker;
    private volatile bool stopping, available;
    private int fadeRequested;
    private long renderedSamples;
    private int errors, underruns;
    internal bool IsAvailable => available;
    internal long RenderedSamples => Interlocked.Read(ref renderedSamples);
    internal int ErrorCount => Volatile.Read(ref errors);
    internal int Underruns => Volatile.Read(ref underruns);
    public AudioPlayback(bool mute)
    {
        if (mute) return;
        try
        {
            device = OpenDevice(0); if (device == 0) return;
            context = CreateContext(device, null);
            if (context == 0 || MakeContextCurrent(context) == 0) { Dispose(); return; }
            GenSources(1, out source); fixed (uint* p = buffers) GenBuffers(buffers.Length, p);
            MakeContextCurrent(0); available = true;
            worker = new Thread(AudioLoop) { IsBackground = true, Name = "Maze continuous audio" }; worker.Start();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        { Console.Error.WriteLine($"Optional audio playback unavailable: {e.Message}"); Dispose(); }
    }
    public void SubmitSounds(IReadOnlyList<SoundEvent> sounds)
    { if (!available) return; foreach (var sound in sounds) if (pending.Count < 256) pending.Enqueue(sound); }
    public void FadeOut() => Interlocked.Exchange(ref fadeRequested, 1);
    // Legacy arcade adapters use resampled PCM. The maze uses independent analytic voices instead.
    public void Submit(ReadOnlySpan<float> samples, int sampleRate = ArcadeGame.SampleRate)
    {
        if (!available || rawBlocks.Count >= 8) return;
        int count = Math.Max(1, (int)Math.Round(samples.Length * (double)PlaybackRate / sampleRate));
        var block = new float[count];
        for (int i = 0; i < count; i++)
        { double position = i * (double)sampleRate / PlaybackRate; int index = Math.Min((int)position, samples.Length - 1); float fraction = (float)(position - index); block[i] = samples[index] * (1 - fraction) + samples[Math.Min(index + 1, samples.Length - 1)] * fraction; }
        rawBlocks.Enqueue(block);
    }
    private void AudioLoop()
    {
        MakeContextCurrent(context);
        var free = new Stack<uint>(buffers); var floats = new float[BlockSamples * 2]; var pcm = new short[BlockSamples * 2];
        float[]? raw = null; int rawIndex = 0; bool draining = false, started = false; long stopAt = 0;
        try
        {
            while (true)
            {
                if (stopping && !draining)
                { draining = true; mixer.FadeOut(); stopAt = Environment.TickCount64 + 80; }
                if (draining && Environment.TickCount64 >= stopAt) break;
                if (Interlocked.Exchange(ref fadeRequested, 0) != 0) mixer.FadeOut();
                while (pending.TryDequeue(out var sound)) if (!draining) mixer.Add(sound);
                GetSource(source, 0x1016, out int processed);
                for (int i = 0; i < processed; i++) { Unqueue(source, 1, out uint buffer); free.Push(buffer); }
                GetSource(source, 0x1015, out int queued);
                while (queued < QueueDepth && free.Count > 0)
                {
                    mixer.RenderInterleaved(floats);
                    for (int i = 0; i < BlockSamples; i++)
                    {
                        if (!draining && (raw is null || rawIndex >= raw.Length)) { rawBlocks.TryDequeue(out raw); rawIndex = 0; }
                        if (!draining && raw is not null) { float value = raw[rawIndex++]; floats[2*i] += value; floats[2*i+1] += value; }
                        pcm[2*i] = (short)(Math.Clamp(floats[2*i], -1, 1) * short.MaxValue);
                        pcm[2*i+1] = (short)(Math.Clamp(floats[2*i+1], -1, 1) * short.MaxValue);
                    }
                    uint next = free.Pop(); fixed (short* p = pcm) BufferData(next, 0x1103, p, pcm.Length * 2, PlaybackRate);
                    Queue(source, 1, in next); queued++; Interlocked.Add(ref renderedSamples, BlockSamples);
                }
                GetSource(source, 0x1010, out int state);
                if (state != 0x1012 && queued > 0) { if (started) Interlocked.Increment(ref underruns); Play(source); started = true; }
                int error = GetError();
                if (error != 0) { Interlocked.Increment(ref errors); Console.Error.WriteLine($"Audio stream error 0x{error:X}."); break; }
                Thread.Sleep(2);
            }
        }
        catch (Exception e) { Interlocked.Increment(ref errors); Console.Error.WriteLine($"Audio stream stopped: {e.Message}"); }
        finally { available = false; Stop(source); MakeContextCurrent(0); }
    }
    public void Dispose()
    {
        available = false; stopping = true; worker?.Join(); worker = null;
        if (context != 0)
        {
            MakeContextCurrent(context);
            if (source != 0) { Stop(source); DeleteSources(1, in source); source = 0; }
            fixed (uint* p = buffers) DeleteBuffers(buffers.Length, p);
            MakeContextCurrent(0); DestroyContext(context); context = 0;
        }
        if (device != 0) { CloseDevice(device); device = 0; }
    }
    [LibraryImport("openal",EntryPoint="alGetError")]private static partial int GetError();
    [LibraryImport("openal",EntryPoint="alcOpenDevice")]private static partial nint OpenDevice(nint name);
    [LibraryImport("openal",EntryPoint="alcCloseDevice")]private static partial byte CloseDevice(nint device);
    [LibraryImport("openal",EntryPoint="alcCreateContext")]private static partial nint CreateContext(nint device,int* attributes);
    [LibraryImport("openal",EntryPoint="alcMakeContextCurrent")]private static partial byte MakeContextCurrent(nint context);
    [LibraryImport("openal",EntryPoint="alcDestroyContext")]private static partial void DestroyContext(nint context);
    [LibraryImport("openal",EntryPoint="alGenSources")]private static partial void GenSources(int count,out uint source);
    [LibraryImport("openal",EntryPoint="alDeleteSources")]private static partial void DeleteSources(int count,in uint source);
    [LibraryImport("openal",EntryPoint="alGenBuffers")]private static partial void GenBuffers(int count,uint* buffers);
    [LibraryImport("openal",EntryPoint="alDeleteBuffers")]private static partial void DeleteBuffers(int count,uint* buffers);
    [LibraryImport("openal",EntryPoint="alBufferData")]private static partial void BufferData(uint buffer,int format,void* data,int size,int frequency);
    [LibraryImport("openal",EntryPoint="alSourceQueueBuffers")]private static partial void Queue(uint source,int count,in uint buffer);
    [LibraryImport("openal",EntryPoint="alSourceUnqueueBuffers")]private static partial void Unqueue(uint source,int count,out uint buffer);
    [LibraryImport("openal",EntryPoint="alGetSourcei")]private static partial void GetSource(uint source,int property,out int value);
    [LibraryImport("openal",EntryPoint="alSourcePlay")]private static partial void Play(uint source);
    [LibraryImport("openal",EntryPoint="alSourceStop")]private static partial void Stop(uint source);
}
