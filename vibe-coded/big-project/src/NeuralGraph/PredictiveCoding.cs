namespace NeuralGraph;

/// <summary>A normal neuron and a signed error pair per graph node. One shared w/-w per edge.</summary>
public sealed class PredictiveCodingModel
{
    public GraphTopology Topology { get; }
    public int NormalCount=>Topology.NodeCount;
    public int ErrorChannelCount=>2*Topology.NodeCount;
    public int PhysicalActivationCount=>3*Topology.NodeCount;
    public int ParameterCount=>Weights.Length;
    public float[] Weights { get; }
    public float[] Plasticity { get; }
    /// <summary>Runtime bound; maze checkpoints persist it in their configuration.</summary>
    public float WeightLimit { get; private set; }=float.PositiveInfinity;
    public float ActivationLimit { get; private set; }=float.PositiveInfinity;
    public bool IsFrozen { get; private set; }
    public PredictiveCodingModel(GraphTopology topology,int seed=1)
    {
        Topology=topology;Weights=new float[topology.Edges.Length];Plasticity=Enumerable.Repeat(1f,Weights.Length).ToArray();
        var rng=new Random(seed);
        for(int e=0;e<Weights.Length;e++)
        {
            var edge=topology.Edges[e];int fanIn=topology.IncomingOffsets[edge.Destination+1]-topology.IncomingOffsets[edge.Destination];
            Weights[e]=(float)(2*rng.NextDouble()-1)*MathF.Sqrt(3f/Math.Max(1,fanIn));
        }
    }
    public void Freeze()=>IsFrozen=true;
    public void Unfreeze()=>IsFrozen=false;
    public void SetWeightLimit(float limit)
    {
        if(!float.IsFinite(limit)||limit<=0)throw new ArgumentOutOfRangeException(nameof(limit));
        Validate();WeightLimit=limit;
        for(int e=0;e<Weights.Length;e++)Weights[e]=Math.Clamp(Weights[e],-limit,limit);
    }
    public void SetActivationLimit(float limit)
    {
        if(!float.IsFinite(limit)||limit<=0)throw new ArgumentOutOfRangeException(nameof(limit));
        ActivationLimit=limit;
    }
    public void Validate()
    {
        if(Weights.Any(v=>!float.IsFinite(v))||Plasticity.Any(v=>!float.IsFinite(v)||v<0))throw new InvalidDataException("Nonfinite weights or invalid fixed plasticity.");
    }
    public void Save(string path)
    {
        Validate();string full=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using(var writer=new BinaryWriter(File.Create(full+".tmp")))
        {
            var t=Topology;writer.Write(0x314E4350);writer.Write(t.InputCount);writer.Write(t.NodeCount);writer.Write(t.Steps);writer.Write(t.Outputs.Length);writer.Write(t.Edges.Length);writer.Write(IsFrozen);
            foreach(int o in t.Outputs)writer.Write(o);
            foreach(var e in t.Edges){writer.Write(e.Source);writer.Write(e.Destination);}
            foreach(float w in Weights)writer.Write(w);foreach(float p in Plasticity)writer.Write(p);
        }
        File.Move(full+".tmp",full,true);
    }
    public static PredictiveCodingModel Load(string path)
    {
        using var reader=new BinaryReader(File.OpenRead(path));
        if(reader.ReadInt32()!=0x314E4350)throw new InvalidDataException("Not a predictive-coding model.");
        int inputs=reader.ReadInt32(),nodes=reader.ReadInt32(),steps=reader.ReadInt32(),outputs=reader.ReadInt32(),edges=reader.ReadInt32();bool frozen=reader.ReadBoolean();
        if(inputs<1||nodes<=inputs||outputs<1||outputs>nodes||edges<0||edges>(reader.BaseStream.Length-reader.BaseStream.Position)/16)throw new InvalidDataException("Invalid predictive graph dimensions.");
        var os=new int[outputs];for(int i=0;i<outputs;i++)os[i]=reader.ReadInt32();
        var es=new Edge[edges];for(int i=0;i<edges;i++)es[i]=new(reader.ReadInt32(),reader.ReadInt32());
        var model=new PredictiveCodingModel(new(inputs,nodes,steps,os,es));
        for(int i=0;i<edges;i++)model.Weights[i]=reader.ReadSingle();for(int i=0;i<edges;i++)model.Plasticity[i]=reader.ReadSingle();
        if(reader.BaseStream.Position!=reader.BaseStream.Length)throw new InvalidDataException("Trailing predictive model data.");
        model.IsFrozen=frozen;model.Validate();return model;
    }
}

