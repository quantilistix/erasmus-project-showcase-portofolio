namespace ArcadeLab;

public sealed record MazeAdaptiveState(int Version,float[] Weights,float[] TargetWeights,long Decisions,long Updates);

/// <summary>
/// Small nonlinear Q network over current, previous and slow sensory traces. Online TD(lambda),
/// bounded replay and a local empirical transition graph supply delayed credit and planning.
/// The policy never reads Position, VisionDepth, MapPixels, teacher routes or enemy objects.
/// </summary>
public sealed class MazeAdaptiveLearner
{
    public static readonly MazeControls[] Actions=[MazeControls.None,MazeControls.Forward,MazeControls.Backward,
        MazeControls.TurnLeft,MazeControls.TurnRight,MazeControls.StrafeLeft,MazeControls.StrafeRight,
        MazeControls.Forward|MazeControls.TurnLeft,MazeControls.Forward|MazeControls.TurnRight,
        MazeControls.Fire,MazeControls.Forward|MazeControls.Fire,MazeControls.StrafeLeft|MazeControls.Fire,
        MazeControls.StrafeRight|MazeControls.Fire,MazeControls.Reload];
    public const int CurrentCount=112,InputCount=CurrentCount*2+72,HiddenCount=32;
    private const int HiddenStride=InputCount+1,OutputOffset=HiddenCount*HiddenStride,OutputStride=HiddenCount+1;
    public static int ParameterCount=>OutputOffset+Actions.Length*OutputStride;
    private readonly float[] weights=new float[ParameterCount],target=new float[ParameterCount],trace=new float[ParameterCount];
    private readonly float[] previous=new float[CurrentCount],slow=new float[72];
    private readonly Random random;
    private readonly List<Experience> replay=[];
    private readonly Dictionary<ulong,Dictionary<int,Transition>> model=[];
    private float[]? pending,nextInput;
    private int action,held;
    private float reward,discount=1;
    private bool sensed;
    public long Decisions { get; private set; }
    public long Updates { get; private set; }
    public float LastTdError { get; private set; }
    public int ReplayCount=>replay.Count;
    public int KnownViews=>model.Count;
    public float Exploration=>Math.Max(.05f,.35f/(1+Decisions/4000f));
    private sealed record Experience(float[] Before,int Action,float Reward,float Discount,float[] After);
    private sealed record Transition(ulong Next,float Reward,float Discount,int Count);
    public MazeAdaptiveLearner(int seed)
    {
        random=new(seed);
        for(int i=0;i<OutputOffset;i++)weights[i]=(float)(random.NextDouble()*2-1)*MathF.Sqrt(2f/InputCount);
        for(int i=OutputOffset;i<weights.Length;i++)weights[i]=(float)(random.NextDouble()*2-1)*.02f;
        weights.CopyTo(target,0);
    }
    public void ResetEpisode()
    {
        pending=nextInput=null;held=0;reward=0;discount=1;sensed=false;
        Array.Clear(trace);Array.Clear(previous);Array.Clear(slow);model.Clear();
    }
    public MazeControls Choose(MazeGame game,ReadOnlySpan<float> cortex,bool training)
    {
        if(pending is not null)return Actions[action];
        var input=nextInput??Sense(game,cortex);nextInput=null;var q=Predict(input,weights);ulong key=Key(input);
        if(model.TryGetValue(key,out var routes))
            foreach(var (a,t) in routes)if(t.Count>=2)q[a]=.8f*q[a]+.2f*Plan(t,2);
        int greedy=Best(q,game);
        action=training&&random.NextDouble()<Exploration?RandomAction(game):greedy;
        if(action!=greedy)Array.Clear(trace); // Watkins trace cut for exploratory actions.
        pending=input;held=0;reward=0;discount=1;Decisions++;
        return Actions[action];
    }
    public void Observe(MazeGame game,ReadOnlySpan<float> cortex,float feedback,bool terminal,bool training)
    {
        if(pending is null)return;
        reward+=discount*feedback;
        discount*=MathF.Pow(.99f,game.Speed);held++;
        if(!terminal&&held*game.Speed<8)return;
        var next=Sense(game,cortex);float gamma=terminal?0:discount;
        if(training)
        {
            var sample=new Experience(pending,action,reward,gamma,next);
            Learn(sample,true);
            if(replay.Count<2048)replay.Add(sample);else replay[random.Next(replay.Count)]=sample;
            for(int i=0;i<4;i++)Learn(replay[random.Next(replay.Count)],false);
            ulong from=Key(pending),to=Key(next);
            if(model.Count>=4096&&!model.ContainsKey(from))model.Clear();
            if(!model.TryGetValue(from,out var routes))model[from]=routes=[];
            // Stochastic outcomes use a running reward estimate; latest observed successor.
            routes.TryGetValue(action,out var old);int count=Math.Min(100,(old?.Count??0)+1);
            routes[action]=new(to,(old?.Reward??0)+(reward-(old?.Reward??0))/count,gamma,count);
            if(Decisions%100==0)weights.CopyTo(target,0);
        }
        nextInput=terminal?null:next;pending=null;
        if(terminal)Array.Clear(trace);
    }
    private float Plan(Transition t,int depth)
    {
        if(depth==0||t.Discount==0||!model.TryGetValue(t.Next,out var routes)||routes.Count==0)return t.Reward;
        return t.Reward+t.Discount*routes.Values.Max(n=>Plan(n,depth-1));
    }
    private static int Best(float[] q,MazeGame game)
    {
        int best=0;for(int i=1;i<q.Length;i++)if(Allowed(i,game)&&q[i]>q[best])best=i;return best;
    }
    private static bool Allowed(int i,MazeGame game)=>((Actions[i]&MazeControls.Fire)==0||(game.Ammo>0&&!game.Reloading))
        &&(Actions[i]!=MazeControls.Reload||(game.Reserve>0&&game.Ammo<MazeGame.MagazineSize&&!game.Reloading));
    private int RandomAction(MazeGame game)
    { int i;do{i=random.Next(Actions.Length);}while(!Allowed(i,game));return i; }
    private static float[] Predict(float[] input,float[] parameters,float[]? hidden=null)
    {
        hidden??=new float[HiddenCount];
        for(int h=0;h<HiddenCount;h++)
        {
            int at=h*HiddenStride;float sum=parameters[at+InputCount];
            for(int i=0;i<InputCount;i++)sum+=parameters[at+i]*input[i];
            hidden[h]=MathF.Tanh(sum);
        }
        var q=new float[Actions.Length];
        for(int a=0;a<q.Length;a++)
        { int at=OutputOffset+a*OutputStride;q[a]=parameters[at+HiddenCount];for(int h=0;h<HiddenCount;h++)q[a]+=parameters[at+h]*hidden[h]; }
        return q;
    }
    private void Learn(Experience e,bool eligibility)
    {
        var hidden=new float[HiddenCount];var q=Predict(e.Before,weights,hidden);
        // Target network stabilizes bootstrapping; terminal transitions never bootstrap.
        var nextValues=Predict(e.After,target);
        float nextValue=nextValues.Where((_,a)=>((Actions[a]&MazeControls.Fire)==0||(e.After[89]>0&&e.After[91]<.5f))
            &&(Actions[a]!=MazeControls.Reload||(e.After[90]>0&&e.After[89]<1&&e.After[91]<.5f))).Max();
        float delta=Math.Clamp(e.Reward+e.Discount*nextValue-q[e.Action],-2,2);
        if(eligibility)LastTdError=delta;
        var gradient=new float[ParameterCount];int output=OutputOffset+e.Action*OutputStride;
        for(int h=0;h<HiddenCount;h++)
        {
            gradient[output+h]=hidden[h];float local=weights[output+h]*(1-hidden[h]*hidden[h]);int at=h*HiddenStride;
            for(int i=0;i<InputCount;i++)gradient[at+i]=local*e.Before[i];gradient[at+InputCount]=local;
        }
        gradient[output+HiddenCount]=1;
        for(int i=0;i<weights.Length;i++)
        {
            float g=gradient[i];
            if(eligibility){trace[i]=Math.Clamp(MathF.Pow(.99f,8)*.85f*trace[i]+g,-3,3);g=trace[i];}
            weights[i]=Math.Clamp(weights[i]+(eligibility?.002f:.0005f)*delta*g,-4,4);
        }
        Updates++;
    }
    private float[] Sense(MazeGame game,ReadOnlySpan<float> cortex)
    {
        var input=new float[InputCount];
        for(int ray=0;ray<MazeGame.VisionRayCount;ray++)for(int c=0;c<3;c++)input[(ray/16)*3+c]+=game.RgbPixels[ray*3+c]/16;
        for(int i=0;i<16;i++)input[72+i]=Math.Clamp(game.Tactile[i]/8,0,1);
        input[88]=game.Health/100;input[89]=game.Ammo/8f;input[90]=game.Reserve/96f;input[91]=game.Reloading?1:0;
        input[92]=MathF.Sqrt(game.Audio.Sum(x=>x*x)/game.Audio.Length);input[93]=MathF.Sqrt(game.AudioRight.Sum(x=>x*x)/game.AudioRight.Length);
        input[94]=Math.Clamp(game.PrimitivePain,0,1);input[95]=Math.Clamp(game.PrimitivePleasure,0,1);
        for(int i=0;i<8;i++){input[96+i]=sensed&&(Actions[action]&MazeControlPolicy.Buttons[i])!=0?1:0;input[104+i]=Math.Clamp(cortex[i],0,1);}
        if(!sensed){input.AsSpan(0,CurrentCount).CopyTo(previous);input.AsSpan(0,72).CopyTo(slow);sensed=true;}
        previous.CopyTo(input,CurrentCount);
        for(int i=0;i<72;i++){slow[i]+=.1f*(input[i]-slow[i]);input[2*CurrentCount+i]=slow[i];}
        input.AsSpan(0,CurrentCount).CopyTo(previous);return input;
    }
    private static ulong Key(float[] input)
    {
        ulong key=14695981039346656037;
        for(int i=0;i<96;i++){key^=(ulong)Math.Clamp((int)(input[i]*8),0,255);key*=1099511628211;}
        return key;
    }
    public MazeAdaptiveState Capture()=>new(1,(float[])weights.Clone(),(float[])target.Clone(),Decisions,Updates);
    public void Restore(MazeAdaptiveState state)
    {
        if(state.Version!=1||state.Weights.Length!=ParameterCount||state.TargetWeights.Length!=ParameterCount||state.Decisions<0||state.Updates<0
            ||state.Weights.Concat(state.TargetWeights).Any(v=>!float.IsFinite(v)||Math.Abs(v)>4))throw new InvalidDataException("Invalid adaptive learner checkpoint.");
        state.Weights.CopyTo(weights,0);state.TargetWeights.CopyTo(target,0);Decisions=state.Decisions;Updates=state.Updates;
        replay.Clear();ResetEpisode();
    }
}
