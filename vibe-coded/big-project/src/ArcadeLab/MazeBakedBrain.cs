using ArcadeLab.Editor;
using NeuralGraph;
using System.Text.Json;

namespace ArcadeLab;

/// <summary>Explicit host bindings for editor sensory panels and the eight maze buttons.</summary>
public sealed class MazeBakedBrain
{
    public BakedBrain Brain { get; }
    public int[] Motors { get; }
    public int[] Rewards { get; }
    public IReadOnlyDictionary<int,string> InputLabels=>inputLabels;
    public IReadOnlyDictionary<int,string> OutputLabels=>outputLabels;
    private readonly Dictionary<int,string> inputLabels=[],outputLabels=[];
    private readonly List<(int Index,string Signal,int Pixel)> inputs=[];
    public MazeBakedBrain(BakedBrain brain)
    {
        Brain=brain;
        int[] Indices(BrainModule module)=>brain.Manifest.Neurons.Select((n,i)=>(n,i))
            .Where(x=>x.n.ModuleId==module.Id).OrderBy(x=>int.TryParse(x.n.Id,out int local)?local:int.MaxValue).ThenBy(x=>x.n.Id,StringComparer.Ordinal).Select(x=>x.i).ToArray();
        var motors=brain.Manifest.Modules.Where(m=>m.Kind==ModuleKind.OutputPanel).ToArray();
        if(motors.Length!=1)throw new InvalidDataException("Maze brain needs exactly one output panel.");
        var motor=motors[0];var ports=Indices(motor);
        if(ports.Length!=MazeSession.OutputCount||ports.Any(i=>!brain.Model.Topology.Outputs.Contains(i)))
            throw new InvalidDataException("Maze output panel needs eight free readout neurons, bound to W, S, A, D, Q, E, F and X.");
        if(motor.ChannelBindings.Length==0)motor.ChannelBindings=MazeKeyBinding.All.Select(k=>k.Key).ToArray();
        if(motor.ChannelBindings.Length!=ports.Length)throw new InvalidDataException("Output channel bindings must match the panel neuron count.");
        Motors=new int[MazeSession.OutputCount];var assigned=new HashSet<string>();
        for(int i=0;i<ports.Length;i++)
        {
            string key=motor.ChannelBindings[i].Trim().ToUpperInvariant();
            var binding=MazeKeyBinding.All.FirstOrDefault(b=>b.Key==key)??throw new InvalidDataException($"Unknown maze output key '{key}'. Use W, S, A, D, Q, E, F, X.");
            if(!assigned.Add(key))throw new InvalidDataException($"Duplicate output key '{key}'.");
            int slot=Array.IndexOf(MazeControlPolicy.Buttons.ToArray(),binding.Control);Motors[slot]=ports[i];
            outputLabels[ports[i]]=$"{key} / {binding.Action}";
        }
        Rewards=brain.Manifest.Modules.Where(m=>m.Kind==ModuleKind.Reward).SelectMany(Indices).ToArray();
        foreach(var module in brain.Manifest.Modules.Where(m=>m.Kind==ModuleKind.InputPanel))
        {
            var indices=Indices(module);
            if(module.ChannelBindings.Length==0)
            {
                bool video=module.Panel==PanelKind.Visual||module.Name.Contains("video",StringComparison.OrdinalIgnoreCase)||module.Name.Contains("visual",StringComparison.OrdinalIgnoreCase);
                bool audio=module.Panel==PanelKind.Audio||module.Name.Contains("audio",StringComparison.OrdinalIgnoreCase);
                bool tactile=module.Panel==PanelKind.Tactile||module.Name.Contains("tactile",StringComparison.OrdinalIgnoreCase);
                module.ChannelBindings=tactile&&indices.Length==MazeGame.TactileCount?Enumerable.Range(0,MazeGame.TactileCount).Select(i=>$"tactile.{i}").ToArray():video&&indices.Length==3?["video.red","video.green","video.blue"]
                    :video&&indices.Length==module.Width*module.Height?Enumerable.Range(0,indices.Length).Select(i=>$"video.pixel.{(i%module.Width)*MazeGame.PixelCount/module.Width}").ToArray()
                    :audio&&indices.Length==2?["audio.left.rms","audio.right.rms"]
                    :indices.Length==1&&(module.Name.Equals("INPUTPANEL",StringComparison.OrdinalIgnoreCase)||module.Name.Contains("pain",StringComparison.OrdinalIgnoreCase))?["body.pain"]
                    :throw new InvalidDataException($"Input panel '{module.Name}' needs explicit ChannelBindings (video.red/green/blue, video.pixel.N, audio.left/right.rms, body.pain/pleasure, body.health).");
            }
            if(module.ChannelBindings.Length!=indices.Length)throw new InvalidDataException($"Input panel '{module.Name}' bindings must match its neuron count.");
            for(int i=0;i<indices.Length;i++)
            {
                string signal=module.ChannelBindings[i].Trim().ToLowerInvariant();int pixel=-1;
                bool valid=signal is "video.red" or "video.green" or "video.blue" or "audio.left.rms" or "audio.right.rms" or "body.pain" or "body.pleasure" or "body.health" or "body.ammo" or "body.reserve";
                if(signal.StartsWith("video.pixel.",StringComparison.Ordinal))valid=int.TryParse(signal[12..],out pixel)&&pixel>=0&&pixel<MazeGame.PixelCount;
                if(signal.StartsWith("video.rgb.",StringComparison.Ordinal))valid=int.TryParse(signal[10..],out pixel)&&pixel>=0&&pixel<MazeVision.FrameSize;
                if(signal.StartsWith("tactile.",StringComparison.Ordinal))valid=int.TryParse(signal[8..],out pixel)&&pixel>=0&&pixel<MazeGame.TactileCount;
                if(!valid)throw new InvalidDataException($"Unknown maze input signal '{signal}' in '{module.Name}'.");
                if(indices[i]>=brain.Model.Topology.InputCount)throw new InvalidDataException("Input binding must target a clamped boundary neuron.");
                inputs.Add((indices[i],signal,pixel));inputLabels[indices[i]]=signal;
            }
        }
        foreach(var module in brain.Manifest.Modules.Where(m=>m.Kind==ModuleKind.Clock))
            foreach(int index in Indices(module))inputLabels[index]=$"clock / {module.Name} ({module.Frequency} Hz)";
        if(inputLabels.Count!=brain.Model.Topology.InputCount)throw new InvalidDataException("Every external brain input must have a host signal or clock binding.");
    }
    public float[] Inputs(MazeGame game)
    {
        var boundary=new float[Brain.Model.Topology.InputCount];
        float[] rgb=new float[3];
        for(int pixel=0;pixel<MazeGame.PixelCount;pixel++)for(int c=0;c<3;c++)rgb[c]+=game.RgbPixels[3*pixel+c]/MazeGame.PixelCount;
        float left=Rms(game.Audio),right=Rms(game.AudioRight);
        foreach(var (index,signal,pixel) in inputs)
            boundary[index]=signal switch
            {
                "video.red"=>rgb[0],"video.green"=>rgb[1],"video.blue"=>rgb[2],
                "audio.left.rms"=>left,"audio.right.rms"=>right,"body.pain"=>game.PrimitivePain,
                "body.pleasure"=>game.PrimitivePleasure,"body.health"=>game.Health/100,"body.ammo"=>game.Ammo/8f,"body.reserve"=>game.Reserve/96f,
                _=>signal.StartsWith("video.rgb.",StringComparison.Ordinal)?game.RgbPixels[pixel]:signal.StartsWith("tactile.",StringComparison.Ordinal)?game.Tactile[pixel]:game.Pixels[pixel]
            };
        return boundary;
    }
    private static float Rms(float[] samples)=>MathF.Sqrt(samples.Sum(v=>v*v)/samples.Length);
    public static string Latest(string root)
    {
        string parent=Path.Combine(root,"src","BrainEditor","artifacts","editor");
        return Directory.Exists(parent)?Directory.EnumerateDirectories(parent,"baked-*").Where(p=>File.Exists(Path.Combine(p,"manifest.json"))&&File.Exists(Path.Combine(p,"network.pcn"))).OrderDescending(StringComparer.Ordinal).FirstOrDefault()
            ??throw new FileNotFoundException("No complete baked editor network found."):throw new DirectoryNotFoundException(parent);
    }
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        Brain.Model.Save(Path.Combine(directory,"network.pcn"));
        File.WriteAllText(Path.Combine(directory,"manifest.json"),JsonSerializer.Serialize(Brain.Manifest,BrainDocument.Json));
    }
}
