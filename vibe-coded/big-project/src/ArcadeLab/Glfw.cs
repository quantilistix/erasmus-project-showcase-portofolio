using System.Runtime.InteropServices;
namespace ArcadeLab;
internal static partial class Glfw
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void CharacterCallback(nint window, uint codepoint);
    [DllImport("glfw3", EntryPoint = "glfwSetCharCallback", CallingConvention = CallingConvention.Cdecl)] internal static extern nint SetCharCallback(nint window, CharacterCallback? callback);
    [LibraryImport("glfw3", EntryPoint = "glfwGetMouseButton")] internal static partial int GetMouseButton(nint window, int button);
    [LibraryImport("glfw3", EntryPoint = "glfwSetWindowSizeLimits")] internal static partial void SetWindowSizeLimits(nint window,int minWidth,int minHeight,int maxWidth,int maxHeight);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ScrollCallback(nint window,double x,double y);
    [DllImport("glfw3",EntryPoint="glfwSetScrollCallback",CallingConvention=CallingConvention.Cdecl)] internal static extern nint SetScrollCallback(nint window,ScrollCallback callback);
    private const string Library = "glfw3";
    [LibraryImport(Library, EntryPoint = "glfwInit")] internal static partial int Init();
    [LibraryImport(Library, EntryPoint = "glfwTerminate")] internal static partial void Terminate();
    [LibraryImport(Library, EntryPoint = "glfwWindowHint")] internal static partial void WindowHint(int hint, int value);
    [LibraryImport(Library, EntryPoint = "glfwCreateWindow", StringMarshalling = StringMarshalling.Utf8)] internal static partial nint CreateWindow(int width, int height, string title, nint monitor, nint share);
    [LibraryImport(Library, EntryPoint = "glfwMaximizeWindow")] internal static partial void MaximizeWindow(nint window);
    [LibraryImport(Library, EntryPoint = "glfwDestroyWindow")] internal static partial void DestroyWindow(nint window);
    [LibraryImport(Library, EntryPoint = "glfwWindowShouldClose")] internal static partial int WindowShouldClose(nint window);
    [LibraryImport(Library, EntryPoint = "glfwPollEvents")] internal static partial void PollEvents();
    [LibraryImport(Library, EntryPoint = "glfwGetWindowAttrib")] internal static partial int GetWindowAttrib(nint window, int attribute);
    [LibraryImport(Library, EntryPoint = "glfwGetCursorPos")] internal static partial void GetCursorPos(nint window,out double x,out double y);
    [LibraryImport(Library, EntryPoint = "glfwGetWindowSize")] internal static partial void GetWindowSize(nint window,out int width,out int height);
    [LibraryImport(Library, EntryPoint = "glfwGetKey")] internal static partial int GetKey(nint window, int key);
    [LibraryImport(Library, EntryPoint = "glfwGetFramebufferSize")] internal static partial void GetFramebufferSize(nint window, out int width, out int height);
    [LibraryImport(Library, EntryPoint = "glfwSetWindowTitle", StringMarshalling = StringMarshalling.Utf8)] internal static partial void SetWindowTitle(nint window, string title);
    [LibraryImport(Library, EntryPoint = "glfwGetRequiredInstanceExtensions")] internal static unsafe partial byte** GetRequiredInstanceExtensions(out uint count);
    [LibraryImport(Library, EntryPoint = "glfwCreateWindowSurface")] internal static partial int CreateWindowSurface(nint instance, nint window, nint allocator, out ulong surface);
}
