using ArcadeLab;
using ArcadeLab.Editor;

try
{
    string Value(string key,string fallback){int index=Array.IndexOf(args,key);if(index<0)return fallback;if(index+1==args.Length)throw new ArgumentException($"Missing value for {key}.");return args[index+1];}
    if(args.Contains("--mcp")){var output=Console.Out;Console.SetOut(Console.Error);using var server=new BrainMcpServer(Value("--workspace",Directory.GetCurrentDirectory()));server.Run(Console.In,output);return 0;}
    if(args.Contains("--help")){Console.WriteLine("BrainEditor: --mcp [--workspace PATH] | --project PATH [--bake NEW_DIRECTORY] [--open-baked DIRECTORY] [--frames COUNT]\nWithout --bake, opens the native four-view editor. New projects start with a connected example.");return 0;}
    string project=Value("--project","artifacts/editor/brain.brain.json");
    var document=File.Exists(project)?BrainDocument.Parse(File.ReadAllText(project)):BrainDocument.Example();
    if(args.Contains("--bake")){var baked=Baking.Bake(document);baked.Save(Value("--bake",""));Console.WriteLine($"Baked {baked.Model.NormalCount} pairs, {baked.Model.ParameterCount} edges.");return 0;}
    using var viewer=new Viewer();viewer.SetMinimumSize(1100,740);var editor=new EditorApp(viewer,document,project);
    if(args.Contains("--open-baked"))editor.OpenBaked(Value("--open-baked",""));
    editor.Run(int.Parse(Value("--frames","0")));return 0;
}
catch(Exception e){Console.Error.WriteLine(e.ToString());return 1;}
