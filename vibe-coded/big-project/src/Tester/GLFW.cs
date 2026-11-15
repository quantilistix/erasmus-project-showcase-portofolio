

using Silk.NET.GLFW;

namespace IntelliCraft;


public static partial class Program
{

    private static Glfw Glfw = null!;
    private static unsafe WindowHandle* Window;

    private static void InitGLFW()
    {
        
        Glfw = Glfw.GetApi();
        if (!Glfw.Init()) throw new Exception("Failed to initialize GLFW");


        Glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
        Glfw.WindowHint(WindowHintBool.Resizable, false);


        unsafe
        {
            Window = Glfw.CreateWindow(800, 600, "Test", null, null);
            if (Window == null)
            {
                Glfw.DestroyWindow(Window);
                Glfw.Terminate();
                throw new Exception("Failed to create window!");
            }
            Glfw.SetWindowPos(Window, 100, 100);
        }
    }


    private static void TerminateGLFW()
    {
        unsafe
        {
            Glfw.DestroyWindow(Window);
        }

        Glfw.Terminate();
    }
}