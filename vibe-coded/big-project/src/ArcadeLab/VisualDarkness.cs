namespace ArcadeLab;

/// <summary>Low-value score from current world-ray RGB channels. HUD pixels are excluded.</summary>
public static class VisualDarkness
{
    public static float RewardSignal(float darkness,float gameSpeed)=>gameSpeed*darkness*darkness;

    public static float Measure(ReadOnlySpan<float> rgb)
    {
        int count=MazeGame.VisionRayCount*MazeVision.Channels;
        if(rgb.Length<count)throw new ArgumentException("Expected the current RGB world-ray line.");
        double sum=0;
        for(int i=0;i<count;i++)
        {
            if(!float.IsFinite(rgb[i]))throw new ArgumentException("Visual channels must be finite.");
            sum+=Math.Clamp(rgb[i],0,1);
        }
        return (float)Math.Clamp(1-sum/count,0,1);
    }
}
