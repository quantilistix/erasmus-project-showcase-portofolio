using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArcadeLab;
using MYT.Kit.Gui;
using GlmSharp;
using System.Diagnostics;
using Vector2=System.Numerics.Vector2;
using Vector3=System.Numerics.Vector3;

namespace ArcadeLab.Editor;

public sealed partial class EditorApp
{
    private readonly Viewer viewer;
    private readonly GuiContext gui;
    private bool focusTextPending,previewDirty=true;
    private string frameText="";
    private GuiKeys frameKeys;
    private int documentRevision,inspectorRevision=-1,topologyRevision,linkSceneRevision=-1;
    private LinkScene? linkScene;
    private object? inspectorTarget;
    private string inspectorFields="";
    private JsonObject? inspectorNode;
    private List<(string Key,JsonNode? Value,Action<JsonNode?> Set)> inspectorEntries=[];
    private BrainDocument document;
    private string projectPath;
    private BakedBrain? baked;
    private string? bakedSource;
    private string selectedModule="",selectedLink="";
    private int selectedGroup=-1,selectedPair=-1,view,fieldPage,kindIndex,groupPage,memberPage;
    private readonly Stack<string> undo=[],redo=[];
    private readonly Dictionary<int,bool> previousKeys=[];
    private readonly Queue<uint> characters=[];
    private bool mouseWasDown,click,mouseDown,rightWasDown,linking,customLinking;
    private string? linkOrigin;
    private int? customOrigin;
    private bool dragging,dragCheckpoint,pairDragging,middleWasDown,shift;
    private float wheel;
    private char dragAxis=' ';
    private BrainModule? previewModule;
    private (int Source,int Destination,float Weight)[] previewEdges=[];
    private BakedBrain? edgeBake;
    private (int Source,int Destination,float Weight)[] bakedEdges=[];
    private Position graphCenter;
    private float graphFit,graphSpan;
    private float mouseX,mouseY,oldMouseX,oldMouseY,width,height;
    private EditorViewState ViewState=>document.GetView(view switch {0=>"Link",1=>selectedLink!=""?"EditLink/"+selectedLink:"Edit/"+selectedModule,2=>"Rewards",_=>"FinalNetwork"});
    private float yaw {get=>ViewState.Yaw;set=>ViewState.Yaw=value;}
    private float pitch {get=>ViewState.Pitch;set=>ViewState.Pitch=Math.Clamp(value,-1.5f,1.5f);}
    private float zoom {get=>ViewState.Zoom;set=>ViewState.Zoom=value;}
    private Vector3 cameraPan {get {var p=ViewState.Pan;return new(p.X,p.Y,p.Z);}set=>ViewState.Pan=new(value.X,value.Y,value.Z);}
    private GuiRect CanvasRect=>ViewportGeometry.CanvasBounds(view,selectedLink!=""?null:Module?.Kind,width,height);
    private GuiRect CanvasContent
    {
        get {var rect=CanvasRect.Inset(1);return view==0?rect:rect with {Height=Math.Max(1,rect.Height-38)};}
    }
    private float Fit(float span)=>.8f*Math.Max(1,Math.Min(CanvasContent.Width,CanvasContent.Height))/Math.Max(2,span);
    private PerspectiveCamera NavigationCamera=>Camera(ViewState.FrameCenter??graphCenter,Fit(ViewState.FrameSpan>0?ViewState.FrameSpan:graphSpan),ViewState.FrameSpan>0?ViewState.FrameSpan:graphSpan);
    private string status="READY - SAVE PROJECT TO KEEP YOUR WORK";
    private string? textTitle,textValue;
    private Action<string>? textCommit;
    private ModuleGraph? preview;
    private GraphMetrics? metrics;

