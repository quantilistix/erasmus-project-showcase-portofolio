using Silk.NET.GLFW;

namespace VoxelGame;

/// <summary>Physical key state survives frames and repeat events until release or focus loss.</summary>
internal sealed class KeyboardState
{
    private readonly HashSet<Keys> _held = [];
    private readonly HashSet<Keys> _pending = [];
    private readonly HashSet<Keys> _pressed = [];
    public IEnumerable<Keys> PressedKeys => _pressed;
    public bool Down(Keys key) => _held.Contains(key);
    public bool Pressed(Keys key) => _pressed.Contains(key);
    public void Handle(Keys key, InputAction action, bool physicalPolling = false)
    {
        if (key == Keys.Unknown) return;
        if (physicalPolling)
        {
            // Preserve taps between snapshots, but synthetic repeat releases cannot
            // change a physically held key or produce another press edge.
            if (action == InputAction.Press && !_held.Contains(key)) _pending.Add(key);
            return;
        }
        if (action == InputAction.Release) _held.Remove(key);
        else if (action == InputAction.Repeat) _held.Add(key);
        else if (_held.Add(key)) _pending.Add(key);
    }
    // Reconcile with GLFW's retained key state after events. Callbacks preserve short taps;
    // polling repairs a missed press/release without depending on another repeat event.
    public void Reconcile(Keys key, bool down)
    {
        if (down) { if (_held.Add(key)) _pending.Add(key); }
        else _held.Remove(key);
    }
    public void Update()
    {
        _pressed.Clear();
        _pressed.UnionWith(_pending);
        _pending.Clear();
    }
    public void Clear() { _held.Clear(); _pressed.Clear(); _pending.Clear(); }
}
