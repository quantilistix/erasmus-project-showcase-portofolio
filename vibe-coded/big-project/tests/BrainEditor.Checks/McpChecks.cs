using ArcadeLab.Editor;
using System.Text.Json;

internal static class McpChecks
{
    internal static void Run(Action<bool,string> check,string output)
    {
        string root=Path.Combine(output,"mcp");Directory.CreateDirectory(root);
        using var server=new BrainMcpServer(root);int id=0;
        JsonElement Request(string method,object? parameters=null)
        {
            string response=server.Handle(JsonSerializer.Serialize(new {jsonrpc="2.0",id=++id,method,@params=parameters??new {}}))!;
            return JsonDocument.Parse(response).RootElement.Clone();
        }
        JsonElement Call(string name,object? arguments=null)=>Request("tools/call",new {name,arguments=arguments??new {}}).GetProperty("result");
        JsonElement Data(JsonElement result)=>JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!).RootElement.Clone();
        check(Request("tools/list").GetProperty("error").GetProperty("code").GetInt32()==-32002,"MCP requires initialization");
        check(Request("initialize",new {protocolVersion="2025-11-25"}).GetProperty("result").GetProperty("protocolVersion").GetString()=="2025-11-25","MCP negotiates protocol version");
        check(server.Handle("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}")==null,"MCP notification produces no output");
        check(Request("tools/list").GetProperty("result").GetProperty("tools").GetArrayLength()==10,"MCP exposes editor and maze tools");
        check(!Call("brain_create_maze",new {path="maze.brain.json"}).GetProperty("isError").GetBoolean(),"MCP creates a maze-ready project");
        var read=Data(Call("brain_read",new {path="maze.brain.json"}));string revision=read.GetProperty("revision").GetString()!;
        var original=File.ReadAllText(Path.Combine(root,"maze.brain.json"));
        check(Call("brain_write",new {path="maze.brain.json",revision="stale",document=read.GetProperty("document")}).GetProperty("isError").GetBoolean()
            &&File.ReadAllText(Path.Combine(root,"maze.brain.json"))==original,"MCP stale edits preserve the original project");
        check(!Call("brain_write",new {path="maze.brain.json",revision,document=read.GetProperty("document")}).GetProperty("isError").GetBoolean(),"MCP accepts a validated revision-matched edit");
        check(Call("brain_read",new {path="../outside.json"}).GetProperty("isError").GetBoolean(),"MCP rejects traversal outside workspace");
        if(!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(root,"linked.json"),Path.Combine(root,"maze.brain.json"));
            check(Call("brain_read",new {path="linked.json"}).GetProperty("isError").GetBoolean(),"MCP rejects symlink paths");
        }
        check(Data(Call("brain_validate",new {path="maze.brain.json"})).GetProperty("mazeCompatible").GetBoolean(),"MCP validates all maze sensory and motor bindings");
        check(!Call("brain_bake",new {path="maze.brain.json",directory="baked"}).GetProperty("isError").GetBoolean(),"MCP bakes a project");
        check(Call("brain_bake",new {path="maze.brain.json",directory="baked"}).GetProperty("isError").GetBoolean(),"MCP protects existing bakes");
        check(!Call("maze_start",new {directory="baked"}).GetProperty("isError").GetBoolean(),"MCP starts headless learning");
        check(Call("maze_step",new {frames=601}).GetProperty("isError").GetBoolean(),"MCP bounds simulation requests");
        check(Data(Call("maze_step",new {frames=32})).GetProperty("advanced").GetInt64()>0,"MCP advances learning and returns diagnostics");
        check(!Call("maze_save",new {directory="checkpoint"}).GetProperty("isError").GetBoolean(),"MCP saves neural learning state");
        check(Call("maze_save",new {directory="checkpoint"}).GetProperty("isError").GetBoolean(),"MCP protects previous checkpoints");
        Call("maze_stop");check(Call("maze_status").GetProperty("isError").GetBoolean(),"MCP stop releases session");
        check(!Call("maze_start",new {directory="checkpoint",training=false}).GetProperty("isError").GetBoolean(),"MCP resumes saved policy for evaluation");
        check(Request("unknown").GetProperty("error").GetProperty("code").GetInt32()==-32601,"MCP reports unknown methods");
        check(JsonDocument.Parse(server.Handle("{")!).RootElement.GetProperty("error").GetProperty("code").GetInt32()==-32700,"MCP reports malformed JSON");
    }
}
