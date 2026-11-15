namespace NeuralGraph;

public sealed partial class CpuBackend
{
    private readonly float[] stateA, stateB;
    private int stateBank;
    public long PropagationSteps { get; private set; }
    public void Advance(ReadOnlySpan<float> inputs,Span<float> outputs,int steps=1)
    {
        var t=model.Topology;
        if(inputs.Length!=t.InputCount || outputs.Length!=t.Outputs.Length)throw new ArgumentException("Continuous input/output shape.");
        var x=inputRates.AsSpan(0,t.EncodedInputCount); var y=outputRates.AsSpan(0,t.EncodedOutputCount);
        RateEncoding.Encode(inputs,x); AdvanceEncoded(x,y,steps); RateEncoding.Decode(y,outputs);
    }
    public void AdvanceEncoded(ReadOnlySpan<float> inputs,Span<float> outputs,int steps=1)
    {
        var t=model.Topology;
        if(inputs.Length!=t.EncodedInputCount || outputs.Length!=t.EncodedOutputCount || steps<1 || steps>1024)throw new ArgumentException("Continuous input/output shape or step count.");
        RateEncoding.Validate(inputs);
        for(int tick=0;tick<steps;tick++)
        {
            float[] from=stateBank==0?stateA:stateB,to=stateBank==0?stateB:stateA;
            inputs.CopyTo(to);
            for(int node=t.InputCount;node<t.NodeCount;node++)
            {
                float potential=model.Parameters[t.Edges.Length+node-t.InputCount];
                for(int k=t.IncomingOffsets[node];k<t.IncomingOffsets[node+1];k++)
                {
                    int edge=t.IncomingEdges[k],source=t.Edges[edge].Source;
                    float value=source<t.InputCount ? inputs[2*source]-inputs[2*source+1] : RateEncoding.Value(from,source);
                    potential+=gain*model.Parameters[edge]*value;
                }
                RateEncoding.Activate(to,node,potential,t.IsOutput[node]);
            }
            stateBank=1-stateBank;
        }
        var state=stateBank==0?stateA:stateB;
        for(int i=0;i<t.Outputs.Length;i++)state.AsSpan(2*t.Outputs[i],2).CopyTo(outputs.Slice(2*i,2));
        PropagationSteps+=steps;
    }
    public void ReadStateActivations(Span<float> destination)
    {
        if(destination.Length!=model.Topology.ActivationCount)throw new ArgumentException("State activation shape.");
        (stateBank==0?stateA:stateB).CopyTo(destination);
    }
    public void RestoreStateActivations(ReadOnlySpan<float> state,long propagationSteps=0)
    {
        if(state.Length!=model.Topology.ActivationCount || propagationSteps<0)throw new ArgumentException("State activation shape or step count.");
        RateEncoding.Validate(state); state.CopyTo(stateA);state.CopyTo(stateB);stateBank=0;PropagationSteps=propagationSteps;
    }
    public void ResetState() { Array.Clear(stateA);Array.Clear(stateB);stateBank=0;PropagationSteps=0; }
}
