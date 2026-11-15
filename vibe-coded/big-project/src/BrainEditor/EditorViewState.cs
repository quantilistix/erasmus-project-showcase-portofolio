namespace ArcadeLab.Editor;

/// <summary>Project-local navigation metadata, independent of neural model edits and undo.</summary>
public sealed class EditorViewState
{
    public float Yaw {get;set;}=GraphCamera.DefaultYaw;
    public float Pitch {get;set;}=GraphCamera.DefaultPitch;
    public float Zoom {get;set;}=1;
    /// <summary>World-space translation of the camera and orbit target.</summary>
    public Position Pan {get;set;}
    public Position? FrameCenter {get;set;}
    public float FrameSpan {get;set;}
    public bool IsValid()=>float.IsFinite(Yaw)&&float.IsFinite(Pitch)&&Math.Abs(Pitch)<=1.5f
        &&float.IsFinite(Zoom)&&Zoom>=.03f&&Zoom<=20&&Finite(Pan)
        &&(FrameCenter is not {} p||Finite(p))&&float.IsFinite(FrameSpan)&&FrameSpan>=0;
    private static bool Finite(Position p)=>float.IsFinite(p.X)&&float.IsFinite(p.Y)&&float.IsFinite(p.Z);
    public void Reset()
    {
        Yaw=GraphCamera.DefaultYaw;Pitch=GraphCamera.DefaultPitch;Zoom=1;Pan=new();FrameCenter=null;FrameSpan=0;
    }
}
