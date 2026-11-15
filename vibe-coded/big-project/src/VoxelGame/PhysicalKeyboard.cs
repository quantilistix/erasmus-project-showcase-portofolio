using System.Runtime.InteropServices;
using Silk.NET.GLFW;

namespace VoxelGame;

/// <summary>X11's physical key bitmap, independent of GLFW's event/repeat cache.</summary>
internal sealed unsafe class PhysicalKeyboard
{
    private readonly nint _display;
    private readonly byte[] _keys = new byte[32];
    private readonly Dictionary<Keys, int> _scanCodes;

    private PhysicalKeyboard(nint display, Glfw glfw, IEnumerable<Keys> keys)
    {
        _display = display;
        _scanCodes = keys.ToDictionary(key => key, key => glfw.GetKeyScancode((int)key));
    }

    public static PhysicalKeyboard? TryCreate(Glfw glfw, IEnumerable<Keys> keys)
    {
        if (!OperatingSystem.IsLinux()) return null;
        // Query the same GLFW library/connection Silk loaded. Do not call X11 APIs
        // on Wayland, or load a second, unrelated GLFW instance.
        if (glfw.Context.TryGetProcAddress("glfwGetPlatform", out nint platform)
            && ((delegate* unmanaged[Cdecl]<int>)platform)() != 0x00060004) return null;
        // GLFW 3.3 is compiled for one platform and has no glfwGetPlatform;
        // its X11 native export itself identifies the backend.
        if (!glfw.Context.TryGetProcAddress("glfwGetX11Display", out nint getDisplay)) return null;
        nint display = ((delegate* unmanaged[Cdecl]<nint>)getDisplay)();
        if (display == 0) return null;
        try
        {
            PhysicalKeyboard keyboard = new(display, glfw, keys);
            keyboard.Update();
            return keyboard;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    public void Update()
    {
        fixed (byte* keys = _keys) XQueryKeymap(_display, keys);
    }

    public bool Down(Keys key) => _scanCodes.TryGetValue(key, out int scan) && IsDown(_keys, scan);

    internal static bool IsDown(ReadOnlySpan<byte> bitmap, int scan) => scan is >= 0 and < 256
        && bitmap.Length >= 32 && (bitmap[scan >> 3] & (1 << (scan & 7))) != 0;

    [DllImport("libX11.so.6", CallingConvention = CallingConvention.Cdecl)]
    private static extern int XQueryKeymap(nint display, byte* keys);
}