    private readonly HashSet<int> pressed=[];
    public EditorApp(Viewer viewer,BrainDocument document,string projectPath)
    {
        this.viewer=viewer;gui=viewer.CreateGui();this.document=document;this.projectPath=projectPath;selectedModule=document.Modules.FirstOrDefault()?.Id??"";selectedGroup=document.RewardGroups.Count>0?0:-1;
        viewer.CharacterInput+=c=>characters.Enqueue(c);
        viewer.ScrollInput+=amount=>wheel+=amount;
    }
    private BrainModule? Module=>document.Modules.FirstOrDefault(m=>m.Id==selectedModule);
    private void Checkpoint(bool positionsOnly=false){undo.Push(document.Serialize());redo.Clear();if(undo.Count>100){var recent=undo.Take(100).Reverse().ToArray();undo.Clear();foreach(string item in recent)undo.Push(item);}Invalidate(positionsOnly);}
    private void Invalidate(bool positionsOnly=false){documentRevision++;if(!positionsOnly)topologyRevision++;baked=null;bakedSource=null;previewDirty=true;metrics=null;}
    private void Restore(string serialized){var navigation=document.EditorViews;document=BrainDocument.Parse(serialized);document.EditorViews=navigation;if(!document.Links.Any(l=>l.Id==selectedLink))selectedLink="";if(!document.Modules.Any(m=>m.Id==selectedModule))selectedModule="";Invalidate();selectedPair=-1;fieldPage=0;dragging=pairDragging=gizmoDragging=false;}
    private void Attempt(Action action){try{action();}catch(Exception e){status=e.Message;}}
    public void OpenBaked(string path)
    {
        baked=BakedBrain.Load(path);bakedSource=null;view=3;status=$"LOADED BAKED NETWORK: {path}";
    }
    public void Run(int maxFrames=0)
    {
        int frames=0;
        while(viewer.IsOpen&&(maxFrames<=0||frames++<maxFrames))
        {
            viewer.PollCanvas();pressed.Clear();
            for(int key=32;key<=348;key++){bool down=viewer.Key(key);if(down&&!previousKeys.GetValueOrDefault(key))pressed.Add(key);previousKeys[key]=down;}
            (mouseX,mouseY)=viewer.Cursor;mouseDown=viewer.MouseButton(0);click=mouseDown&&!mouseWasDown;
            frameText=string.Concat(characters.Select(c=>char.ConvertFromUtf32((int)c)));characters.Clear();
            frameKeys=GuiKeys.None;
            foreach(var (code,key) in new (int,GuiKeys)[] {(259,GuiKeys.Backspace),(261,GuiKeys.Delete),(263,GuiKeys.Left),(262,GuiKeys.Right),(268,GuiKeys.Home),(269,GuiKeys.End),(258,GuiKeys.Tab),(257,GuiKeys.Enter),(256,GuiKeys.Escape)})
                if(pressed.Contains(code))frameKeys|=key;
            uiCaptured=choiceMenu is not null;
            if(inlineId is not null)
            {
                if(pressed.Contains(256)){inlineId=null;gui.ClearFocus();}
                else if(pressed.Contains(257))CommitInline();
                else if((viewer.Key(341)||viewer.Key(345))&&pressed.Contains(65)){inlineValue="";frameText="";gui.Focus(inlineId);}
            }
            else if(textTitle is not null)HandleText();else if(choiceMenu is null&&gui.Focused is null)
            {
                characters.Clear();
                for(int i=0;i<4;i++)if(pressed.Contains(49+i)){view=i;selectedPair=-1;fieldPage=0;}
                bool control=viewer.Key(341)||viewer.Key(345);
                if(control&&pressed.Contains(83))SaveProject();
                if(control&&pressed.Contains(90)&&undo.TryPop(out string? old)){redo.Push(document.Serialize());Restore(old);status="UNDO";}
                if(control&&pressed.Contains(89)&&redo.TryPop(out string? next)){undo.Push(document.Serialize());Restore(next);status="REDO";}
                if(pressed.Contains(256)){selectedModule=selectedLink="";selectedPair=-1;linking=false;linkOrigin=null;}
                if(pressed.Contains(266))fieldPage=Math.Max(0,fieldPage-1);if(pressed.Contains(267))fieldPage++;
                if(viewer.Key(263))yaw-=.025f;if(viewer.Key(262))yaw+=.025f;if(viewer.Key(265))pitch-=.025f;if(viewer.Key(264))pitch+=.025f;
                pitch=Math.Clamp(pitch,-1.5f,1.5f);
                if(viewer.Key(61))zoom=Math.Min(20,zoom*1.02f);if(viewer.Key(45))zoom=Math.Max(.03f,zoom/1.02f);
                if(pressed.Contains(70))ViewState.Reset();
                shift=viewer.Key(340)||viewer.Key(344);
                dragAxis=viewer.Key(88)?'X':viewer.Key(89)?'Y':viewer.Key(90)?'Z':' ';
                bool inGraph=CanvasContent.Contains(new vec2(mouseX/gui.Scale,mouseY/gui.Scale))&&view is 0 or 1 or 3;
                if(inGraph&&!dragging&&!pairDragging&&!gizmoDragging)
                {
                    if(viewer.MouseButton(1)&&rightWasDown)cameraPan+=NavigationCamera.PanTranslation(new((mouseX-oldMouseX)/gui.Scale,(mouseY-oldMouseY)/gui.Scale));
                    if(viewer.MouseButton(2)&&middleWasDown){yaw-=(mouseX-oldMouseX)*.006f;pitch=Math.Clamp(pitch+(mouseY-oldMouseY)*.006f,-1.5f,1.5f);}
                    if(wheel!=0)
                    {
                        var before=NavigationCamera;zoom=Math.Clamp(zoom*MathF.Exp(wheel*.12f),.03f,20);
                        cameraPan+=before.ZoomAnchorTranslation(NavigationCamera,new(mouseX/gui.Scale,mouseY/gui.Scale));
                    }
                }
            }
            viewer.DrawCanvas(Draw,$"Brain Editor | {document.Name} | {projectPath}");
            wheel=0;middleWasDown=viewer.MouseButton(2);
            mouseWasDown=mouseDown;rightWasDown=viewer.MouseButton(1);oldMouseX=mouseX;oldMouseY=mouseY;
            if(!mouseDown)
            {
                if((dragging||gizmoDragging)&&dragCheckpoint&&document.Links.Any(l=>l.Pattern==LinkPattern.Nearest))topologyRevision++;
                dragging=false;pairDragging=false;gizmoDragging=false;
            }
            Thread.Sleep(10);
        }
    }
    private void SaveProject()=>Attempt(()=>{document.Save(projectPath);status=$"PROJECT SAVED: {projectPath}";});
    private void Text(string title,string value,Action<string> commit){textTitle=title;textValue=value;textCommit=commit;characters.Clear();focusTextPending=true;}
    private void HandleText()
    {
        if((viewer.Key(341)||viewer.Key(345))&&pressed.Contains(65)){textValue="";frameText="";gui.Focus("value");}
        if(pressed.Contains(256)){textTitle=null;gui.ClearFocus();return;}
    }