public sealed record PredictiveCodingState(float[] Normal,float[] ErrorRates,long Ticks);

/// <summary>Activity and local plasticity only; signed effective learning rates reverse local plasticity. No global loss optimizer or backpropagation.</summary>
public interface IPredictiveCodingBackend : IDisposable
{
    string DeviceName { get; }
    float Gain { get; set; }
    /// <summary>Set signed output errors to their normal activities instead of integrating them.</summary>
    bool MatchOutputErrorsToNormal { get; set; }
    long PropagationSteps { get; }
    void Step(ReadOnlySpan<float> inputs,int steps,float activityStep,float weightStep,float learningRate,bool learn,ReadOnlySpan<float> outputClamps=default);
    void ReadNormal(Span<float> normal);
    void ReadErrorRates(Span<float> errors);
    PredictiveCodingState CaptureState();
    void RestoreState(PredictiveCodingState state);
    double MeasureTotalError();
    void UploadParameters();
    void DownloadParameters();
    /// <summary>Read selected live weights without downloading or validating the entire model.</summary>
    void ReadWeights(ReadOnlySpan<int> edges,Span<float> destination);
}

internal static class PredictiveValidation
{
    internal static void ClampNormal(Span<float> normal,float limit)
    {
        for(int i=0;i<normal.Length;i++)normal[i]=MathF.Min(normal[i],limit);
    }
    internal static void Step(GraphTopology t,ReadOnlySpan<float> input,int steps,float h,float weightStep,float rate,ReadOnlySpan<float> clamps)
    {
        if(input.Length!=t.InputCount||steps<1||steps>1024||!float.IsFinite(h)||h<=0||h>.1f||!float.IsFinite(weightStep)||weightStep<0||!float.IsFinite(rate)||!float.IsFinite(weightStep*rate)||(!clamps.IsEmpty&&clamps.Length!=t.Outputs.Length))throw new ArgumentException("Invalid predictive integration settings or boundary shape.");
        RateEncoding.Validate(input);RateEncoding.Validate(clamps);
    }
    internal static void State(GraphTopology t,PredictiveCodingState state)
    {
        if(state.Normal.Length!=t.NodeCount||state.ErrorRates.Length!=2*t.NodeCount||state.Ticks<0)throw new ArgumentException("Invalid predictive state shape.");
        RateEncoding.Validate(state.Normal);RateEncoding.Validate(state.ErrorRates);
    }
    internal static void WeightSelection(int count,ReadOnlySpan<int> edges,int destinationLength)
    {
        if(edges.Length!=destinationLength)throw new ArgumentException("Weight selection shape.");
        foreach(int edge in edges)if((uint)edge>=(uint)count)throw new ArgumentOutOfRangeException(nameof(edges));
    }
    internal static double Sum(ReadOnlySpan<float> rates)
    {
        double sum=0;for(int i=0;i<rates.Length;i+=2){double e=(double)rates[i]-rates[i+1];sum+=e*e;}return sum;
    }
}

