namespace ArcadeLab;

/// <summary>Common positive pleasure baseline plus amplitude-scaled, sample-held pain randomness.</summary>
public sealed class RewardDriveInputs
{
    private readonly float[] noise;
    private readonly int holdFrames;
    private long nextSample;
    private bool sampled;
    public RewardDriveInputs(int count,int holdFrames=12)
    {
        if(count<1||holdFrames<1)throw new ArgumentOutOfRangeException(nameof(count));
        noise=new float[count];this.holdFrames=holdFrames;
    }
    public void Write(Span<float> destination,long frame,float pleasure,float pain,Random random)
    {
        if(destination.Length!=noise.Length||frame<0||!float.IsFinite(pleasure)||pleasure<0||!float.IsFinite(pain)||pain<0)throw new ArgumentException("Nonnegative finite pleasure/pain and matching drive inputs required.");
        if(pain>0&&(!sampled||frame>=nextSample))
        {
            for(int i=0;i<noise.Length;i++)noise[i]=(float)random.NextDouble();
            nextSample=checked(frame+holdFrames);sampled=true;
        }
        for(int i=0;i<noise.Length;i++)destination[i]=pleasure+pain*noise[i];
    }
    public RewardDriveState Capture()=>new((float[])noise.Clone(),nextSample,sampled);
    public void Restore(RewardDriveState state)
    {
        if(state.Noise?.Length!=noise.Length||state.NextSample<0||state.Noise.Any(v=>!float.IsFinite(v)||v<0||v>1))throw new InvalidDataException("Invalid reward drive state.");
        state.Noise.CopyTo(noise,0);nextSample=state.NextSample;sampled=state.Sampled;
    }
}
public sealed record RewardDriveState(float[] Noise,long NextSample,bool Sampled);
