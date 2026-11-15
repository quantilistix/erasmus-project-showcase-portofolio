using ArcadeLab;
using ArcadeLab.Editor;
using GlmSharp;
using MYT.Kit.Gui;
using MYT.Kit.Neural;
using NeuralGraph;
using MYT.Kit.Graph;
using System.Diagnostics;
using Vector2=System.Numerics.Vector2;
using Vector3=System.Numerics.Vector3;

if(args.Contains("--adaptive-benchmark")){AdaptiveMazeBenchmark.Run();return;}
int checks=0;
void Check(bool condition,string name)
{
    if(!condition)throw new InvalidOperationException(name);
    Console.WriteLine("PASS "+name);checks++;
}
string output=Path.GetFullPath(args.FirstOrDefault(a=>!a.StartsWith("--"))??Path.Combine(Path.GetTempPath(),"myt-neural-checks-"+Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(output);
var atlas=new GuiAtlas();
var font=GuiDefaultFont.Create(atlas);
Check(font.Glyph('a').Image!=font.Glyph('A').Image,"font preserves lowercase glyphs");
var gui=new GuiContext(atlas,new GuiStyle(font));
var screen=new vec2(320,200);
var button=new GuiRect(10,10,100,30);
gui.Begin(new GuiInput {Cursor=new vec2(20,20),MouseDown=true},screen,1);
Check(gui.Button(button,"CLICK"),"MYT button press");gui.End();
gui.Begin(new GuiInput {Cursor=new vec2(20,20),MouseDown=true},screen,1);
Check(!gui.Button(button,"CLICK"),"held button does not repeat");gui.End();
gui.Begin(new GuiInput(),screen,1);gui.Focus("field");string value="ab";
gui.TextField("field",new GuiRect(10,50,100,20),ref value);gui.End();
gui.Begin(new GuiInput {Keys=GuiKeys.Left,Text="c"},screen,1);
gui.TextField("field",new GuiRect(10,50,100,20),ref value);gui.End();
Check(value=="abc","text insertion");
gui.Begin(new GuiInput {Keys=GuiKeys.Backspace},screen,1);
gui.TextField("field",new GuiRect(10,50,100,20),ref value);gui.End();
Check(value=="ac","caret navigation and backspace");
gui.Begin(new GuiInput(),screen,1);
gui.PushClip(new GuiRect(5,5,10,10));gui.Rect(new GuiRect(0,0,30,30),GuiColor.White);gui.PopClip();gui.End();
int pixels=0;
GuiRasterizer.Draw(gui.DrawList,atlas,(x,y,n,c)=>{CheckSpan(x>=5&&x+n<=15&&y>=5&&y<15);pixels+=n;});
void CheckSpan(bool valid){if(!valid)throw new Exception("Raster clip escaped");}
Check(pixels==100,"GUI raster respects clipping");
gui.Begin(new GuiInput(),screen,1);
gui.Gradient(new GuiRect(0,0,2,2),GuiColor.Black,GuiColor.White);gui.End();
var gradient=new List<GuiColor>();
GuiRasterizer.Draw(gui.DrawList,atlas,(x,y,n,c)=>gradient.Add(c));
Check(gradient.Count==2&&gradient[0].R==64&&gradient[1].R==191,"GUI gradient sampling");
gui.Begin(new GuiInput(),screen,1);
gui.Rect(new GuiRect(0,0,2,1),new GuiColor(10,20,30,128));gui.End();
GuiColor translucent=default;
GuiRasterizer.Draw(gui.DrawList,atlas,(x,y,n,c)=>translucent=c);
Check(translucent==new GuiColor(10,20,30,128),"GUI raster preserves tint and straight alpha");
var batch=new GraphDrawList {Clip=new(0,0,32,32)};
batch.Line(new(5,15,.2f),new(25,15,.2f),new(255,0,0));
batch.Line(new(15,5,.8f),new(15,25,.8f),new(0,0,255));
int[] graphPixels=new int[32*32];
var graphRaster=new GraphRasterizer();graphRaster.Draw(batch,graphPixels,32,32);
Check((graphPixels[15*32+15]&0xFFFFFF)==0xFF,"reference line depth rejects farther weight");
batch.Neuron(new(15,15,.99f),3,new(0,255,0));graphRaster.Draw(batch,graphPixels,32,32);
Check((graphPixels[15*32+15]&0xFFFFFF)==0xFF,"reference far neuron is occluded by near weights");
batch.Neuron(new(15,15,.1f),3,new(0,255,0));graphRaster.Draw(batch,graphPixels,32,32);
Check((graphPixels[15*32+15]&0xFFFFFF)==0xFF00,"reference near neuron covers weights");
batch.Neuron(new(15,15,.7f),3,new(255,255,0));graphRaster.Draw(batch,graphPixels,32,32);
Check((graphPixels[15*32+15]&0xFFFFFF)==0xFF00,"reference neurons depth-test each other");
Check(batch.Lines.Length==4,"weights use exactly two vertices per line primitive");
var guides=new GraphDrawList {Clip=new(0,0,32,32)};
guides.Guide(new(5,15,.1f),new(25,15,.1f),new(255,0,0,128));
Array.Clear(graphPixels);graphRaster.Draw(guides,graphPixels,32,32);
Check((graphPixels[15*32+15]&255)==128,"orientation guides blend transparently");
guides.Line(new(15,5,.8f),new(15,25,.8f),new(0,0,255));
Array.Clear(graphPixels);graphRaster.Draw(guides,graphPixels,32,32);
Check((graphPixels[15*32+15]&0xFFFFFF)==0xFF0000,"orientation guides do not occlude weight depth");
foreach(float density in new[]{.001f,1f,20f,1000f})
{
    guides.Clear();
    float spacing=GraphOrientation.AddGrid(guides,new(1,2,3),density,.7f,.4f,p=>GraphCamera.Rotate(p,.7f,.4f));
    Check(spacing>0&&guides.Guides.Length is >0 and <=204&&guides.Lines.Length==0,"bounded 3D grid at zoom "+density);
}
foreach(var angles in new[]{(0f,0f),(.7f,.4f),(-1.2f,-.6f)})
{
    var original=new Vector3(2,-3,4);
    var roundtrip=GraphCamera.Unrotate(GraphCamera.Rotate(original,angles.Item1,angles.Item2),angles.Item1,angles.Item2);
    Check(Vector3.Distance(original,roundtrip)<.00001f,"camera rotation inverse");
    var delta=GraphCamera.Drag(new(40,-20),20,angles.Item1,angles.Item2);
    var screenDelta=GraphCamera.Rotate(delta,angles.Item1,angles.Item2);
    Check(Vector3.Distance(screenDelta,new(2,-1,0))<.00001f,"camera-plane dragging at orbit angle");
}
Check(GraphCamera.Drag(new(40,-20),20,.7f,.4f,axis:'Z')==new Vector3(0,0,3),"Z-constrained world drag");
var depthDrag=GraphCamera.Rotate(GraphCamera.Drag(new(0,-20),20,.7f,.4f,depth:true),.7f,.4f);
Check(Vector3.Distance(depthDrag,new(0,0,1))<.00001f,"depth drag follows camera direction");
var zoomBefore=new PerspectiveCamera(Vector3.Zero,new(200,200),300,10,.1f,100,.7f,.4f);
var zoomAfter=zoomBefore with {Distance=5};var zoomCursor=new Vector2(240,170);
var anchoredPoint=zoomBefore.Unproject(zoomCursor,zoomBefore.Distance);
zoomAfter=zoomAfter with {Target=zoomAfter.Target+zoomBefore.ZoomAnchorTranslation(zoomAfter,zoomCursor)};
zoomAfter.TryProject(anchoredPoint,out var anchorScreen);
Check(Vector2.Distance(new(anchorScreen.X,anchorScreen.Y),zoomCursor)<.0001f,"camera translation preserves cursor-anchored zoom");
var panBefore=new PerspectiveCamera(Vector3.Zero,new(200,200),300,10,.1f,100,0,0);
var panAfter=panBefore with {Target=panBefore.Target+panBefore.PanTranslation(new(30,0))};
panAfter.TryProject(new(1,-5,0),out var nearPan);panAfter.TryProject(new(1,5,0),out var farPan);
panBefore.TryProject(new(1,-5,0),out var nearOriginal);panBefore.TryProject(new(1,5,0),out var farOriginal);
Check(panAfter.Origin==panBefore.Origin&&nearPan.X-nearOriginal.X>farPan.X-farOriginal.X,"sideways camera pan keeps lens fixed and produces parallax");
var metadata=new BrainDocument();var linkView=metadata.GetView("Link");
linkView.Yaw=1.2f;linkView.Pitch=.6f;linkView.Zoom=2;linkView.Pan=new(1,2,3);linkView.FrameCenter=new(4,5,6);linkView.FrameSpan=12;
metadata.GetView("Edit/a").Zoom=3;metadata.GetView("Edit/b").Zoom=4;
Check(metadata.GetView("Link").Zoom==2&&metadata.GetView("Edit/a").Zoom==3&&metadata.GetView("Edit/b").Zoom==4,"independent camera state per view and module");
string navigationPath=Path.Combine(output,"navigation.brain.json");metadata.Save(navigationPath);
var restoredViews=BrainDocument.Parse(File.ReadAllText(navigationPath));var restoredLink=restoredViews.GetView("Link");
Check(restoredLink.Yaw==1.2f&&restoredLink.Pitch==.6f&&restoredLink.Zoom==2&&restoredLink.Pan==new Position(1,2,3)
    &&restoredLink.FrameCenter==new Position(4,5,6)&&restoredLink.FrameSpan==12,"camera state survives project Save/Open");
restoredViews.GetView("Edit/a").Reset();
Check(restoredViews.GetView("Link").Zoom==2&&restoredViews.GetView("Edit/a").Zoom==1,"fit resets only the active view");
var canvasBounds=new GuiRect(10,10,100,100);
Check(ViewportGeometry.MarkerVisible(canvasBounds,new(8,60,.5f),4)&&!ViewportGeometry.MarkerVisible(canvasBounds,new(5,60,.5f),4),"partial neurons remain visible until their whole marker exits");
Check(ViewportGeometry.Overlaps(canvasBounds,new(-100,40,130,56)),"partial module cards intersect canvas");
Vector2 crossingFrom=new(-100,60),crossingTo=new(300,60);
Check(ViewportGeometry.ClipLine(canvasBounds,ref crossingFrom,ref crossingTo)&&Math.Abs(crossingFrom.X-10)<.001f&&Math.Abs(crossingTo.X-110)<.001f,"module links clip with both endpoints outside");
foreach(int v in new[]{0,1,3})
{
    var bounds=ViewportGeometry.CanvasBounds(v,ModuleKind.Layered,1100,740);
    Check(bounds.Bottom==666&&bounds.Height>=476,"canvas fills space down to footer for view "+v);
}
Check(GraphCamera.Rotate(Vector3.UnitZ,0,0).Y<0&&GraphCamera.Rotate(Vector3.UnitY,0,0).Z>0,"Z is up and Y is forward");
var perspective=new PerspectiveCamera(Vector3.Zero,new(200,200),300,10,.1f,100,0,0);
perspective.TryProject(new(1,-5,0),out var nearPoint);
perspective.TryProject(new(1,5,0),out var farPoint);
Check(nearPoint.X-200>farPoint.X-200&&nearPoint.Z<farPoint.Z,"perspective shrinks distant geometry with ordered depth");
perspective.TryProject(Vector3.UnitZ,out var upPoint);
Check(upPoint.Y<200,"perspective projects positive Z upward");
Check(!perspective.TryProject(new(0,-11,0),out _),"perspective hides points behind camera");
Check(perspective.ProjectSegment(new(-1,-11,0),new(1,0,0),out var clippedA,out var clippedB)
    &&float.IsFinite(clippedA.X)&&clippedA.Z>=0&&clippedB.Z<=1,"perspective clips crossing lines at near plane");
Check(!perspective.ProjectSegment(new(0,-20,0),new(1,-15,0),out _,out _),"perspective rejects lines behind near plane");
foreach(var angles in new[]{(0f,0f),(.7f,.4f),(-1.2f,-.6f)})
{
    var camera=perspective with {Distance=20,Yaw=angles.Item1,Pitch=angles.Item2};
    var point=new Vector3(2,3,4);
    camera.TryProject(point,out var screenPoint);
    var restored=camera.Unproject(new(screenPoint.X,screenPoint.Y),camera.ToView(point).Z);
    Check(Vector3.Distance(restored,point)<.00001f,"perspective unprojection round trip");
    var motion=new Vector2(40,-20);
    var moved=point+camera.Drag(point,motion);
    camera.TryProject(moved,out var movedScreen);
    Check(Vector2.Distance(new(movedScreen.X-screenPoint.X,movedScreen.Y-screenPoint.Y),motion)<.0001f,"perspective drag tracks cursor at object depth");
}
GroupChecks.Run(Check,output);
ConnectionChecks.Run(Check);
PowerLawChecks.Run(Check,output);
LatticeChecks.Run(Check,output);
EditorInteractionChecks.Run(Check,output);
MazeBakedChecks.Run(Check,output);
CortexRewardChecks.Run(Check,output);
AdaptiveMazeChecks.Run(Check,output);
McpChecks.Run(Check,output);
ModularBrainChecks.Run(Check,output);
int mazeBrainArgument=Array.IndexOf(args,"--maze-brain");
if(mazeBrainArgument>=0)
{
    using var liveMaze=new MazeSession(new MazeConfig(),true,"cpu",bakedBrain:BakedBrain.Load(args[mazeBrainArgument+1]));
    var originalWeights=(float[])liveMaze.MainModel.Weights.Clone();
    for(int frame=0;frame<10;frame++)liveMaze.StepControls(MazeControls.None);
    var rewardModules=liveMaze.Baked!.Brain.Manifest.Modules.Where(m=>m.Kind==ModuleKind.Reward||m.Name.Contains("reward",StringComparison.OrdinalIgnoreCase)).Select(m=>m.Id).ToHashSet();
    int[] rewardEdges=liveMaze.MainModel.Topology.Edges.Select((e,i)=>(e,i)).Where(x=>rewardModules.Contains(liveMaze.Baked.Brain.Manifest.Neurons[x.e.Source].ModuleId)||rewardModules.Contains(liveMaze.Baked.Brain.Manifest.Neurons[x.e.Destination].ModuleId)).Select(x=>x.i).ToArray();
    Check(liveMaze.RewardUpdates==10&&rewardEdges.Any(i=>liveMaze.MainModel.Weights[i]!=originalWeights[i]),"supplied bake performs ten genuine reward-path learning updates");
    Viewer.CaptureMaze(liveMaze,1600,1000,Path.Combine(output,"maze-editor-network.ppm"));
    Check(liveMaze.Baked!.OutputLabels.Count==8&&liveMaze.Baked.InputLabels.Count==liveMaze.MainModel.Topology.InputCount,"supplied bake resolves every external input and motor key");
    Check(liveMaze.Baked.InputLabels.Count(p=>p.Value.StartsWith("tactile."))==16,"supplied bake has sixteen real tactile boundaries");
    var frozen=rewardEdges.Select(i=>liveMaze.MainModel.Weights[i]).ToArray();liveMaze.SetMode(false);
    for(int frame=0;frame<3;frame++)liveMaze.StepControls();
    Check(rewardEdges.Select(i=>liveMaze.MainModel.Weights[i]).SequenceEqual(frozen),"supplied bake keeps reward paths frozen in play");
}

if(args.Contains("--gpu"))GpuGraphChecks.Run(Check);
var topology=new GraphTopology(1,2,1,[1],[new(0,1)]);
var model=new PredictiveCodingModel(topology,42);
using(var network=PredictiveNetworks.Create(model,NeuralBackend.Cpu))
{
    network.Step([.5f],2,.04f,.02f,.00001f,true);
    Check(network.CaptureState().Normal.All(float.IsFinite),"MYT neural CPU integration");
}
var document=BrainDocument.Example();
var baked=Baking.Bake(document);
string bakedPath=Path.Combine(output,"baked-"+Guid.NewGuid().ToString("N"));
baked.Save(bakedPath);
using(var runtime=new BrainRuntime(baked))
{
    runtime.Step(new float[baked.Model.Topology.InputCount],1d/60);
    Check(runtime.Time>0,"baked network runtime");
}
foreach(var size in new[]{(1100,740),(1600,1000)})
for(int view=0;view<4;view++)
{
    string path=Path.Combine(output,$"editor-{size.Item1}-{view}.ppm");
    Viewer.CaptureCanvas(size.Item1,size.Item2,path,(canvas,w,h)=>
    {
        var editor=new EditorApp(canvas,document,"preview.brain.json");
        if(view==3)editor.OpenBaked(bakedPath);
        editor.RenderPreview(w,h,view);
    });
    Check(new FileInfo(path).Length>size.Item1*size.Item2*3,"editor view "+view+" at "+size.Item1);
}
using(var maze=new MazeSession(new MazeConfig {Layers=1,Width=16,RewardHidden=8},true,"cpu"))
{
    maze.Step();
    var before=maze.Observation.ToArray();long frames=maze.Frames;
    Viewer.CaptureMaze(maze,1600,1000,Path.Combine(output,"maze.ppm"));
    Check(maze.Frames==frames&&before.SequenceEqual(maze.Observation),"maze rendering preserves simulation and sensors");
    Check(maze.NormalActivities.All(float.IsFinite),"maze MYT backend integration");
}
// Regression: clock modules and array copies land on odd/even indices after a large
// input prefix, and a global neuron stride skipped some of them. Compilation, persistence, runtime and rendering must keep them all.
var largeInput=new BrainModule {Kind=ModuleKind.InputPanel,Name="SENSORS",Channels=2001};
var clocks=Enumerable.Range(0,3).Select(i=>new BrainModule {Kind=ModuleKind.Clock,Name="CLOCK "+i,Position=new((i-1)*100,400,10)}).ToArray();
clocks[0].Modifiers.Add(new ArrayModifier {Dimensions=2,AxisSizes=[2,2],Spacing=8});
var clockOutput=new BrainModule {Kind=ModuleKind.OutputPanel,Name="OUTPUT",Channels=2,Position=new(150,400,0)};
var clockDocument=new BrainDocument {Modules=[largeInput,..clocks,clockOutput]};
foreach(var clock in clocks)clockDocument.Links.Add(new(){Source=new(){ModuleId=clock.Id},Destination=new(){ModuleId=clockOutput.Id}});
clockDocument.Links.Add(new(){Source=new(){ModuleId=largeInput.Id},Destination=new(){ModuleId=clockOutput.Id}});
var clockScene=new LinkScene(clockDocument);var clockBake=Baking.Bake(clockDocument);
Check(clocks.All(m=>clockBake.Manifest.Neurons.Count(n=>n.ModuleId==m.Id)==Generation.Generate(m).Neurons.Count),"baking preserves every clock above 2000 neurons");
var identity=clockBake.Manifest.Neurons.Select((n,i)=>(n,i)).ToDictionary(x=>(x.n.ModuleId,x.n.Id),x=>x.i);
Check(clockScene.Graph.Neurons.All(n=>clockBake.Manifest.Neurons[identity[(n.ModuleId,n.Id)]]==n),"Link preview and bake preserve identical neuron identities and geometry");
Check(clockScene.Graph.Edges.Select((e,i)=>clockBake.Model.Topology.Edges[i].Source==identity[(clockScene.Graph.Neurons[e.Source].ModuleId,clockScene.Graph.Neurons[e.Source].Id)]
    &&clockBake.Model.Topology.Edges[i].Destination==identity[(clockScene.Graph.Neurons[e.Destination].ModuleId,clockScene.Graph.Neurons[e.Destination].Id)]
    &&clockBake.Model.Weights[i]==e.Weight&&clockBake.Model.Plasticity[i]==e.Plasticity).All(x=>x),"Link preview and bake use identical connections and parameters");
string clockBakePath=Path.Combine(output,"clock-bake-"+Guid.NewGuid().ToString("N"));clockBake.Save(clockBakePath);
var loadedClock=BakedBrain.Load(clockBakePath);
Check(clocks.All(m=>loadedClock.Manifest.Neurons.Any(n=>n.ModuleId==m.Id&&n.Boundary)),"saved baked manifest restores all clock inputs");
using(var clockRuntime=new BrainRuntime(loadedClock))
{
    clockRuntime.Step(new float[loadedClock.Model.Topology.InputCount],1d/60,learn:false);
    var state=clockRuntime.Backend.CaptureState();
    Check(loadedClock.Manifest.Neurons.Select((n,i)=>(n,i)).Where(x=>clocks.Any(m=>m.Id==x.n.ModuleId)).All(x=>Math.Abs(state.Normal[x.i]-.5f)<.00001f),"runtime drives every baked clock boundary");
}
Viewer.CaptureCanvas(1100,740,Path.Combine(output,"clock-final.ppm"),(canvas,w,h)=>
{
    var editor=new EditorApp(canvas,clockDocument,"clock-preview.json");editor.OpenBaked(clockBakePath);editor.RenderPreview(w,h,3);
    int blueMarkers=0;var vertices=canvas.Graph.Neurons;
    for(int i=0;i<vertices.Length;i+=6)if(vertices[i].Color==GraphOrientation.ZColor&&vertices[i].Uv.X<0)blueMarkers++;
    Check(blueMarkers==clockBake.Manifest.Neurons.Count(n=>clocks.Any(m=>m.Id==n.ModuleId))+1,"Final Network renders every clock marker plus the Z-axis marker");
});
var firstClock=clocks[0];var clockIndices=clockScene.Graph.ModuleIndices[firstClock.Id];
var previousClock=clockScene.Graph.Neurons[clockIndices[0]];var edgeList=clockScene.Graph.Edges;
firstClock.Position+=new Position(1,2,3);clockScene.SynchronizePositions();
Check(clockScene.Graph.Neurons[clockIndices[0]].Position==previousClock.Position+new Position(1,2,3)
    &&ReferenceEquals(edgeList,clockScene.Graph.Edges),"module dragging translates cached geometry without recompiling connections");
var invalid=BrainDocument.Example();invalid.Links.Add(new(){Source=new(){ModuleId=invalid.Modules[1].Id},Destination=new(){ModuleId=invalid.Modules[0].Id}});
var invalidPreview=Baking.Compile(invalid,strictLinks:false);
Check(invalidPreview.LinkErrors.Count==1&&invalidPreview.Neurons.Count==invalid.Modules.Sum(m=>Generation.Generate(m).Neurons.Count),"invalid links leave module previews visible");
bool invalidRejected=false;try{Baking.Bake(invalid);}catch(InvalidDataException){invalidRejected=true;}
Check(invalidRejected,"baking still rejects invalid clamped-input links");
var labelArea=new GuiRect(0,0,500,300);var firstLabel=new GuiRect(100,100,140,18);
var nextLabel=GraphLabelLayout.Place(firstLabel,labelArea,[firstLabel]);
Check(nextLabel.Intersect(firstLabel).IsEmpty&&nextLabel.X>=0&&nextLabel.Bottom<=300,"module name placement avoids overlapping labels");
var clockBox=clockScene.ById[clocks[1].Id];
var pickCamera=PerspectiveCamera.Create(clockBox.Center,new(300,300),20,1,500,20,GraphCamera.DefaultYaw,GraphCamera.DefaultPitch);
Check(clockBox.IntersectRay(pickCamera,new(300,300),out var hitDistance)&&hitDistance>0,"3D module bounds support camera-ray selection");
var dense=new BrainDocument {Modules=[new BrainModule {Kind=ModuleKind.Layered,LayerSizes=[192,192,192]},new BrainModule {Kind=ModuleKind.Clock}]};
foreach(string key in new[]{"Link","Edit/"+dense.Modules[0].Id,"FinalNetwork"})dense.GetView(key).Zoom=.5f;
int denseEdgeCount=Baking.Compile(dense).Edges.Count;
string denseBakePath=Path.Combine(output,"dense-bake-"+Guid.NewGuid().ToString("N"));Baking.Bake(dense).Save(denseBakePath);
Viewer.CaptureCanvas(1600,1000,Path.Combine(output,"dense.ppm"),(canvas,w,h)=>
{
    var editor=new EditorApp(canvas,dense,"dense-preview.json");
    editor.RenderPreview(w,h,1);canvas.FinishGraphPreview();
    var timer=Stopwatch.StartNew();long allocated=GC.GetAllocatedBytesForCurrentThread();
    for(int i=0;i<10;i++){canvas.Graph.Clear();editor.RenderPreview(w,h,1);canvas.FinishGraphPreview();}
    timer.Stop();allocated=GC.GetAllocatedBytesForCurrentThread()-allocated;
    Check(denseEdgeCount>62862&&canvas.Graph.Lines.Length==denseEdgeCount*2,"Edit view renders every internal edge beyond the former display caps");
    canvas.Graph.Clear();editor.RenderPreview(w,h,0);
    Check(canvas.Graph.Lines.Length==denseEdgeCount*2,"Link view renders every internal edge without sampling");
    editor.OpenBaked(denseBakePath);canvas.Graph.Clear();editor.RenderPreview(w,h,3);
    Check(canvas.Graph.Lines.Length==denseEdgeCount*2,"Final Network renders every baked edge without sampling");
    Console.WriteLine($"Dense offscreen editor: {timer.Elapsed.TotalMilliseconds/10:F2} ms/frame, {allocated/10:N0} bytes/frame, {canvas.Graph.Lines.Length/2} weight primitives");
});
int sceneArgument=Array.IndexOf(args,"--scene");
if(sceneArgument>=0)
{
    var source=BrainDocument.Parse(File.ReadAllText(args[sceneArgument+1]));source.GetView("Link").Reset();
    Viewer.CaptureCanvas(1600,1000,Path.Combine(output,"project-link.ppm"),(canvas,w,h)=>
    {
        var editor=new EditorApp(canvas,source,"project-preview.json");editor.RenderPreview(w,h,0);canvas.FinishGraphPreview();
        var timer=Stopwatch.StartNew();
        for(int i=0;i<5;i++){editor.RenderPreview(w,h,0);canvas.FinishGraphPreview();}
        timer.Stop();Console.WriteLine($"Project Link preview: {timer.Elapsed.TotalMilliseconds/5:F2} ms/frame (CPU offscreen)");
        Check(canvas.Graph.Lines.Length>0&&canvas.Graph.Guides.Length>=source.Modules.Count*24,"project Link view draws network weights and twelve-edge module boxes");
    });
}
Console.WriteLine($"{checks} checks passed; previews in {output}");
