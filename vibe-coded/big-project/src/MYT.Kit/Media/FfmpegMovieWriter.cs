using System.Diagnostics;

namespace MYT.Kit.Media;

/// <summary>Streams offline RGBA frames into FFmpeg; stderr is drained while stdin is written.</summary>
public sealed class FfmpegMovieWriter : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _errors;
    private readonly int _frameBytes;
    private bool _finished;

    public FfmpegMovieWriter(string executable, string workingDirectory, string output,
        int width, int height, int fps, string audioFile, string? subtitleFile = null)
    {
        if (width < 2 || height < 2 || width % 2 != 0 || height % 2 != 0 || fps < 1 || fps > 120)
            throw new ArgumentException("H.264 requires positive even dimensions and 1–120 fps.");
        _frameBytes = checked(width * height * 4);
        ProcessStartInfo info = new(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        void Add(params string[] args) { foreach (string arg in args) info.ArgumentList.Add(arg); }
        Add("-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-f", "rawvideo", "-pixel_format", "rgba",
            "-video_size", $"{width}x{height}", "-framerate", fps.ToString(), "-i", "pipe:0", "-i", audioFile);
        if (subtitleFile is not null)
        {
            // A local basename avoids FFmpeg filter syntax ambiguities in arbitrary output paths.
            if (subtitleFile != Path.GetFileName(subtitleFile) || subtitleFile.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.' && c != '_'))
                throw new ArgumentException("Subtitle file must be a simple basename in the working directory.");
            Add("-vf", $"ass={subtitleFile}");
        }
        Add("-map", "0:v:0", "-map", "1:a:0", "-c:v", "libx264", "-preset", "fast", "-crf", "20",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", output);
        _process = Process.Start(info) ?? throw new IOException("Could not start FFmpeg.");
        _errors = _process.StandardError.ReadToEndAsync();
    }

    public async ValueTask WriteFrameAsync(ReadOnlyMemory<byte> rgba, CancellationToken token = default)
    {
        if (_finished) throw new InvalidOperationException("Movie writer has finished.");
        if (rgba.Length != _frameBytes) throw new ArgumentException("Incorrect RGBA frame size.", nameof(rgba));
        try { await _process.StandardInput.BaseStream.WriteAsync(rgba, token); }
        catch (IOException error)
        {
            if (_process.HasExited) throw new IOException($"FFmpeg exited: {await _errors}", error);
            throw;
        }
    }

    public async Task FinishAsync(CancellationToken token = default)
    {
        if (_finished) return;
        _process.StandardInput.Close();
        await _process.WaitForExitAsync(token);
        string errors = await _errors;
        if (_process.ExitCode != 0) throw new IOException($"FFmpeg failed ({_process.ExitCode}): {errors}");
        _finished = true;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
            await _errors;
        }
        finally { _process.Dispose(); }
    }
}
