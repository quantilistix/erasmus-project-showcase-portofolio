using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArcadeLab.Editor;

public enum ModuleKind { Hub, Lattice, Layered, InputPanel, OutputPanel, Pain, Clock, Reward, Custom }
[Flags] public enum NeuronRole { Input = 1, Hidden = 2, Output = 4, All = 7 }
public enum SelectionKind { Uniform, HighestDegree, DegreeWeighted }
public enum GroupKind { Input, Hidden, Output, Other }
public enum GroupCriterion { Role, Layer, IndexRange, SpatialBox, ExplicitIds, All }
public enum EndpointScope { LegacyRoles, AllNeurons, Groups }
public enum LinkPattern { AllToAll, OneToOne, Nearest, FanOut }
public enum LinkWeightKind { Random, Constant }
public enum LatticeShape { Flat, Spherical, Hyperbolic, Custom }
public enum PanelKind { Visual, Audio, Controls, Other, Tactile }
public readonly record struct Position(float X, float Y, float Z)
{
    public static Position operator +(Position a, Position b) => new(a.X+b.X,a.Y+b.Y,a.Z+b.Z);
    public static Position operator *(Position a, float b) => new(a.X*b,a.Y*b,a.Z*b);
}
public sealed class DegreeBand { public int Degree { get; set; } = 4; public double Fraction { get; set; } = 1; }
public sealed class CrossSettings { public double SourcePercent { get; set; } = 100; public double DestinationPercent { get; set; } = 100; }
public sealed class ArrayModifier
{
    public bool Enabled { get; set; } = true;
    public int Dimensions { get; set; } = 2;
    public int[] AxisSizes { get; set; } = [2,2];
    public float Spacing { get; set; } = 8;
    public LatticeShape Shape { get; set; }
    public List<Position> CustomPositions { get; set; } = [];
}
public sealed class CustomNeuron
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public NeuronRole Role { get; set; } = NeuronRole.Hidden;
    public Position Position { get; set; }
}
public sealed class CustomEdge
{
    public string Source { get; set; } = "";
    public string Destination { get; set; } = "";
    public float Weight { get; set; } = .1f;
    public float Plasticity { get; set; } = 1;
}
public sealed class BrainModule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Module";
    public ModuleKind Kind { get; set; } = ModuleKind.Layered;
    public int Seed { get; set; } = 42;
    public Position Position { get; set; }
    public Position Rotation { get; set; } // XYZ Euler degrees, applied about the module origin.
    public List<NeuronGroup> Groups { get; set; } = NeuronGroups.Defaults();
    public int NeuronCount { get; set; } // Zero uses the legacy Inputs + Hidden + Outputs count.
    public int Inputs { get; set; } = 8;
    public int Hidden { get; set; } = 24;
    public int Outputs { get; set; } = 4;
    public float WeightScale { get; set; } = .2f;
    public float Plasticity { get; set; } = 1;
    public HubPowerLaw PowerLaw { get; set; } = new();
    // Retained for reading old projects; hub generation now uses PowerLaw.
    public List<DegreeBand> DegreeDistribution { get; set; } = [new()];
    public int Dimensions { get; set; } = 2;
    public int[] AxisSizes { get; set; } = [];
    public int Neighbors { get; set; } = 4;
    public LatticeShape Shape { get; set; }
    public List<Position> CustomPositions { get; set; } = [];
    public double Imperfection { get; set; }
    public int[] LayerSizes { get; set; } = [8,16,16,4];
    public List<CrossSettings> Crosses { get; set; } = [];
    public int ResidualStep { get; set; } = 3;
    public int ResidualDensity { get; set; }
    public bool FixedResidual { get; set; } = true;
    /// <summary>Host signal or key name per panel channel, in local neuron ID order.</summary>
    public string[] ChannelBindings { get; set; } = [];
    public PanelKind Panel { get; set; } = PanelKind.Other;
    public int Width { get; set; } = 8;
    public int Height { get; set; } = 8;
    public float ResolutionDecay { get; set; } = 1;
    public int Channels { get; set; } = 8;
    public float MinFrequency { get; set; } = 80;
    public float MaxFrequency { get; set; } = 8000;
    public int SampleRate { get; set; } = 48000;
    public float Frequency { get; set; } = 1;
    public float Amplitude { get; set; } = 1;
    public float Phase { get; set; }
    public List<CustomNeuron> Neurons { get; set; } = [];
    public List<CustomEdge> Edges { get; set; } = [];
    public List<ArrayModifier> Modifiers { get; set; } = [];
}
public sealed class LinkEndpoint
{
    public string ModuleId { get; set; } = "";
    public NeuronRole Roles { get; set; } = NeuronRole.All;
    public List<string> GroupIds { get; set; } = [];
    public EndpointScope Scope { get; set; } // Existing documents retain their role-based routes.
    public double Percent { get; set; } = 100;
    public SelectionKind Selection { get; set; }
    public double DegreeExponent { get; set; } = 1;
}
public sealed class BrainLink
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Link";
    public LinkEndpoint Source { get; set; } = new();
    public LinkEndpoint Destination { get; set; } = new();
    public LinkPattern Pattern { get; set; }
    public int FanOut { get; set; } = 4;
    public LinkWeightKind WeightKind { get; set; }
    public float Weight { get; set; } = .1f;
    public double CrossPercent { get; set; } = 100;
    public int Seed { get; set; } = 42;
    public float WeightScale { get; set; } = .2f;
    public float Plasticity { get; set; } = 1;
}
public sealed class RewardGroup
{
    public string Name { get; set; } = "Reward group";
    public List<string> ModuleIds { get; set; } = [];
    public string RewardModuleId { get; set; } = "";
    public int RewardNeuron { get; set; }
}
public sealed class BrainDocument
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Untitled brain";
    public List<BrainModule> Modules { get; set; } = [];
    public List<BrainLink> Links { get; set; } = [];
    public List<RewardGroup> RewardGroups { get; set; } = [];
    public Dictionary<string,EditorViewState> EditorViews {get;set;}=[];
    public EditorViewState GetView(string key)
    {
        EditorViews??=[];
        if(!EditorViews.TryGetValue(key,out var state)||state is null||!state.IsValid())EditorViews[key]=state=new();
        return state;
    }
    public static JsonSerializerOptions Json { get; } = new() { WriteIndented=true, Converters={new JsonStringEnumConverter()} };
    public string Serialize() => JsonSerializer.Serialize(this,Json);
    public static BrainDocument Parse(string text)
    {
        var doc=JsonSerializer.Deserialize<BrainDocument>(text,Json) ?? throw new InvalidDataException("Empty brain document.");
        if(doc.Version!=1)throw new InvalidDataException("Unsupported brain document version.");
        return doc;
    }
    public void Save(string path)
    {
        string full=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full+".tmp",Serialize());File.Move(full+".tmp",full,true);
    }
    public static BrainDocument Example()
    {
        var input=new BrainModule { Name="SENSORY",Kind=ModuleKind.InputPanel,Channels=8,Position=new(-10,0,0) };
        var core=new BrainModule { Name="CORTEX",Position=new(0,0,0) };
        var output=new BrainModule { Name="MOTOR",Kind=ModuleKind.OutputPanel,Channels=4,Position=new(12,0,0) };
        return new() { Modules=[input,core,output],Links=[
            new() { Source=new() { ModuleId=input.Id },Destination=new() { ModuleId=core.Id,Roles=NeuronRole.Input } },
            new() { Source=new() { ModuleId=core.Id,Roles=NeuronRole.Output },Destination=new() { ModuleId=output.Id } }] };
    }
}
