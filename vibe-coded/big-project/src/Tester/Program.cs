

using Silk.NET.GLFW;
namespace IntelliCraft;


public static partial class Program
{

    public static Task<int> Main(string[] arguments)
    {

        Init();
        GameLoop();
        Terminate();

        return Task.FromResult(0);

    }

}

