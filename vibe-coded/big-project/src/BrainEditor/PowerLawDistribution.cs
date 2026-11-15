namespace ArcadeLab.Editor;

public sealed class HubPowerLaw
{
    public int MinDegree {get;set;}=1;
    public int MaxDegree {get;set;}=32;
    public double Exponent {get;set;}=2;
}

/// <summary>Discrete, truncated P(k) proportional to k^(-exponent).
/// The finite-network degree profile uses evenly spaced percentiles, including both
/// endpoints, then shuffles their neuron assignments using the seed.</summary>
public sealed class PowerLawDistribution
{
    private readonly int minimum;
    private readonly double[] cumulative;
    public PowerLawDistribution(HubPowerLaw settings,int availableNeighbors)
    {
        Generation.Require(settings is not null,"Missing hub power-law settings.");
        Generation.Require(settings!.MinDegree>=1&&settings.MaxDegree>=settings.MinDegree,"Power-law degrees require 1 <= minimum <= maximum.");
        Generation.Require(double.IsFinite(settings.Exponent)&&settings.Exponent>=0,"Power-law exponent must be finite and nonnegative.");
        Generation.Require(availableNeighbors>=0,"Invalid available neighbor count.");
        int maximum=Math.Min(settings.MaxDegree,availableNeighbors);
        minimum=Math.Min(settings.MinDegree,maximum);
        cumulative=new double[maximum-minimum+1];
        if(maximum==0){cumulative[0]=1;return;}
        double sum=0;
        for(int i=0;i<cumulative.Length;i++)
        {
            // Scaling by the first degree avoids overflow/underflow of the whole distribution
            // for very large exponents while preserving all relative probabilities.
            sum+=Math.Exp(-settings.Exponent*Math.Log((double)(minimum+i)/minimum));
            cumulative[i]=sum;
        }
        for(int i=0;i<cumulative.Length;i++)cumulative[i]/=sum;
        cumulative[^1]=1;
    }
    public int Sample(Random random)
    {
        return Quantile(random.NextDouble());
    }
    private int Quantile(double sample)
    {
        if(sample>=1)return minimum+cumulative.Length-1;
        int low=0,high=cumulative.Length-1;
        while(low<high)
        {
            int middle=low+(high-low)/2;
            if(sample<cumulative[middle])high=middle;else low=middle+1;
        }
        return minimum+low;
    }
    public int[] Assign(int neuronCount,int seed)
    {
        Generation.Require(neuronCount is >0 and <=Generation.MaxNeurons,"Invalid hub neuron count.");
        var degrees=new int[neuronCount];
        for(int i=0;i<degrees.Length;i++)degrees[i]=Quantile(neuronCount==1?0:(double)i/(neuronCount-1));
        new Random(seed).Shuffle(degrees);
        return degrees;
    }
}
