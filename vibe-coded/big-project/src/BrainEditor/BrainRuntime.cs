using NeuralGraph;
using System.Numerics;
namespace ArcadeLab.Editor;

/// <summary>Host adapter for baked editor networks, shared by the editor and maze.</summary>
public sealed class BrainRuntime : IDisposable
{
    public BakedBrain Brain { get; }
    public IPredictiveCodingBackend Backend { get; }
    public double Time { get; private set; }
    public IReadOnlyDictionary<string,double> RewardDerivatives=>rewardDerivatives;
    public IReadOnlyDictionary<string,double> GroupFactors=>groupFactors;
    private readonly Dictionary<string,double> rewardDerivatives=[],groupFactors=[];
    private readonly HashSet<string> rewardModules=[];
    private readonly bool[] rewardEdges;
    private readonly BrainModule[] boundaryModules;
    private readonly (BrainModule Module,int[] Edges)[] painModules;
    private readonly (RewardGroup Group,int Index)[] rewardGroups;
    private readonly string[] edgeModules;
    private readonly float[] boundaryBuffer,nextWeights;

    private readonly Dictionary<string,double> previousRewards=[];
    private readonly Dictionary<string,(double Next,float Value)> pain=[];
    private readonly Dictionary<string,Random> random=[];
    private readonly Dictionary<string,long> randomDraws=[];
    public BrainRuntime(BakedBrain brain,IPredictiveCodingBackend? backend=null)
    {
        Brain=brain;brain.Model.SetActivationLimit(8);Backend=backend??new CpuPredictiveCodingBackend(brain.Model);
        foreach(var m in brain.Manifest.Modules)
        {
            random[m.Id]=new(m.Seed);
            if(m.Kind==ModuleKind.Reward||m.Name.Contains("reward",StringComparison.OrdinalIgnoreCase))rewardModules.Add(m.Id);
        }
        foreach(var group in brain.Manifest.RewardGroups.Where(g=>g.Name.Contains("reward",StringComparison.OrdinalIgnoreCase)))
            foreach(string id in group.ModuleIds)rewardModules.Add(id);
        rewardEdges=brain.Model.Topology.Edges.Select(e=>rewardModules.Contains(brain.Manifest.Neurons[e.Source].ModuleId)||rewardModules.Contains(brain.Manifest.Neurons[e.Destination].ModuleId)).ToArray();
        var modules=brain.Manifest.Modules.ToDictionary(m=>m.Id);
        boundaryModules=brain.Manifest.Neurons.Take(brain.Model.Topology.InputCount).Select(n=>modules[n.ModuleId]).ToArray();
        boundaryBuffer=new float[boundaryModules.Length];nextWeights=new float[brain.Model.Weights.Length];
        edgeModules=brain.Model.Topology.Edges.Select(e=>brain.Manifest.Neurons[e.Source].ModuleId).ToArray();
        painModules=brain.Manifest.Modules.Where(m=>m.Kind==ModuleKind.Pain).Select(m=>(m,brain.Model.Topology.Edges.Select((e,i)=>(e,i))
            .Where(x=>brain.Manifest.Neurons[x.e.Source].ModuleId==m.Id&&!brain.Manifest.Neurons[x.e.Destination].Boundary).Select(x=>x.i).ToArray())).ToArray();
        rewardGroups=brain.Manifest.RewardGroups.Select(g=>(g,brain.Manifest.Neurons.Select((n,i)=>(n,i)).Where(x=>x.n.ModuleId==g.RewardModuleId).ElementAt(g.RewardNeuron).i)).ToArray();
    }
    public void Step(float[] inputs,double dt,int activitySteps=1,float activityStep=.04f,float weightStep=.02f,float rate=.00001f,IReadOnlyDictionary<string,float>? factorOverrides=null,bool learn=true,IReadOnlyDictionary<int,float>? clamps=null,float learningBias=0)
    {
        Generation.Require(double.IsFinite(dt)&&dt>0&&double.IsFinite(Time+dt),"dt must be finite and positive.");
        Generation.Require(inputs.Length==Brain.Model.Topology.InputCount&&inputs.All(v=>float.IsFinite(v)&&v>=0),"Invalid external input shape or value.");
        Generation.Require(activitySteps>0&&activitySteps<=1024&&float.IsFinite(rate)&&float.IsFinite(weightStep)&&weightStep>=0,"Invalid integration parameters.");
        Generation.Require(float.IsFinite(learningBias)&&learningBias>=0,"Invalid implicit learning bias.");
        if(clamps is not null)foreach(var (index,value) in clamps)
            Generation.Require(index>=Brain.Model.Topology.InputCount&&index<Brain.Model.NormalCount&&float.IsFinite(value)&&value>=0,"Invalid host clamp.");
        var ns=Brain.Manifest.Neurons;float[] boundary=boundaryBuffer;inputs.CopyTo(boundary,0);
        for(int i=0;i<boundary.Length;i++)
        {
            var module=boundaryModules[i];
            if(module.Kind==ModuleKind.Clock)boundary[i]=module.Amplitude*.5f*(1+MathF.Sin((float)(2*Math.PI*module.Frequency*Time+module.Phase)));
            boundary[i]=Math.Clamp(boundary[i],0,ns[i].ActivationCap);
        }
        for(int tick=0;tick<activitySteps;tick++)
        {
            double tickTime=Time+dt*tick/activitySteps;
            for(int i=0;i<boundary.Length;i++)
            {
                var module=boundaryModules[i];
                if(module.Kind==ModuleKind.Clock)boundary[i]=Math.Clamp(module.Amplitude*.5f*(1+MathF.Sin((float)(2*Math.PI*module.Frequency*tickTime+module.Phase))),0,ns[i].ActivationCap);
            }
            if(clamps is not null)
            {
                var held=Backend.CaptureState();
                foreach(var (index,value) in clamps)held.Normal[index]=Math.Clamp(value,0,ns[index].ActivationCap);
                Backend.RestoreState(held);
            }
            Backend.Step(boundary,1,activityStep,0,0,false);
            var state=Backend.CaptureState();
            for(int i=0;i<ns.Count;i++)state.Normal[i]=Math.Min(state.Normal[i],ns[i].ActivationCap);
            if(clamps is not null)foreach(var (index,value) in clamps)state.Normal[index]=Math.Clamp(value,0,ns[index].ActivationCap);
            foreach(var (module,edges) in painModules)
            {
                var held=pain.GetValueOrDefault(module.Id);
                if(module.Frequency<=0)continue;
                if(tickTime>=held.Next){held=(tickTime+1/module.Frequency,(float)random[module.Id].NextDouble());pain[module.Id]=held;randomDraws[module.Id]=randomDraws.GetValueOrDefault(module.Id)+1;}
                foreach(int index in edges)
                {
                    var edge=Brain.Model.Topology.Edges[index];
                    state.Normal[edge.Destination]=Math.Clamp(state.Normal[edge.Destination]+state.Normal[edge.Source]*held.Value*(float)(dt/activitySteps),0,ns[edge.Destination].ActivationCap);
                }
            }
            Backend.RestoreState(state);
        }
        var current=Backend.CaptureState();var multipliers=new Dictionary<string,double>();rewardDerivatives.Clear();groupFactors.Clear();
        foreach(var (group,index) in rewardGroups)
        {
            double reward=current.Normal[index];double derivative=previousRewards.TryGetValue(group.Name,out double old)?(reward-old)/dt:0;
            previousRewards[group.Name]=reward;rewardDerivatives[group.Name]=derivative;
            double factor=factorOverrides is not null&&factorOverrides.TryGetValue(group.Name,out float constant)?constant:derivative;
            Generation.Require(double.IsFinite(factor),"Invalid reward factor.");groupFactors[group.Name]=factor;
            foreach(string id in group.ModuleIds)multipliers[id]=factor;
        }
        if(learn&&!Brain.Model.IsFrozen)
        {
            float[] next=nextWeights;
            for(int e=0;e<next.Length;e++)
            {
                var edge=Brain.Model.Topology.Edges[e];double delta=weightStep*rate*Brain.Model.Plasticity[e]*(current.ErrorRates[2*edge.Source]-current.ErrorRates[2*edge.Source+1])*current.Normal[edge.Destination]*(rewardEdges[e]?0:multipliers.GetValueOrDefault(edgeModules[e],1)+learningBias);
                double value=Math.Clamp(Brain.Model.Weights[e]+delta,-Brain.Model.WeightLimit,Brain.Model.WeightLimit);Generation.Require(double.IsFinite(value)&&Math.Abs(value)<=float.MaxValue,"Nonfinite weight update.");next[e]=(float)value;
            }
            next.CopyTo(Brain.Model.Weights,0);
            Backend.UploadParameters();
        }
        Time+=dt;
    }
    public double MeasureRewardError()
    {
        var state=Backend.CaptureState();double sum=0;
        for(int i=0;i<Brain.Manifest.Neurons.Count;i++)if(rewardModules.Contains(Brain.Manifest.Neurons[i].ModuleId))
        {double error=state.ErrorRates[2*i]-state.ErrorRates[2*i+1];sum+=error*error;}
        return sum;
    }
    /// <summary>Teach only reward-path parameters with clamped labels, then restore free inference.
    /// The teaching state never becomes the perceived reward derivative or motor state.</summary>
    public double TeachRewards(float[] inputs,double dt,IReadOnlyDictionary<int,float> targets,int steps,float h,float weightStep,float rate,IReadOnlyDictionary<int,float>? motorClamps=null)
    {
        if(targets.Count==0)return 0;
        foreach(var (index,value) in targets)
            Generation.Require(index>=0&&index<Brain.Model.NormalCount&&Brain.Manifest.Modules.Single(m=>m.Id==Brain.Manifest.Neurons[index].ModuleId).Kind==ModuleKind.Reward&&float.IsFinite(value)&&value>=0,"Invalid reward teaching target.");
        var activeRewardModules=targets.Keys.Select(i=>Brain.Manifest.Neurons[i].ModuleId).ToHashSet();
        var inactiveRewardModules=Brain.Manifest.Modules.Where(m=>m.Kind==ModuleKind.Reward&&!activeRewardModules.Contains(m.Id)).Select(m=>m.Id).ToHashSet();
        var free=CaptureState();var derivatives=new Dictionary<string,double>(rewardDerivatives);var factors=new Dictionary<string,double>(groupFactors);
        var clamps=motorClamps is null?new Dictionary<int,float>():new Dictionary<int,float>(motorClamps);
        foreach(var item in targets)clamps[item.Key]=item.Value;
        double loss;
        try
        {
            Step(inputs,dt,steps,h,weightStep,rate,learn:false,clamps:clamps);
            var taught=Backend.CaptureState();loss=MeasureRewardError();
            if(!Brain.Model.IsFrozen)
            for(int e=0;e<rewardEdges.Length;e++)if(rewardEdges[e])
            {
                var edge=Brain.Model.Topology.Edges[e];
                if(inactiveRewardModules.Contains(Brain.Manifest.Neurons[edge.Source].ModuleId)||inactiveRewardModules.Contains(Brain.Manifest.Neurons[edge.Destination].ModuleId))continue;
                double delta=weightStep*rate*Brain.Model.Plasticity[e]*(taught.ErrorRates[2*edge.Source]-taught.ErrorRates[2*edge.Source+1])*taught.Normal[edge.Destination];
                double value=Math.Clamp(Brain.Model.Weights[e]+delta,-Brain.Model.WeightLimit,Brain.Model.WeightLimit);
                Generation.Require(double.IsFinite(value)&&Math.Abs(value)<=float.MaxValue,"Nonfinite reward teaching update.");Brain.Model.Weights[e]=(float)value;
            }
        }
        finally
        {
            RestoreState(free);rewardDerivatives.Clear();groupFactors.Clear();
            foreach(var pair in derivatives)rewardDerivatives.Add(pair.Key,pair.Value);
            foreach(var pair in factors)groupFactors.Add(pair.Key,pair.Value);
            Backend.UploadParameters();
        }
        return loss;
    }
    public void Dispose()=>Backend.Dispose();
    public BrainRuntimeState CaptureState()=>new(Time,new(previousRewards),new(pain.ToDictionary(p=>p.Key,p=>new HeldBrainPain(p.Value.Next,p.Value.Value))),Backend.CaptureState(),new(randomDraws));
    public void RestoreState(BrainRuntimeState state)
    {
        Generation.Require(double.IsFinite(state.Time)&&state.Time>=0,"Invalid brain time.");
        Time=state.Time;previousRewards.Clear();pain.Clear();
        foreach(var item in state.PreviousRewards)previousRewards.Add(item.Key,item.Value);
        foreach(var item in state.Pain)pain.Add(item.Key,(item.Value.Next,item.Value.Value));
        foreach(var module in Brain.Manifest.Modules)
        {
            long draws=state.RandomDraws?.GetValueOrDefault(module.Id)??0;
            Generation.Require(draws>=0,"Invalid pain random state.");
            if(!randomDraws.TryGetValue(module.Id,out long currentDraws)||currentDraws!=draws)
            {
                random[module.Id]=new(module.Seed);
                for(long i=0;i<draws;i++)random[module.Id].NextDouble();
            }
            randomDraws[module.Id]=draws;
        }
        Backend.RestoreState(state.Network);
    }
}
public sealed record HeldBrainPain(double Next,float Value);
public sealed record BrainRuntimeState(double Time,Dictionary<string,double> PreviousRewards,Dictionary<string,HeldBrainPain> Pain,PredictiveCodingState Network,Dictionary<string,long>? RandomDraws=null);

