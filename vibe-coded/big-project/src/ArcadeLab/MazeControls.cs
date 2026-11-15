namespace ArcadeLab;

[Flags]
public enum MazeControls
{
    None=0,Forward=1,Backward=2,TurnLeft=4,TurnRight=8,StrafeLeft=16,StrafeRight=32,Fire=64,Reload=128
}

public static class MazeControlPolicy
{
    public const float ActivationThreshold = .5f;
    public const MazeControls All=(MazeControls)255;
    // Preserve checkpoint ports. Former Wait (0) is a value baseline; ForwardFire (8) is reserved.
    public static ReadOnlySpan<int> OutputSlots=>[1,2,3,4,5,6,7,9];
    public static ReadOnlySpan<MazeControls> Buttons=>[MazeControls.Forward,MazeControls.Backward,MazeControls.TurnLeft,MazeControls.TurnRight,MazeControls.StrafeLeft,MazeControls.StrafeRight,MazeControls.Fire,MazeControls.Reload];
    public static MazeControls FromActivities(ReadOnlySpan<float> activities)
    {
        if(activities.Length!=8)throw new ArgumentException("Expected eight nonnegative motor neurons.");
        MazeControls controls=MazeControls.None;
        for(int i=0;i<8;i++){if(!float.IsFinite(activities[i])||activities[i]<0)throw new ArgumentException("Motor activations must be nonnegative.");if(activities[i]>ActivationThreshold)controls|=Buttons[i];}
        return controls;
    }
    public static MazeControls FromScores(ReadOnlySpan<float> scores)
    {
        if(scores.Length<MazeGame.ActionCount)throw new ArgumentException("Invalid controls/threshold.");
        MazeControls controls=MazeControls.None;
        for(int i=0;i<Buttons.Length;i++)if(scores[OutputSlots[i]]>ActivationThreshold)controls|=Buttons[i];
        return controls;
    }
    public static void WriteFeedback(MazeControls controls,Span<float> destination)
    {
        if(destination.Length!=MazeGame.ActionCount || (controls&~All)!=0)throw new ArgumentException("Invalid button feedback.");
        destination.Clear();for(int i=0;i<Buttons.Length;i++)if((controls&Buttons[i])!=0)destination[OutputSlots[i]]=1;
    }
    public static MazeControls FromLegacy(MazeAction action)=>action switch
    {
        MazeAction.Wait=>MazeControls.None,MazeAction.Forward=>MazeControls.Forward,MazeAction.Backward=>MazeControls.Backward,
        MazeAction.TurnLeft=>MazeControls.TurnLeft,MazeAction.TurnRight=>MazeControls.TurnRight,MazeAction.StrafeLeft=>MazeControls.StrafeLeft,
        MazeAction.StrafeRight=>MazeControls.StrafeRight,MazeAction.Fire=>MazeControls.Fire,MazeAction.Reload=>MazeControls.Reload,
        MazeAction.ForwardFire=>MazeControls.Forward|MazeControls.Fire,_=>throw new ArgumentOutOfRangeException(nameof(action))
    };
    public static MazeAction ToLegacy(MazeControls controls)=>controls switch
    {
        MazeControls.Forward=>MazeAction.Forward,MazeControls.Backward=>MazeAction.Backward,MazeControls.TurnLeft=>MazeAction.TurnLeft,
        MazeControls.TurnRight=>MazeAction.TurnRight,MazeControls.StrafeLeft=>MazeAction.StrafeLeft,MazeControls.StrafeRight=>MazeAction.StrafeRight,
        MazeControls.Fire=>MazeAction.Fire,MazeControls.Reload=>MazeAction.Reload,MazeControls.Forward|MazeControls.Fire=>MazeAction.ForwardFire,_=>MazeAction.Wait
    };
}
