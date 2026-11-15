using System.Reflection;
using ArcadeLab;
using ArcadeLab.Editor;
using GlmSharp;
using MYT.Kit.Gui;

internal static class EditorInteractionChecks
{
    public static void Run(Action<bool,string> check,string output)
    {
        var module=new BrainModule {Name="Editable",LayerSizes=[4,8,4]};
        var target=new BrainModule {Name="Target",LayerSizes=[4,4],Position=new(18,0,0)};
        var link=new BrainLink {Source=new(){ModuleId=module.Id,GroupIds=["output"]},Destination=new(){ModuleId=target.Id,GroupIds=["input"]}};
        var doc=new BrainDocument {Modules=[module,target],Links=[link]};
        Viewer.CaptureCanvas(1100,740,Path.Combine(output,"interaction-module.ppm"),(canvas,w,h)=>
        {
            var editor=new EditorApp(canvas,doc,"unused.json");
            FieldInfo Field(string name)=>typeof(EditorApp).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!;
            void Frame(int view,float x=0,float y=0,bool down=false,string text="",GuiKeys keys=GuiKeys.None)
                =>editor.RenderPreview(w,h,view,new GuiInput {Cursor=new(x,y),MouseDown=down,Text=text,Keys=keys});
            void Click(int view,float x,float y){Frame(view,x,y,true);Frame(view,x,y);}
            Frame(0);Click(0,28,185);
            check((string)Field("selectedModule").GetValue(editor)! ==""&&(string)Field("selectedLink").GetValue(editor)! =="","empty canvas click clears selection");
            Field("selectedModule").SetValue(editor,module.Id);
            Frame(1);Click(1,720,211);Frame(1,keys:GuiKeys.End);Frame(1,text:" revised");Frame(1,keys:GuiKeys.Enter);
            check(doc.Modules[0].Name=="Editable revised"&&Field("textTitle").GetValue(editor) is null,"property text edits inline without a modal");
            Click(1,1015,266);
            check(doc.Modules[0].LayerSizes.Length==4,"layer plus button adds an editable layer");
            Click(1,1048,266);
            check(doc.Modules[0].LayerSizes.Length==3,"layer minus button removes a layer");
            Click(1,730,481);Click(1,730,518);
            check(!doc.Modules[0].FixedResidual,"boolean property uses a false/true dropdown");
            Click(1,700,373);Frame(1,keys:GuiKeys.Home);Frame(1,keys:GuiKeys.Delete);Frame(1,text:"-");Frame(1,keys:GuiKeys.Enter);
            check(doc.Modules[0].ResidualStep==3&&Field("inlineId").GetValue(editor) is not null,"invalid inline number keeps the draft without changing the model");
            Frame(1,keys:GuiKeys.Escape);
            check(Field("inlineId").GetValue(editor) is null,"Escape cancels an invalid inline draft");
            Click(1,845,160); // Neuron groups tab.
            Click(1,730,356); // Kind dropdown in the group inspector.
            check(Field("choiceMenu").GetValue(editor) is not null&&Field("textTitle").GetValue(editor) is null,"enum opens a dropdown without a modal");
            Click(1,730,475); // Other.
            check(doc.Modules[0].Groups.Single(g=>g.Id=="output").Kind==GroupKind.Other,"enum dropdown commits its selected option");
            Frame(1);
        });
        Viewer.CaptureCanvas(1100,740,Path.Combine(output,"interaction-link-groups.ppm"),(canvas,w,h)=>
        {
            var editor=new EditorApp(canvas,doc,"unused.json");
            typeof(EditorApp).GetField("selectedLink",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(editor,link.Id);
            void Frame(float x=0,float y=0,bool down=false)=>editor.RenderPreview(w,h,1,new GuiInput {Cursor=new vec2(x,y),MouseDown=down});
            Frame();Frame(730,259,true);Frame(730,259); // All source neurons.
            check(link.Source.Scope==EndpointScope.AllNeurons,"link Edit group list selects all source neurons");
            Frame(730,337,true);Frame(730,337); // Output group.
            check(link.Source.Scope==EndpointScope.Groups&&link.Source.GroupIds.SequenceEqual(["output"]),"link Edit group list selects one named group");
            Frame(730,311,true);Frame(730,311); // Hidden group.
            check(link.Source.GroupIds.Count==2&&link.Source.GroupIds.Contains("hidden"),"link Edit group list toggles multiple groups");
            Frame(730,311,true);Frame(730,311);Frame(730,337,true);Frame(730,337);
            check(link.Source.Scope==EndpointScope.Groups&&link.Source.GroupIds.Count==0&&!Baking.Compile(doc).EdgeLinkIds.Contains(link.Id),"deselecting every group makes an empty link, not an all-neuron link");
            Frame(730,337,true);Frame(730,337);
        });
        Viewer.CaptureCanvas(1100,740,Path.Combine(output,"interaction-link-overview.ppm"),(canvas,w,h)=>
        {
            var editor=new EditorApp(canvas,doc,"unused.json");editor.RenderPreview(w,h,0);
        });
        Viewer.CaptureCanvas(1100,740,Path.Combine(output,"interaction-rotate.ppm"),(canvas,w,h)=>
        {
            var editor=new EditorApp(canvas,doc,"unused.json");
            void Frame(float x=0,float y=0,bool down=false)=>editor.RenderPreview(w,h,0,new GuiInput {Cursor=new(x,y),MouseDown=down});
            Frame();
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var camera=(PerspectiveCamera)typeof(EditorApp).GetProperty("NavigationCamera",flags)!.GetValue(editor)!;
            var scene=(LinkScene)typeof(EditorApp).GetField("linkScene",flags)!.GetValue(editor)!;
            var center=scene.ById[doc.Modules[0].Id].Center;float radius=65/camera.PixelsPerUnit(center);
            camera.TryProject(center+System.Numerics.Vector3.UnitX*radius*.65f,out var start);
            camera.TryProject(center+System.Numerics.Vector3.UnitX*radius*.9f,out var end);
            var before=doc.Modules[0].Position;
            Frame(start.X,start.Y,true);Frame(end.X,end.Y,true);Frame(end.X,end.Y);
            var after=doc.Modules[0].Position;
            check(after.X>before.X&&Math.Abs(after.Y-before.Y)<.001f&&Math.Abs(after.Z-before.Z)<.001f,"dragging the X arrow moves only the module X coordinate");
            Frame(1010,162,true);Frame(1010,162); // Rotate mode.
            center=scene.ById[doc.Modules[0].Id].Center;radius=65/camera.PixelsPerUnit(center);
            camera.TryProject(center+new System.Numerics.Vector3(MathF.Cos(.55f),MathF.Sin(.55f),0)*radius,out start);
            camera.TryProject(center+new System.Numerics.Vector3(MathF.Cos(.8f),MathF.Sin(.8f),0)*radius,out end);
            Frame(start.X,start.Y,true);Frame(end.X,end.Y,true);Frame(end.X,end.Y);
            check(Math.Abs(doc.Modules[0].Rotation.Z)>1,"dragging the Z ring rotates the module");
        });
    }
}