/// <summary>One running complex sum per logarithmic band, with about two periods per window.</summary>
public sealed class SlidingAudioDft
{
    private readonly float[][] windows;
    private readonly int[] cursors;
    private readonly Complex[] sums, rotations, removals;
    public float[] Frequencies { get; }
    public SlidingAudioDft(int sampleRate,float minFrequency,float maxFrequency,int bands)
    {
        Generation.Require(sampleRate>0&&float.IsFinite(minFrequency)&&float.IsFinite(maxFrequency)&&minFrequency>0&&maxFrequency>=minFrequency&&maxFrequency<sampleRate/2f&&bands>0&&bands<=4096,"Invalid DFT settings.");
        Frequencies=Enumerable.Range(0,bands).Select(i=>minFrequency*MathF.Pow(maxFrequency/minFrequency,bands==1?0:(float)i/(bands-1))).ToArray();
        windows=new float[bands][];cursors=new int[bands];sums=new Complex[bands];rotations=new Complex[bands];removals=new Complex[bands];
        long total=0;
        for(int i=0;i<bands;i++)
        {
            double required=Math.Ceiling(2d*sampleRate/Frequencies[i]);Generation.Require(required<=2000000,"Audio window exceeds limit.");int length=Math.Max(2,(int)required);total+=length;Generation.Require(total<=8000000,"Audio windows exceed memory limit.");
            windows[i]=new float[length];double angle=-2*Math.PI*Frequencies[i]/sampleRate;rotations[i]=Complex.FromPolarCoordinates(1,angle);removals[i]=Complex.FromPolarCoordinates(1,angle*length);
        }
    }
    public void Push(float sample)
    {
        Generation.Finite(sample);
        for(int i=0;i<windows.Length;i++)
        {
            float old=windows[i][cursors[i]];windows[i][cursors[i]]=sample;cursors[i]=(cursors[i]+1)%windows[i].Length;
            sums[i]=rotations[i]*sums[i]+sample-old*removals[i];
        }
    }
    public float Magnitude(int band)=>(float)(2*sums[band].Magnitude/windows[band].Length);
}