    private void Rect(float x,float y,float w,float h,float r,float g,float b)=>gui.Rect(new GuiRect(x,y,w,h),new GuiColor((byte)(r*255),(byte)(g*255),(byte)(b*255)));
    private void Label(string s,float x,float y,int scale=2)=>gui.Text(new vec2(x,y),s,gui.Style.Text,scale==1?9:14);
    private bool Hit(float x,float y,float w,float h)=>textTitle is null&&mouseX>=x&&mouseX<x+w&&mouseY>=y&&mouseY<y+h;
    private bool Button(string title,float x,float y,float w=130,bool active=false)
    {
        var area=new GuiRect(x,y,w,30);
        var style=gui.Style;
        gui.Style=style with {TextSize=title.Length*9+16<=w?12:9};
        bool result=gui.Button(area,title,textTitle is null&&choiceMenu is null&&!uiCaptured);
        gui.Style=style;
        if(active)gui.Rect(new GuiRect(x,y+28,w,2),GuiColor.FromRgb(0x57D6C4));
        return result;
    }
    public void RenderPreview(float w,float h,int selectedView=0,GuiInput? input=null)
    {
        view=selectedView;
        if(input is {} state)
        {
            mouseX=state.Cursor.x;mouseY=state.Cursor.y;mouseDown=state.MouseDown;click=mouseDown&&!mouseWasDown;
            frameText=state.Text??"";frameKeys=state.Keys;wheel=state.Scroll;uiCaptured=choiceMenu is not null;
            pressed.Clear();if((state.Keys&GuiKeys.Enter)!=0)pressed.Add(257);if((state.Keys&GuiKeys.Escape)!=0)pressed.Add(256);
            if(inlineId is not null)
            {
                if(pressed.Contains(256)){inlineId=null;gui.ClearFocus();}
                else if(pressed.Contains(257))CommitInline();
            }
        }
        viewer.Graph.Clear();Draw(w,h);
        if(input is not null)
        {
            mouseWasDown=mouseDown;oldMouseX=mouseX;oldMouseY=mouseY;
            if(!mouseDown){gizmoDragging=false;dragging=false;pairDragging=false;}
        }
    }
    private void Draw(float w,float h)
    {
        float scale=Math.Max(1,Math.Min(w/1100f,h/740f));
        gui.Begin(new GuiInput { Cursor=new vec2(mouseX,mouseY),MouseDown=mouseDown, Scroll=wheel, Text=frameText,Keys=frameKeys,Time=(double)Stopwatch.GetTimestamp()/Stopwatch.Frequency },new vec2(w,h),scale);
        mouseX/=scale;mouseY/=scale;
        w/=scale;h/=scale;width=w;height=h;
        if(inlineId is not null&&click&&!inlineRect.Contains(new vec2(mouseX,mouseY)))
            if(!CommitInline()){uiCaptured=true;click=false;}
        gui.Gradient(new GuiRect(0,0,w,h),GuiColor.FromRgb(0x101E30),GuiColor.FromRgb(0x080F19));
        Rect(0,0,w,52,.03f,.065f,.09f);Label("MYT / BRAIN STUDIO",18,18,2);
        Label($"{document.Modules.Count} MODULES / {document.Links.Count} LINKS",760,76,1);
        string[] tabs=["1 LINK","2 EDIT","3 REWARDS","4 FINAL NETWORK"];
        for(int i=0;i<4;i++)if(Button(tabs[i],240+i*165,10,155,view==i)){view=i;selectedPair=-1;fieldPage=0;}
        if(Button("SAVE PROJECT",16,64,120))SaveProject();
        if(Button("SAVE AS",144,64,100))Text("PROJECT SAVE PATH",projectPath,value=>{document.Save(value);projectPath=value;status="PROJECT SAVED";});
        if(Button("OPEN PROJECT",252,64,120))Text("OPEN PROJECT PATH",projectPath,value=>{var loaded=BrainDocument.Parse(File.ReadAllText(value));Checkpoint();document=loaded;projectPath=value;selectedModule=document.Modules.FirstOrDefault()?.Id??"";selectedLink="";selectedGroup=-1;selectedPair=-1;status="PROJECT OPENED";});
        if(Button("OPEN BAKED",380,64,110))Text("OPEN BAKED DIRECTORY","artifacts/editor/baked",OpenBaked);
        if(Button("UNDO",498,64,65)&&undo.TryPop(out string? old)){redo.Push(document.Serialize());Restore(old);}
        if(Button("REDO",571,64,65)&&redo.TryPop(out string? next)){undo.Push(document.Serialize());Restore(next);}
        if(Button("RENAME",644,64,85))Text("PROJECT NAME",document.Name,value=>{Checkpoint();document.Name=value;});
        gui.Gradient(new GuiRect(12,104,w-24,h-174),GuiColor.FromRgb(0x101E2B),GuiColor.FromRgb(0x0A1420));
        Attempt(()=>{switch(view){case 0:DrawLinks();break;case 1:DrawModule();break;case 2:DrawRewards();break;case 3:DrawBaked();break;}});
        if(view is 0 or 1 or 3)
        {
            gui.Outline(CanvasRect,GuiColor.FromRgb(0x6388A5),1);
            if(view!=0)gui.Rect(new GuiRect(CanvasRect.X,CanvasContent.Bottom,CanvasRect.Width,1),GuiColor.FromRgb(0x39556F));
        }
        Rect(0,h-62,w,62,.035f,.08f,.10f);Label(status[..Math.Min(status.Length,Math.Max(1,(int)w/7))],16,h-50,1);
        Label("RMB PAN / MMB ORBIT / WHEEL ZOOM / DRAG MOVE / SHIFT DEPTH / HOLD X Y Z AXIS / F FIT / CTRL S SAVE / CTRL Z-Y HISTORY",16,h-28,1);
        if(textTitle is not null)
        {
            var bar=new GuiRect(12,h-66,w-24,62);
            gui.Rect(bar,GuiColor.FromRgb(0x24394E));
            Label(textTitle+" | ENTER APPLY / ESC CANCEL",24,bar.Y+5,1);
            string value=textValue??"";
            gui.TextField("value",new GuiRect(24,bar.Y+23,w-260,32),ref value,1_000_000);
            textValue=value;
            if(pressed.Contains(257))Attempt(()=>{textCommit!(textValue!);textTitle=null;gui.ClearFocus();});
            if(gui.Button(new GuiRect(w-220,bar.Y+23,92,30),"CANCEL")){textTitle=null;gui.ClearFocus();}
            if(gui.Button(new GuiRect(w-120,bar.Y+23,96,30),"APPLY"))Attempt(()=>{textCommit!(textValue!);textTitle=null;gui.ClearFocus();});
        }
        DrawChoices();
        gui.End();
        if(focusTextPending){gui.Focus("value");focusTextPending=false;}
        viewer.RenderGui(gui);
        mouseX*=scale;mouseY*=scale;
    }

