using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArcadeLab;

namespace ArcadeLab.Editor;

/// <summary>Single-client, newline-delimited JSON-RPC MCP server. No window or network listener.</summary>
public sealed class BrainMcpServer : IDisposable
{
    private const string Protocol="2025-11-25";
    private readonly string root;
    private MazeSession? session;
    private bool initialized,ready;
    private static readonly JsonSerializerOptions Wire=new() {PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
    public BrainMcpServer(string workspace)
    {
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if(!Directory.Exists(root))throw new DirectoryNotFoundException(root);
    }
    public void Run(TextReader input,TextWriter output)
    {
        string? line;
        while((line=input.ReadLine()) is not null)
        {
            var response=Handle(line);
            if(response is not null){output.WriteLine(response);output.Flush();}
        }
    }
    public string? Handle(string line)
    {
        object? id=null;
        try
        {
            using var document=JsonDocument.Parse(line);var request=document.RootElement;
            if(request.ValueKind!=JsonValueKind.Object||!request.TryGetProperty("jsonrpc",out var version)||version.GetString()!="2.0"
                ||!request.TryGetProperty("method",out var methodValue)||methodValue.ValueKind!=JsonValueKind.String)
                return Error(null,-32600,"Expected a JSON-RPC 2.0 request.");
            bool hasId=request.TryGetProperty("id",out var rawId);
            if(hasId)
            {
                if(rawId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))return Error(null,-32600,"Invalid request ID.");
                id=rawId.Clone();
            }
            string method=methodValue.GetString()!;
            var parameters=request.TryGetProperty("params",out var p)?p:JsonSerializer.SerializeToElement(new {});
            if(!hasId)
            {
                if(method=="notifications/initialized"&&initialized)ready=true;
                return null;
            }
            if(method=="ping")return Result(id,new {});
            if(method=="initialize")
            {
                if(initialized)return Error(id,-32600,"Already initialized.");
                string requested=String(parameters,"protocolVersion");initialized=true;
                return Result(id,new {protocolVersion=requested is "2024-11-05" or "2025-03-26" or "2025-06-18" or Protocol?requested:Protocol,
                    capabilities=new {tools=new {listChanged=false}},serverInfo=new {name="brain-editor",version="1.0.0"},
                    instructions="Edit and bake BrainEditor files and run one bounded headless maze session. Paths are relative to the configured workspace. Project writes require the revision returned by brain_read. Changes on disk are loaded into the GUI when reopened; this server does not control an existing GUI window. maze_step advances simulation; no training runs between calls."});
            }
            if(!ready)return Error(id,-32002,"Initialize and send notifications/initialized first.");
            if(method=="tools/list")return Result(id,new {tools=Definitions()});
            if(method!="tools/call")return Error(id,-32601,"Unknown method.");
            string name=String(parameters,"name");
            if(!Names.Contains(name))return Error(id,-32602,"Unknown tool.");
            var arguments=parameters.TryGetProperty("arguments",out var a)?a:JsonSerializer.SerializeToElement(new {});
            try
            {
                if(arguments.ValueKind!=JsonValueKind.Object)throw new ArgumentException("arguments must be an object.");
                string[] allowed=name switch
                {
                    "brain_read" or "brain_create_maze" or "brain_validate"=>["path"],
                    "brain_write"=>["path","revision","document"],"brain_bake"=>["path","directory"],
                    "maze_start"=>["directory","training","adaptive","seed"],"maze_step"=>["frames"],"maze_save"=>["directory"],_=>[]
                };
                if(arguments.EnumerateObject().Any(p=>!allowed.Contains(p.Name)))throw new ArgumentException("Unknown tool argument.");
                object value=Call(name,arguments);
                return Result(id,new {content=new[]{new {type="text",text=JsonSerializer.Serialize(value,Wire)}},isError=false});
            }
            catch(Exception e) when(e is not OutOfMemoryException)
            {return Result(id,new {content=new[]{new {type="text",text=e.Message}},isError=true});}
        }
        catch(JsonException e){return Error(id,-32700,e.Message);}
        catch(Exception e) when(e is not OutOfMemoryException){return Error(id,-32602,e.Message);}
    }
    private static readonly string[] Names=["brain_read","brain_write","brain_create_maze","brain_validate","brain_bake","maze_start","maze_step","maze_status","maze_save","maze_stop"];
    private static object[] Definitions()
    {
        object S(string description)=>new {type="string",description};
        object Tool(string name,string description,object properties,string[] required,bool readOnly=false)=>new {name,description,
            inputSchema=new {type="object",properties,required,additionalProperties=false},
            annotations=new {readOnlyHint=readOnly,destructiveHint=!readOnly,idempotentHint=readOnly,openWorldHint=false}};
        var path=S("Workspace-relative path.");
        return [
            Tool("brain_read","Read a project, including its complete editable JSON and revision hash.",new {path},["path"],true),
            Tool("brain_write","Validate and atomically save a complete project. Existing files require their current revision; new files require an empty revision.",new {path,revision=S("SHA-256 from brain_read, or empty for a new file."),document=new {type="object",description="Complete BrainDocument JSON, including modules, links and reward groups."}},["path","revision","document"]),
            Tool("brain_create_maze","Create a connected maze-ready sensory/cortex/motor/reward project in a new file.",new {path},["path"]),
            Tool("brain_validate","Compile a project and report neuron/edge counts and whether maze bindings are valid.",new {path},["path"],true),
            Tool("brain_bake","Bake a project into a new directory containing network.pcn and manifest.json.",new {path,directory=path},["path","directory"]),
            Tool("maze_start","Start one CPU maze session from a bake or checkpoint. Training defaults to true and adaptive control to true for new bakes. Stop the existing session first.",new {directory=path,training=new {type="boolean",@default=true},adaptive=new {type="boolean"},seed=new {type="integer",@default=42}},["directory"]),
            Tool("maze_step","Advance 1–600 frames (up to a two-second compute slice) and return actual frames advanced and learning diagnostics.",new {frames=new {type="integer",minimum=1,maximum=600,@default=60}},[]),
            Tool("maze_status","Read current maze learning metrics and reward components without advancing the simulation.",new {},[],true),
            Tool("maze_save","Save the current brain and learned action policy into a NEW checkpoint directory.",new {directory=path},["directory"]),
            Tool("maze_stop","Dispose the current session. Save first to retain changes.",new {},[])
        ];
    }
    private object Call(string name,JsonElement a)
    {
        switch(name)
        {
            case "brain_read":
            {
                string path=PathFor(String(a,"path"));string text=File.ReadAllText(path);var doc=BrainDocument.Parse(text);
                return new {path,revision=Revision(text),document=JsonSerializer.SerializeToElement(doc,BrainDocument.Json)};
            }
            case "brain_write":
            {
                string path=PathFor(String(a,"path")),revision=String(a,"revision");
                if((File.Exists(path)?Revision(File.ReadAllText(path)):"")!=revision)throw new InvalidOperationException("Project changed on disk. Read it again before editing.");
                var doc=BrainDocument.Parse(a.GetProperty("document").GetRawText());_ = Baking.Bake(doc);
                if((File.Exists(path)?Revision(File.ReadAllText(path)):"")!=revision)throw new InvalidOperationException("Project changed during validation. Read it again.");
                AtomicWrite(path,doc.Serialize());return new {path,revision=Revision(doc.Serialize())};
            }
            case "brain_create_maze":
            {
                string path=PathFor(String(a,"path"));if(File.Exists(path))throw new IOException("Choose a new project path.");
                var doc=MazeBrainTemplate.Create();_ = Baking.Bake(doc);AtomicWrite(path,doc.Serialize());
                return new {path,revision=Revision(doc.Serialize())};
            }
            case "brain_validate":
            case "brain_bake":
            {
                string path=PathFor(String(a,"path"));var brain=Baking.Bake(BrainDocument.Parse(File.ReadAllText(path)));
                string? mazeError=null;try{_ = new MazeBakedBrain(brain);}catch(Exception e){mazeError=e.Message;}
                string? directory=null;if(name=="brain_bake"){directory=PathFor(String(a,"directory"));brain.Save(directory);}
                return new {neurons=brain.Model.NormalCount,edges=brain.Model.ParameterCount,mazeCompatible=mazeError is null,mazeError,directory};
            }
            case "maze_start":
            {
                if(session is not null)throw new InvalidOperationException("Save/stop the current session first.");
                string directory=PathFor(String(a,"directory"));bool training=Boolean(a,"training",true);
                foreach(string file in new[]{"brain-maze.json","maze.json","manifest.json","network.pcn","main.pcn","reward.pcn"})_ = PathFor(Path.Combine(directory,file));
                bool? adaptive=a.TryGetProperty("adaptive",out var flag)?flag.GetBoolean():null;
                if(File.Exists(Path.Combine(directory,"brain-maze.json"))||File.Exists(Path.Combine(directory,"maze.json")))
                    session=MazeSession.Load(directory,training,"cpu",adaptiveControl:adaptive);
                else session=new MazeSession(new() {Seed=Integer(a,"seed",42),AdaptiveControl=adaptive??true,PropagationSteps=4},training,"cpu",bakedBrain:BakedBrain.Load(directory));
                return Status();
            }
            case "maze_step":
            {
                var live=RequireSession();int frames=Integer(a,"frames",60);if(frames<1||frames>600)throw new ArgumentOutOfRangeException("frames","Use 1–600 frames.");
                var timer=System.Diagnostics.Stopwatch.StartNew();long before=live.Frames;
                do{live.Step();}while(live.Frames-before<frames&&timer.Elapsed.TotalSeconds<2);
                return new {advanced=live.Frames-before,status=Status()};
            }
            case "maze_status":return Status();
            case "maze_save":
            {
                var live=RequireSession();string directory=PathFor(String(a,"directory"));
                if(Directory.Exists(directory)||File.Exists(directory))throw new IOException("Choose a new checkpoint directory.");
                string staging=directory+".tmp-"+Guid.NewGuid().ToString("N");
                try{live.Save(staging);Directory.Move(staging,directory);}
                finally{if(Directory.Exists(staging))Directory.Delete(staging,true);}
                return new {directory,frames=live.Frames};
            }
            case "maze_stop":session?.Dispose();session=null;return new {stopped=true};
            default:throw new ArgumentException("Unknown tool.");
        }
    }
    private object Status()
    {
        var s=RequireSession();return new {neurons=s.MainModel.NormalCount,connections=s.MainModel.Weights.Length,s.FrameMilliseconds,s.Frames,s.Training,s.ControlOrigin,s.CompletedMazes,s.TotalKills,s.TotalHits,s.TotalMisses,s.TotalExploration,
            episode=s.Game.Episode,level=s.Game.Level,health=s.Game.Health,s.LearningReward,s.Loss,s.RewardLoss,
            adaptive=s.Adaptive is null?null:new {s.Adaptive.Decisions,s.Adaptive.Updates,s.Adaptive.ReplayCount,s.Adaptive.KnownViews,s.Adaptive.LastTdError,s.Adaptive.Exploration,s.AdaptiveRewards.EpisodeReturn,s.AdaptiveRewards.ExplorationEarned},
            cortexRewards=s.CortexRewards?.Signals};
    }
    private MazeSession RequireSession()=>session??throw new InvalidOperationException("Start a maze session first.");
    private string PathFor(string relative)
    {
        if(string.IsNullOrWhiteSpace(relative))throw new ArgumentException("Path is required.");
        string path=Path.GetFullPath(relative,root),rel=Path.GetRelativePath(root,path);
        if(rel==".."||rel.StartsWith(".."+Path.DirectorySeparatorChar)||Path.IsPathRooted(rel)||rel==".")throw new ArgumentException("Path must be inside the workspace.");
        // Reject symlinks, including dangling links and final filenames, before reading/writing.
        string current=root;
        foreach(string part in rel.Split(Path.DirectorySeparatorChar))
        {
            current=Path.Combine(current,part);
            if(new FileInfo(current).LinkTarget is not null||new DirectoryInfo(current).LinkTarget is not null)throw new ArgumentException("Symlink paths are not supported.");
        }
        return path;
    }
    private static void AtomicWrite(string path,string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);string temporary=path+".tmp-"+Guid.NewGuid().ToString("N");
        try{File.WriteAllText(temporary,text);File.Move(temporary,path,true);}finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static string Revision(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string String(JsonElement value,string name)=>value.GetProperty(name).GetString()??throw new ArgumentException($"Missing {name}.");
    private static bool Boolean(JsonElement value,string name,bool fallback)=>value.TryGetProperty(name,out var p)?p.GetBoolean():fallback;
    private static int Integer(JsonElement value,string name,int fallback)=>value.TryGetProperty(name,out var p)?p.GetInt32():fallback;
    private static string Result(object? id,object result)=>JsonSerializer.Serialize(new {jsonrpc="2.0",id,result},Wire);
    private static string Error(object? id,int code,string message)=>JsonSerializer.Serialize(new {jsonrpc="2.0",id,error=new {code,message}},Wire);
    public void Dispose(){session?.Dispose();session=null;}
}