/// <summary>Reference projected symplectic Euler: errors first, normal activities second, weights once per call.</summary>
public sealed class CpuPredictiveCodingBackend : IPredictiveCodingBackend
{
    private readonly PredictiveCodingModel model;
    private readonly float[] normal,errors;
    private readonly int[] outputMap;
    private float gain=1;
    private bool matchOutputErrorsToNormal;
    public bool MatchOutputErrorsToNormal
    {
        get=>matchOutputErrorsToNormal;
        set{matchOutputErrorsToNormal=value;if(value)SyncOutputErrors();}
    }
    private void SyncOutputErrors(){foreach(int node in model.Topology.Outputs){errors[2*node]=normal[node];errors[2*node+1]=0;}}
    public string DeviceName=>"CPU predictive coding";
    public long PropagationSteps { get; private set; }
    public float Gain { get=>gain;set{if(!float.IsFinite(value)||value<0||value>4)throw new ArgumentOutOfRangeException(nameof(value));gain=value;} }
    public CpuPredictiveCodingBackend(PredictiveCodingModel model)
    {
        this.model=model;model.Validate();var t=model.Topology;normal=new float[t.NodeCount];errors=new float[2*t.NodeCount];outputMap=Enumerable.Repeat(-1,t.NodeCount).ToArray();
        for(int i=0;i<t.Outputs.Length;i++)outputMap[t.Outputs[i]]=i;
    }
    public void Step(ReadOnlySpan<float> inputs,int steps,float activityStep,float weightStep,float learningRate,bool learn,ReadOnlySpan<float> outputClamps=default)
    {
        var t=model.Topology;PredictiveValidation.Step(t,inputs,steps,activityStep,weightStep,learningRate,outputClamps);
        PredictiveValidation.ClampNormal(normal,model.ActivationLimit);
        for(int tick=0;tick<steps;tick++)
        {
            inputs.CopyTo(normal);
            PredictiveValidation.ClampNormal(normal.AsSpan(0,t.InputCount),model.ActivationLimit);
            if(!outputClamps.IsEmpty)for(int i=0;i<t.Outputs.Length;i++)normal[t.Outputs[i]]=MathF.Min(outputClamps[i],model.ActivationLimit);
            for(int i=0;i<t.NodeCount;i++)
            {
                if(matchOutputErrorsToNormal&&outputMap[i]>=0){errors[2*i]=normal[i];errors[2*i+1]=0;continue;}
                float prediction=0;
                for(int k=t.OutgoingOffsets[i];k<t.OutgoingOffsets[i+1];k++){int e=t.OutgoingEdges[k];prediction+=model.Weights[e]*normal[t.Edges[e].Destination];}
                float next=errors[2*i]-errors[2*i+1]+activityStep*(normal[i]-gain*prediction);
                errors[2*i]=MathF.Max(next,0);errors[2*i+1]=MathF.Max(-next,0);
            }
            for(int j=t.InputCount;j<t.NodeCount;j++)
            {
                if(!outputClamps.IsEmpty&&outputMap[j]>=0)continue;
                float incoming=0;
                for(int k=t.IncomingOffsets[j];k<t.IncomingOffsets[j+1];k++){int e=t.IncomingEdges[k],i=t.Edges[e].Source;incoming+=model.Weights[e]*(errors[2*i]-errors[2*i+1]);}
                normal[j]=Math.Clamp(normal[j]+activityStep*(-(errors[2*j]-errors[2*j+1])+gain*incoming),0,model.ActivationLimit);
            }
        }
        if(matchOutputErrorsToNormal)SyncOutputErrors();
        float coefficient=weightStep*learningRate;
        if(learn&&!model.IsFrozen&&coefficient!=0)
            for(int e=0;e<t.Edges.Length;e++){var edge=t.Edges[e];model.Weights[e]=Math.Clamp(model.Weights[e]-coefficient*model.Plasticity[e]*(errors[2*edge.Source]-errors[2*edge.Source+1])*normal[edge.Destination],-model.WeightLimit,model.WeightLimit);}
        PropagationSteps+=steps;RateEncoding.Validate(normal);RateEncoding.Validate(errors);model.Validate();
    }
    public void ReadNormal(Span<float> destination){if(destination.Length!=normal.Length)throw new ArgumentException("Normal shape.");normal.CopyTo(destination);}
    public void ReadErrorRates(Span<float> destination){if(destination.Length!=errors.Length)throw new ArgumentException("Error shape.");errors.CopyTo(destination);}
    public PredictiveCodingState CaptureState()=>new((float[])normal.Clone(),(float[])errors.Clone(),PropagationSteps);
    public void RestoreState(PredictiveCodingState state){PredictiveValidation.State(model.Topology,state);state.Normal.CopyTo(normal,0);PredictiveValidation.ClampNormal(normal,model.ActivationLimit);state.ErrorRates.CopyTo(errors,0);if(matchOutputErrorsToNormal)SyncOutputErrors();PropagationSteps=state.Ticks;}
    public double MeasureTotalError()=>PredictiveValidation.Sum(errors);
    public void UploadParameters()=>model.Validate();
    public void ReadWeights(ReadOnlySpan<int> edges,Span<float> destination)
    {
        PredictiveValidation.WeightSelection(model.Weights.Length,edges,destination.Length);
        for(int i=0;i<edges.Length;i++)destination[i]=model.Weights[edges[i]];
    }
    public void DownloadParameters() { }
    public void Dispose() { }
}
