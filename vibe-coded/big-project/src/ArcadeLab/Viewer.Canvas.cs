namespace ArcadeLab;

/// <summary>Shared native canvas for tools using the existing Vulkan presentation path.</summary>
public sealed unsafe partial class Viewer
{
    private int[]? softwarePixels;
    private Viewer(int width,int height)
    {
        extent=new((uint)width,(uint)height);softwarePixels=new int[checked(width*height)];
    }
    /// <summary>Render the same tool canvas to a PPM without a desktop or Vulkan device.</summary>
    public static void CaptureCanvas(int width,int height,string path,Action<Viewer,float,float> draw)
    {
        if(width<640||height<480)throw new ArgumentOutOfRangeException(nameof(width));
        using var canvas=new Viewer(width,height);canvas.softwarePixels!.AsSpan().Fill(canvas.PackColor(.018f,.025f,.045f));draw(canvas,width,height);canvas.FinishGraphPreview();
        using var stream=File.Create(path);stream.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        foreach(int pixel in canvas.softwarePixels!){stream.WriteByte((byte)pixel);stream.WriteByte((byte)(pixel>>8));stream.WriteByte((byte)(pixel>>16));}
    }
    private Glfw.CharacterCallback? canvasCharacterCallback;
    public event Action<uint>? CharacterInput;
    public event Action<float>? ScrollInput;
    private Glfw.ScrollCallback? canvasScrollCallback;
    public void SetMinimumSize(int width,int height)=>Glfw.SetWindowSizeLimits(window,width,height,-1,-1);
    public bool IsOpen=>Glfw.WindowShouldClose(window)==0;
    public bool Key(int code)=>Glfw.GetKey(window,code)!=0;
    public bool MouseButton(int code)=>Glfw.GetMouseButton(window,code)!=0;
    public (float X,float Y) Cursor
    {
        get { Glfw.GetCursorPos(window,out double x,out double y);Glfw.GetWindowSize(window,out int w,out int h);return ((float)(x*extent.Width/Math.Max(1,w)),(float)(y*extent.Height/Math.Max(1,h))); }
    }
    public void PollCanvas()
    {
        if(canvasCharacterCallback is null){canvasCharacterCallback=(_,code)=>CharacterInput?.Invoke(code);Glfw.SetCharCallback(window,canvasCharacterCallback);}
        if(canvasScrollCallback is null){canvasScrollCallback=(_,x,y)=>ScrollInput?.Invoke((float)y);Glfw.SetScrollCallback(window,canvasScrollCallback);}
        Glfw.PollEvents();
    }
    public void DrawCanvas(Action<float,float> draw,string title)
    {
        Glfw.SetWindowTitle(window,title);Draw(null,[],0,canvas:draw);
    }
    public void CanvasRect(float x,float y,float width,float height,float r,float g,float b)=>Rect(x,y,width,height,r,g,b);
    public void CanvasLabel(string text,float x,float y,int scale=2)=>Label(text.ToUpperInvariant(),x,y,scale);
}