    private static GuiColor ModuleColor(ModuleKind kind)=>GuiColor.FromRgb(kind switch
    {
        ModuleKind.InputPanel=>0x64CDEB,ModuleKind.OutputPanel=>0xB499EF,
        ModuleKind.Reward=>0xF4BE69,ModuleKind.Pain=>0xEF8490,ModuleKind.Clock=>0x82A5EF,_=>0x6CDBB2
    });
    private PerspectiveCamera Camera(Position center,float fit,float span)
        =>PerspectiveCamera.Create(new Vector3(center.X,center.Y,center.Z)+cameraPan,new(CanvasContent.Center.x,CanvasContent.Center.y),fit,zoom,
            CanvasContent.Height,Math.Max(2,span),yaw,pitch);

    private void DrawLinks()
    {
        float side=width-450;
        if(Button("ADD "+((ModuleKind)kindIndex),22,116,190))
        {
            Checkpoint();var m=new BrainModule { Kind=(ModuleKind)kindIndex,Name=((ModuleKind)kindIndex).ToString().ToUpperInvariant(),Position=new(document.Modules.Count*3-6,4,0) };
            if(m.Kind==ModuleKind.Lattice)m.AxisSizes=[6,6];
            if(m.Kind==ModuleKind.Custom)m.Neurons.Add(new() { Role=NeuronRole.Output });document.Modules.Add(m);selectedModule=m.Id;selectedLink="";
        }
        Choose(new(220,116,90,30),"TYPE",Enum.GetNames<ModuleKind>(),i=>kindIndex=i);
        if(Button(linking?"CANCEL LINK":"CREATE LINK",318,116,130,linking)){linking=!linking;linkOrigin=null;status="SELECT SOURCE THEN DESTINATION MODULE";}
        DrawLinkScene();
        Rect(side,104,438,height-174,.04f,.08f,.11f);
        if(selectedLink!=""&&document.Links.FirstOrDefault(l=>l.Id==selectedLink) is {} selected)
        {
            Label("LINK",side+14,118,2);
            Inline("link-name",new(side+14,148,402,30),selected.Name,value=>{Checkpoint();selected.Name=value;});
            if(Button("EDIT LINK",side+14,190,160)){view=1;fieldPage=0;}
            if(Button("DELETE",side+182,190,100)){Checkpoint();document.Links.Remove(selected);selectedLink="";return;}
            Label("EDIT LINK TO CHOOSE GROUPS AND WIRING",side+14,242,1);
            Label("FROM: "+(document.Modules.FirstOrDefault(m=>m.Id==selected.Source.ModuleId)?.Name??"MISSING"),side+14,275,1);
            Label("TO: "+(document.Modules.FirstOrDefault(m=>m.Id==selected.Destination.ModuleId)?.Name??"MISSING"),side+14,299,1);
        }
        else if(Module is {} module)
        {
            Label("MODULE",side+14,118,2);
            if(Button("EDIT",side+14,148,100)){view=1;selectedPair=-1;fieldPage=0;}
            if(Button("DELETE",side+124,148,100)){Checkpoint();document.Modules.Remove(module);document.Links.RemoveAll(l=>l.Source.ModuleId==module.Id||l.Destination.ModuleId==module.Id);foreach(var group in document.RewardGroups)group.ModuleIds.Remove(module.Id);document.RewardGroups.RemoveAll(g=>g.RewardModuleId==module.Id);selectedModule=document.Modules.FirstOrDefault()?.Id??"";return;}
            DrawTransformMode(side+234,148);
            Label("EDIT MODULE TO CHANGE NEURONS AND GROUPS",side+14,190,1);
            Inspector(module,side+14,220,ReplaceModule,typeof(BrainModule),["Name","Position","Rotation"]);
        }
    }
    private void ReplaceModule(object value)
    {
        var replacement=(BrainModule)value;var generated=Generation.Generate(replacement);NeuronGroups.Resolve(replacement,generated.Neurons);Checkpoint();int index=document.Modules.FindIndex(m=>m.Id==replacement.Id);document.Modules[index]=replacement;
    }
    private void DrawModule()
    {
        float side=width-450;
        if(selectedLink!=""&&document.Links.FirstOrDefault(l=>l.Id==selectedLink) is {} link){DrawLinkEditor(link);return;}
        if(Module is not {} module){Label("SELECT OR ADD A MODULE IN LINK VIEW",30,150);return;}
        Label(module.Name+" / "+module.Kind,24,118,2);
        if(Button("NEXT MODULE",24,148,130)){int index=document.Modules.IndexOf(module);selectedModule=document.Modules[(index+1)%document.Modules.Count].Id;selectedPair=-1;fieldPage=0;return;}
        if(Button("ADD ARRAY",162,148,110)){Checkpoint();module.Modifiers.Add(new());}
        if(Button("REMOVE ARRAY",280,148,130)&&module.Modifiers.Count>0){Checkpoint();module.Modifiers.RemoveAt(module.Modifiers.Count-1);}
        if(module.Kind==ModuleKind.Custom)
        {
            if(Button("ADD PAIR",24,186,100)){Checkpoint();module.Neurons.Add(new() { Position=new(module.Neurons.Count,0,0) });selectedPair=module.Neurons.Count-1;}
            if(Button("CONNECT",132,186,100,customLinking)){customLinking=!customLinking;customOrigin=null;status="CLICK TWO PAIRS TO CONNECT";}
            if(Button("DELETE PAIR",240,186,120)&&selectedPair>=0&&selectedPair<module.Neurons.Count)
            {
                Checkpoint();string id=module.Neurons[selectedPair].Id;module.Neurons.RemoveAt(selectedPair);module.Edges.RemoveAll(e=>e.Source==id||e.Destination==id);selectedPair=-1;
            }
        }

        try
        {
            if(previewDirty||preview is null||!ReferenceEquals(previewModule,module))
            {
                preview=Generation.Generate(module);previewModule=module;previewDirty=false;metrics=null;
                previewEdges=preview.Edges.Select(e=>(e.Source,e.Destination,e.Weight)).ToArray();
            }
            if(metrics is null&&!pairDragging)metrics=NetworkMetrics.Measure(preview);
            DrawGraph(preview!.Neurons,previewEdges,true);
        }
        catch(Exception e){preview=null;metrics=null;status=e.Message;Label("PREVIEW INVALID - CORRECT MODULE SETTINGS",24,240,1);}
        Rect(side,104,438,height-174,.04f,.08f,.11f);Label("PROPERTIES",side+14,118,2);
        if(selectedPair>=0&&module.Kind==ModuleKind.Custom&&module.Modifiers.Count==0&&selectedPair<module.Neurons.Count)
        {
            if(Button("MODULE SETTINGS",side+14,148,160))selectedPair=-1;
            int index=selectedPair;
            if(index>=0)Inspector(module.Neurons[index],side+14,190,obj=>
            {
                var replacement=JsonSerializer.Deserialize<BrainModule>(JsonSerializer.Serialize(module,BrainDocument.Json),BrainDocument.Json)!;
                replacement.Neurons[index]=(CustomNeuron)obj;Generation.Generate(replacement);Checkpoint();module.Neurons[index]=(CustomNeuron)obj;
            },typeof(CustomNeuron));
        }
        else
        {
            if(DrawModuleTabs(side+14,146)){DrawGroups(module,side+14,190);return;}
            if(module.Kind==ModuleKind.Hub&&hubDistributionTab){DrawHubDistribution(module,side+14,190);return;}
            var fields=new List<string>{"Name","WeightScale","Plasticity","Seed","Position","Rotation","Modifiers"};
            switch(module.Kind)
            {
                case ModuleKind.Hub:fields.InsertRange(1,["NeuronCount","PowerLaw"]);break;
                case ModuleKind.Lattice:fields.InsertRange(1,["Dimensions","AxisSizes","Neighbors","Shape","CustomPositions","Imperfection"]);break;
                case ModuleKind.Layered:fields.InsertRange(1,["LayerSizes","Crosses","ResidualStep","ResidualDensity","FixedResidual"]);break;
                case ModuleKind.InputPanel:case ModuleKind.OutputPanel:fields.AddRange(["Panel","Channels","ChannelBindings","Width","Height","ResolutionDecay","MinFrequency","MaxFrequency","SampleRate"]);break;
                case ModuleKind.Clock:fields.AddRange(["Frequency","Amplitude","Phase"]);break;
                case ModuleKind.Pain:fields.AddRange(["Outputs","Frequency"]);break;
                case ModuleKind.Reward:fields.Add("Outputs");break;
                case ModuleKind.Custom:fields.AddRange(["Neurons","Edges"]);break;
            }
            Inspector(module,side+14,182,ReplaceModule,typeof(BrainModule),fields.ToArray());
        }
    }
    private void FrameGraph(IReadOnlyList<GeneratedNeuron> ns)
    {
        if(ViewState.FrameCenter is null||ViewState.FrameSpan<=0)
        {
            graphCenter=new((ns.Min(n=>n.Position.X)+ns.Max(n=>n.Position.X))/2,(ns.Min(n=>n.Position.Y)+ns.Max(n=>n.Position.Y))/2,(ns.Min(n=>n.Position.Z)+ns.Max(n=>n.Position.Z))/2);
            graphSpan=Math.Max(2,Math.Max(ns.Max(n=>n.Position.X)-ns.Min(n=>n.Position.X),Math.Max(ns.Max(n=>n.Position.Y)-ns.Min(n=>n.Position.Y),ns.Max(n=>n.Position.Z)-ns.Min(n=>n.Position.Z))));
            ViewState.FrameCenter=graphCenter;ViewState.FrameSpan=graphSpan;
        }
        graphCenter=ViewState.FrameCenter.Value;graphSpan=ViewState.FrameSpan;
        graphFit=Fit(graphSpan);
    }
    private void DrawGraph(IReadOnlyList<GeneratedNeuron> ns,(int Source,int Destination,float Weight)[] edges,bool select)
    {
        if(ns.Count==0)return;
        FrameGraph(ns);
        var camera=Camera(graphCenter,graphFit,graphSpan);
        var points=new Vector3[ns.Count];var viewPoints=new Vector3[ns.Count];
        for(int i=0;i<points.Length;i++)
        {
            var p=ns[i].Position;
            viewPoints[i]=camera.ToView(new(p.X,p.Y,p.Z));
            camera.TryProject(new(p.X,p.Y,p.Z),out points[i]);
        }
        var batch=viewer.Graph;
        var clip=CanvasContent;
        batch.Clip=clip.Scaled(gui.Scale);
        DrawOrientation(clip,graphCenter,graphFit,true);
        var renderModules=(view==3?baked!.Manifest.Modules:document.Modules).ToDictionary(m=>m.Id);
        int shownNodes=0,shownEdges=0;
        Vector3 Pixel(Vector3 p)=>new(p.X*gui.Scale,p.Y*gui.Scale,p.Z);
        shownEdges=BrainGraphDrawing.Edges(batch,camera,clip,viewPoints,edges,gui.Scale);
        var clockLabels=new List<(Vector3 Point,string Name)>();var namedClocks=new HashSet<string>();
        int hovered=-1;float distance=64,nearestDepth=float.PositiveInfinity;
        for(int i=0;i<points.Length;i++)
        {
            var p=points[i];
            var n=ns[i];float perspectiveScale=Math.Clamp(camera.Distance/viewPoints[i].Z,.4f,2.5f);
            float size=(selectedPair==i?10:6)*perspectiveScale;
            float radius=size*.5f+(selectedPair==i?3:0);
            if(!ViewportGeometry.MarkerVisible(clip,p,radius))continue;shownNodes++;
            var color=BrainGraphDrawing.NeuronColor(n,renderModules.GetValueOrDefault(n.ModuleId)?.Kind??ModuleKind.Custom);
            if(view==1&&selectedLink==""&&groupsTab&&highlightedGroupModule==n.ModuleId&&highlightedGroup?.Contains(i)==true)color=GuiColor.FromRgb(0xFFE19D);
            if(selectedPair==i)batch.Neuron(Pixel(p),(size*.5f+3)*gui.Scale,GuiColor.White);
            BrainGraphDrawing.Neuron(batch,p,size*.5f,color,gui.Scale);
            if(view==3&&renderModules.GetValueOrDefault(n.ModuleId)?.Kind==ModuleKind.Clock&&namedClocks.Add(n.ModuleId))
                clockLabels.Add((p,renderModules[n.ModuleId].Name));
            float d=(p.X-mouseX)*(p.X-mouseX)+(p.Y-mouseY)*(p.Y-mouseY);
            if(clip.Contains(new vec2(mouseX,mouseY))&&d<=distance&&(d<distance-.1f||p.Z<nearestDepth)){hovered=i;distance=d;nearestDepth=p.Z;}
        }
        var clockOccupied=new List<GuiRect> {new(clip.Right-100,clip.Top+10,96,90)};
        foreach(var label in clockLabels)
        {
            string text=label.Name[..Math.Min(32,label.Name.Length)];
            float w=Math.Min(clip.Width-8,gui.Style.Font.Measure(text)*9/gui.Style.Font.Height+8);
            var rect=new GuiRect(Math.Clamp(label.Point.X+8,clip.Left+4,clip.Right-w-4),Math.Clamp(label.Point.Y-10,clip.Top+4,clip.Bottom-22),w,18);
            var arranged=GraphLabelLayout.Place(rect,new(clip.Left+4,clip.Top+4,clip.Width-8,clip.Height-8),clockOccupied);
            clockOccupied.Add(arranged);
            if(arranged!=rect)batch.Guide(Pixel(label.Point),Pixel(new(arranged.Center.x,arranged.Center.y,.5f)),GraphOrientation.ZColor.WithAlpha(.5f));
            viewer.GraphLabel(gui,arranged,text,GraphOrientation.ZColor);
        }
        if(hovered>=0&&textTitle is null&&!uiCaptured&&choiceMenu is null)
        {
            var n=ns[hovered];
            Label($"PAIR {hovered} {n.Role} CAP {n.ActivationCap} XYZ {n.Position.X:F2} {n.Position.Y:F2} {n.Position.Z:F2}",CanvasRect.X+10,CanvasContent.Bottom+22,1);
            if(select&&click&&!uiCaptured&&choiceMenu is null)
            {
                selectedPair=hovered;fieldPage=0;
                if(Module is {Kind:ModuleKind.Custom,Modifiers.Count:0} custom)
                {
                    if(customLinking)
                    {
                        if(customOrigin is null)customOrigin=hovered;
                        else if(customOrigin.Value!=hovered){Checkpoint();custom.Edges.Add(new() {Source=custom.Neurons[customOrigin.Value].Id,Destination=custom.Neurons[hovered].Id});customOrigin=null;customLinking=false;}
                    }
                    else {pairDragging=true;dragCheckpoint=false;}
                }
            }
        }
        if(select&&click&&hovered<0&&CanvasContent.Contains(new vec2(mouseX,mouseY))&&textTitle is null&&!uiCaptured&&choiceMenu is null){selectedPair=-1;customOrigin=null;}
        if(pairDragging&&mouseDown&&!click&&Module is {Kind:ModuleKind.Custom,Modifiers.Count:0} module&&selectedPair>=0&&selectedPair<module.Neurons.Count)
        {
            var mouseDelta=new Vector2(mouseX-oldMouseX/gui.Scale,mouseY-oldMouseY/gui.Scale);
            if(mouseDelta.LengthSquared()>0)
            {
                if(!dragCheckpoint){Checkpoint();dragCheckpoint=true;}
                var position=ns[selectedPair].Position;
                var delta=camera.Drag(new(position.X,position.Y,position.Z),mouseDelta,shift,dragAxis);
                var neuron=module.Neurons[selectedPair];
                neuron.Position+=ModuleTransform.Position(Vector3.Transform(delta,System.Numerics.Quaternion.Inverse(ModuleTransform.Rotation(module.Rotation))));Invalidate();
            }
        }
        Label($"{ns.Count:N0} PAIRS / {edges.Length:N0} EDGES | SHOWN {shownNodes} / {shownEdges}",CanvasRect.X+10,CanvasContent.Bottom+7,1);
        if(hovered<0&&select&&metrics is not null)
            Label($"CLUSTER {metrics.Clustering:F3} | PATH {metrics.MeanPath:F2} | REACH {metrics.ReachableFraction:P0} | {metrics.SampledSources} SOURCES",CanvasRect.X+10,CanvasContent.Bottom+22,1);
    }

