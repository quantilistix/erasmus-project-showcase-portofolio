namespace VoxelGame;

/// <summary>Frame-loop timing from real elapsed seconds, independent of the simulation's
/// catch-up cap. The worst frame is visible immediately and retained through the next bucket.</summary>
internal sealed class FrameTiming
{
    private double _elapsed;
    private int _frames;
    private double _worst;
    private double _previousWorst;
    private double _reportedFps;

    public double FramesPerSecond => _reportedFps > 0 ? _reportedFps : _frames / Math.Max(_elapsed, 1e-9);
    public double SlowestSeconds => Math.Max(_worst, _previousWorst);

    public void Add(double seconds)
    {
        if (!(seconds > 0) || !double.IsFinite(seconds))
            return;
        _elapsed += seconds;
        _frames++;
        _worst = Math.Max(_worst, seconds);
        if (_elapsed < 1.0)
            return;
        _reportedFps = _frames / _elapsed;
        _previousWorst = _worst;
        _elapsed = 0;
        _frames = 0;
        _worst = 0;
    }
}
