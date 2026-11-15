using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ArcadeLab;

internal static class NativeLibraries
{
    [ModuleInitializer]
    internal static void Initialize() => NativeLibrary.SetDllImportResolver(typeof(NativeLibraries).Assembly, (name, assembly, path) => name switch
    {
        "glfw3" => NativeLibrary.Load(OperatingSystem.IsWindows() ? "glfw3.dll" : OperatingSystem.IsMacOS() ? "libglfw.3.dylib" : "libglfw.so.3", assembly, path),
        "openal" => NativeLibrary.Load(OperatingSystem.IsWindows() ? "OpenAL32.dll" : OperatingSystem.IsMacOS() ? "/System/Library/Frameworks/OpenAL.framework/OpenAL" : "libopenal.so.1", assembly, path),
        _ => 0
    });
}