    private void DrawRewards()
    {
        float side=width-450;
        if(Button("ADD GROUP",24,116,130))
        {
            var reward=document.Modules.FirstOrDefault(m=>m.Kind==ModuleKind.Reward);
            Checkpoint();if(reward is null){reward=new() { Kind=ModuleKind.Reward,Name="REWARD",Outputs=1,Position=new(document.Modules.Count*3,4,0) };document.Modules.Add(reward);}
            document.RewardGroups.Add(new() { Name="Group "+(document.RewardGroups.Count+1),RewardModuleId=reward.Id });selectedGroup=document.RewardGroups.Count-1;fieldPage=0;
        }
        int pageSize=Math.Max(1,(int)(height-350)/38);
        groupPage=Math.Clamp(groupPage,0,Math.Max(0,(document.RewardGroups.Count-1)/pageSize));
        if(Button("< GROUPS",24,height-140,100))groupPage=Math.Max(0,groupPage-1);
        if(Button("GROUPS >",132,height-140,100))groupPage=Math.Min(Math.Max(0,(document.RewardGroups.Count-1)/pageSize),groupPage+1);
        int groupRow=0;
        foreach(var group in document.RewardGroups.Skip(groupPage*pageSize).Take(pageSize).ToArray())
        {
            if(Button(group.Name,24,160+groupRow*38,220,selectedGroup==groupPage*pageSize+groupRow)){selectedGroup=groupPage*pageSize+groupRow;fieldPage=0;}groupRow++;
        }
        if(selectedGroup<0||selectedGroup>=document.RewardGroups.Count){Label("CREATE OR SELECT A REWARD GROUP",270,160,1);return;}
        var selected=document.RewardGroups[selectedGroup];Label("MEMBER MODULES - CLICK TO TOGGLE",270,120,1);
        memberPage=Math.Clamp(memberPage,0,Math.Max(0,(document.Modules.Count-1)/pageSize));
        if(Button("< MEMBERS",270,height-140,110))memberPage=Math.Max(0,memberPage-1);
        if(Button("MEMBERS >",388,height-140,110))memberPage=Math.Min(Math.Max(0,(document.Modules.Count-1)/pageSize),memberPage+1);
        int memberRow=0;
        foreach(var module in document.Modules.Skip(memberPage*pageSize).Take(pageSize))
        {
            bool member=selected.ModuleIds.Contains(module.Id);float y=160+memberRow++*38;if(y>height-160)break;
            if(Button((member?"+ ":"- ")+module.Name,270,y,Math.Max(130,side-290),member))
            {
                if(!member&&document.RewardGroups.Any(g=>g!=selected&&g.ModuleIds.Contains(module.Id))){status="MODULE ALREADY BELONGS TO ANOTHER REWARD GROUP";continue;}
                Checkpoint();if(member)selected.ModuleIds.Remove(module.Id);else selected.ModuleIds.Add(module.Id);
            }
        }
        Rect(side,104,438,height-174,.04f,.08f,.11f);Label("REWARD GROUP",side+14,118,2);
        if(Button("DELETE GROUP",side+14,148,140)){Checkpoint();document.RewardGroups.RemoveAt(selectedGroup);selectedGroup=-1;return;}
        if(Button("CYCLE REWARD SOURCE",side+14,186,210))
        {
            var rewards=document.Modules.Where(m=>m.Kind==ModuleKind.Reward).ToList();
            if(rewards.Count>0){int index=rewards.FindIndex(m=>m.Id==selected.RewardModuleId);Checkpoint();selected.RewardModuleId=rewards[(index+1)%rewards.Count].Id;selected.RewardNeuron=0;}
        }
        string sourceName=document.Modules.FirstOrDefault(m=>m.Id==selected.RewardModuleId)?.Name??"MISSING";Label("SOURCE: "+sourceName,side+14,230,1);
        Label("CAP 8 / DERIVATIVE OVER DT",side+14,254,1);Label("FACTOR = dR/dt; HOST MAY SET CONSTANTS",side+14,274,1);
        Inspector(selected,side+14,310,obj=>{Checkpoint();document.RewardGroups[selectedGroup]=(RewardGroup)obj;},typeof(RewardGroup),["Name","RewardNeuron"]);
    }
    private void DrawBaked()
    {
        float side=width-450;
        if(Button("BAKE PROJECT",24,116,150))Attempt(()=>{baked=Baking.Bake(document);bakedSource=document.Serialize();status="BAKED CURRENT PROJECT - READY TO SAVE NETWORK";});
        if(Button("SAVE BAKED",182,116,140))
        {
            if(baked is null){status="BAKE THE PROJECT FIRST";}
            else Text("SAVE BAKED NETWORK TO NEW DIRECTORY","artifacts/editor/baked-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"),path=>{baked.Save(path);status="BAKED NETWORK SAVED: "+path;});
        }
        if(baked is null){Label("BAKE THE PROJECT TO COMPILE ALL MODULES AND LINKS",24,180,1);return;}
        if(!ReferenceEquals(edgeBake,baked))
        {
            edgeBake=baked;bakedEdges=baked.Model.Topology.Edges.Select((e,i)=>(e.Source,e.Destination,baked.Model.Weights[i])).ToArray();
        }
        DrawGraph(baked.Manifest.Neurons,bakedEdges,false);
        Rect(side,104,438,height-174,.04f,.08f,.11f);Label("FINAL NETWORK",side+14,118,2);
        Label($"PAIRS: {baked.Model.NormalCount}",side+14,164,2);Label($"EDGES: {baked.Model.ParameterCount}",side+14,196,2);
        Label($"EXTERNAL INPUTS: {baked.Model.Topology.InputCount}",side+14,238,1);Label($"READOUTS: {baked.Model.Topology.Outputs.Length}",side+14,260,1);
        Label($"REWARD GROUPS: {baked.Manifest.RewardGroups.Count}",side+14,282,1);Label("REWARD CAP 8 / OTHER CAPS 1",side+14,310,1);
        var clockIds=baked.Manifest.Modules.Where(m=>m.Kind==ModuleKind.Clock).Select(m=>m.Id).ToHashSet();
        Label($"CLOCK INPUTS: {baked.Manifest.Neurons.Count(n=>clockIds.Contains(n.ModuleId))}",side+14,330,1);
        Label(bakedSource is null?"INSPECTING OPENED BAKED NETWORK":"BAKED FROM CURRENT PROJECT",side+14,350,1);
        if(bakedSource is null&&Button("EDIT SOURCE AS PROJECT",side+14,384,250))
        {
            var source=BrainDocument.Parse(baked.Manifest.SourceDocument);Checkpoint();document=source;selectedModule=source.Modules.FirstOrDefault()?.Id??"";view=0;status="SOURCE RESTORED - SAVE AS A PROJECT";
        }
    }
    private void Inspector(object target,float x,float y,Action<object> apply,Type type,string[]? fields=null)
    {
        PropertyInspector(target,x,y,apply,type,fields);
    }
}
