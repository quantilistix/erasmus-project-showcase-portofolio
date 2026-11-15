namespace ArcadeLab;

/// <summary>One key/action table for physical keyboard input and baked motor ports.</summary>
public sealed record MazeKeyBinding(string Key,int Code,MazeControls Control,string Action)
{
    public static IReadOnlyList<MazeKeyBinding> All { get; }=new MazeKeyBinding[]
    {
        new("W",87,MazeControls.Forward,"FORWARD"),new("S",83,MazeControls.Backward,"BACKWARD"),
        new("A",65,MazeControls.TurnLeft,"TURN LEFT"),new("D",68,MazeControls.TurnRight,"TURN RIGHT"),
        new("Q",81,MazeControls.StrafeLeft,"STRAFE LEFT"),new("E",69,MazeControls.StrafeRight,"STRAFE RIGHT"),
        new("F",70,MazeControls.Fire,"FIRE"),new("X",88,MazeControls.Reload,"RELOAD")
    };
    public static MazeControls Read(Func<int,bool> keyDown)
    {
        MazeControls controls=MazeControls.None;
        foreach(var binding in All)if(keyDown(binding.Code))controls|=binding.Control;
        if(keyDown(263))controls|=MazeControls.TurnLeft;
        if(keyDown(262))controls|=MazeControls.TurnRight;
        return controls;
    }
}
