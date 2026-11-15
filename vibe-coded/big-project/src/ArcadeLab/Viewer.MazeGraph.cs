using ArcadeLab.Editor;
using MYT.Kit.Gui;
using System.Numerics;

namespace ArcadeLab;

public sealed unsafe partial class Viewer
{
    public bool MazeGraphVisible { get; set; }=true;
    private MazeSession? mazeGraphSession;
    private GeneratedNeuron[] mazeGraphNeurons=[];
    private (int Source,int Destination,float Weight)[] mazeGraphEdges=[];
    private Dictionary<string,ModuleKind> mazeGraphKinds=[];
    private (string Name,Vector3 Min,Vector3 Max,GuiColor Color)[] mazeGraphModules=[];
    private Vector3 mazeGraphCenter,mazeGraphPan;
    private float mazeGraphSpan,mazeGraphYaw=GraphCamera.DefaultYaw,mazeGraphPitch=GraphCamera.DefaultPitch,mazeGraphZoom=1,mazeGraphWheel;
    private Vector2 mazeGraphCursor;
    private double mazeGraphWeightsRead;
    private bool mazeGraphRight,mazeGraphMiddle,mazeGraphScrollRegistered;

    private void MazeEditorGraph(MazeSession session,float w,float h)
    {
        if(!MazeGraphVisible)return;
        var clip=new GuiRect(.67f*w,.09f*h,.315f*w,.305f*h);
        Rect(.66f*w,.025f*h,.335f*w,.505f*h,.025f,.04f,.065f);
        if(!ReferenceEquals(mazeGraphSession,session))
        {
            mazeGraphSession=session;mazeGraphPan=Vector3.Zero;mazeGraphZoom=1;mazeGraphWeightsRead=0;
            mazeGraphYaw=GraphCamera.DefaultYaw;mazeGraphPitch=GraphCamera.DefaultPitch;
            if(session.Baked is {} baked)
            {
                mazeGraphNeurons=baked.Brain.Manifest.Neurons.ToArray();
                mazeGraphKinds=baked.Brain.Manifest.Modules.ToDictionary(m=>m.Id,m=>m.Kind);
            }
            else
            {
                // Legacy networks use their actual layer positions with the same editor renderer.
                var topology=session.MainModel.Topology;var outputs=topology.Outputs.ToHashSet();
                mazeGraphNeurons=Enumerable.Range(0,topology.NodeCount).Select(i=>
                {
                    bool input=i<topology.InputCount,output=outputs.Contains(i);
                    int layer=input?0:output?session.Config.Layers+1:1+(i-topology.InputCount)/session.Config.Width;
                    int row=input?i:output?Array.IndexOf(topology.Outputs,i):(i-topology.InputCount)%session.Config.Width;
                    int count=input?topology.InputCount:output?topology.Outputs.Length:session.Config.Width;
                    return new GeneratedNeuron(i.ToString(),"layer"+layer,input?NeuronRole.Input:output?NeuronRole.Output:NeuronRole.Hidden,new(layer*8,(row%32-16)*.4f,(row/32-count/64f)*.4f),input,1);
                }).ToArray();
                mazeGraphKinds=[];
            }
            mazeGraphModules=mazeGraphNeurons.GroupBy(n=>n.ModuleId).Select(group=>
            {
                var lo=new Vector3(float.PositiveInfinity);var hi=new Vector3(float.NegativeInfinity);
                foreach(var neuron in group){var point=ModuleTransform.Vector(neuron.Position);lo=Vector3.Min(lo,point);hi=Vector3.Max(hi,point);}
                string name=session.Baked?.Brain.Manifest.Modules.FirstOrDefault(m=>m.Id==group.Key)?.Name??group.Key;
                return (name,lo-new Vector3(.45f),hi+new Vector3(.45f),BrainGraphDrawing.NeuronColor(group.First(),mazeGraphKinds.GetValueOrDefault(group.Key,ModuleKind.Custom)));
            }).ToArray();
            var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
            foreach(var n in mazeGraphNeurons){var p=ModuleTransform.Vector(n.Position);min=Vector3.Min(min,p);max=Vector3.Max(max,p);}
            mazeGraphCenter=(min+max)/2;var size=max-min;mazeGraphSpan=Math.Max(2,Math.Max(size.X,Math.Max(size.Y,size.Z)));
            mazeGraphEdges=session.MainModel.Topology.Edges.Select((e,i)=>(e.Source,e.Destination,session.MainModel.Weights[i])).ToArray();
        }
        float fit=Math.Min(clip.Width,clip.Height)*.72f/mazeGraphSpan;
        PerspectiveCamera Camera()=>PerspectiveCamera.Create(mazeGraphCenter+mazeGraphPan,new(clip.Center.x,clip.Center.y),fit,mazeGraphZoom,clip.Height,mazeGraphSpan,mazeGraphYaw,mazeGraphPitch);
        var camera=Camera();var cursor=window==0?new Vector2(-100):new Vector2(Cursor.X,Cursor.Y);
        bool inside=clip.Contains(new GlmSharp.vec2(cursor.X,cursor.Y));
        bool right=window!=0&&MouseButton(1),middle=window!=0&&MouseButton(2);
        if(inside)
        {
            var delta=cursor-mazeGraphCursor;
            if(right&&mazeGraphRight)mazeGraphPan+=camera.PanTranslation(delta);
            if(middle&&mazeGraphMiddle){mazeGraphYaw+=delta.X*.008f;mazeGraphPitch=Math.Clamp(mazeGraphPitch+delta.Y*.008f,-1.5f,1.5f);}
            if(mazeGraphWheel!=0)
            {
                var previous=Camera();mazeGraphZoom=Math.Clamp(mazeGraphZoom*MathF.Pow(1.12f,mazeGraphWheel),.03f,20);
                mazeGraphPan+=previous.ZoomAnchorTranslation(Camera(),cursor);
            }
            camera=Camera();
        }
        mazeGraphCursor=cursor;mazeGraphRight=right;mazeGraphMiddle=middle;mazeGraphWheel=0;
        Label("LIVE NETWORK / EDITOR 3D VIEW",.675f*w,.04f*h,1);
        Label("RIGHT DRAG PAN / MIDDLE ORBIT / WHEEL ZOOM",.675f*w,.06f*h,1);
        var views=new Vector3[mazeGraphNeurons.Length];var screens=new Vector3[views.Length];
        for(int i=0;i<views.Length;i++)
        {
            var world=ModuleTransform.Vector(mazeGraphNeurons[i].Position);views[i]=camera.ToView(world);camera.TryProject(world,out screens[i]);
        }
        Graph.Clip=clip;
        double now=(double)System.Diagnostics.Stopwatch.GetTimestamp()/System.Diagnostics.Stopwatch.Frequency;
        if(now-mazeGraphWeightsRead>.5)
        {
            session.Backend.DownloadParameters();
            for(int i=0;i<mazeGraphEdges.Length;i++)mazeGraphEdges[i].Weight=session.MainModel.Weights[i];
            mazeGraphWeightsRead=now;
        }
        BrainGraphDrawing.Edges(Graph,camera,clip,views,mazeGraphEdges);
        int hovered=-1;float distance=64,depth=float.PositiveInfinity;
        for(int i=0;i<screens.Length;i++)
        {
            var n=mazeGraphNeurons[i];var screen=screens[i];
            float radius=3*Math.Clamp(camera.Distance/views[i].Z,.4f,2.5f);
            if(!float.IsFinite(screen.X)||!float.IsFinite(screen.Y)||screen.X+radius<clip.Left||screen.X-radius>clip.Right||screen.Y+radius<clip.Top||screen.Y-radius>clip.Bottom)continue;
            var color=BrainGraphDrawing.NeuronColor(n,mazeGraphKinds.GetValueOrDefault(n.ModuleId,ModuleKind.Custom));
            BrainGraphDrawing.Neuron(Graph,screen,radius,color);
            if(session.NormalActivities[i]>.5f)BrainGraphDrawing.Neuron(Graph,screen,radius*.45f,GuiColor.White);
            float d=Vector2.DistanceSquared(cursor,new(screen.X,screen.Y));
            if(inside&&d<distance&&(d<distance-.1f||screen.Z<depth)){hovered=i;distance=d;depth=screen.Z;}
        }
        var occupied=new List<GuiRect>();
        foreach(var module in mazeGraphModules)
        {
            Vector3 Corner(int i)=>new((i&1)==0?module.Min.X:module.Max.X,(i&2)==0?module.Min.Y:module.Max.Y,(i&4)==0?module.Min.Z:module.Max.Z);
            for(int corner=0;corner<8;corner++)for(int axis=0;axis<3;axis++)
            {
                int other=corner^(1<<axis);if(other<corner)continue;
                if(camera.ProjectSegment(Corner(corner),Corner(other),out var a,out var b))Graph.Guide(a,b,module.Color.WithAlpha(.4f));
            }
            if(!camera.TryProject((module.Min+module.Max)/2,out var center)||center.X<clip.Left||center.X>clip.Right||center.Y<clip.Top||center.Y>clip.Bottom)continue;
            string text=module.Name[..Math.Min(22,module.Name.Length)];
            float width=Math.Min(clip.Width-8,mazeGui!.Style.Font.Measure(text)*9/mazeGui.Style.Font.Height+8);
            var desired=new GuiRect(center.X-width/2,center.Y-20,width,18);
            var arranged=GraphLabelLayout.Place(desired,clip.Inset(4),occupied);occupied.Add(arranged);
            GraphLabel(mazeGui,arranged,text,module.Color);
        }
        if(hovered>=0)
        {
            string binding=session.Baked?.InputLabels.GetValueOrDefault(hovered)??session.Baked?.OutputLabels.GetValueOrDefault(hovered)??mazeGraphNeurons[hovered].Role.ToString();
            string module=session.Baked?.Brain.Manifest.Modules.FirstOrDefault(m=>m.Id==mazeGraphNeurons[hovered].ModuleId)?.Name??mazeGraphNeurons[hovered].ModuleId;
            GraphLabel(mazeGui!,new GuiRect(clip.X+4,clip.Bottom-36,clip.Width-8,18),$"{module}: {mazeGraphNeurons[hovered].Id} -> {binding}",GuiColor.White);
            GraphLabel(mazeGui!,new GuiRect(clip.X+4,clip.Bottom-18,clip.Width-8,18),$"NODE {hovered} X {session.NormalActivities[hovered]:F3} E+ {session.ErrorActivities[2*hovered]:F3} E- {session.ErrorActivities[2*hovered+1]:F3}",GuiColor.White);
        }
        DrawMazeBindings(session,w,h);
    }
    private void DrawMazeBindings(MazeSession session,float w,float h)
    {
        float x=.675f*w,y=.405f*h;
        if(session.Baked is not {} baked)
        {Label($"{session.MainModel.NormalCount} NEURONS / {mazeGraphEdges.Length} EDGES",x,y,1);return;}
        Label("BOUND INPUTS (LIVE NEURON VALUES)",x,y,1);
        var labels=baked.InputLabels.Where(p=>!p.Value.StartsWith("tactile.")||p.Value=="tactile.0").Select(p=>p.Value=="tactile.0"?new KeyValuePair<int,string>(p.Key,"tactile[16]"):p).ToArray();int rows=(labels.Length+1)/2;
        for(int i=0;i<labels.Length;i++)
        {
            var (index,name)=labels[i];string signal=name.StartsWith("clock")?"clock":name;
            Label($"{index}:{signal} {session.NormalActivities[index]:F2}",x+(i/rows)*.16f*w,y+13+(i%rows)*12,1);
        }
    }
    private void CortexAssignedRewards(MazeSession session,float w,float h)
    {
        if(session.CortexRewards is not {} cortex)return;
        float y=.582f*h;
        Label(session.Training?"ASSIGNED REWARDS / TRAINING":"ASSIGNED REWARDS / OBSERVER ONLY",.69f*w,y,1);
        Label("CORTEX",.69f*w,y+14,1);Label("REWARD",.79f*w,y+14,1);
        Label("PUNISH",.855f*w,y+14,1);Label("NET",.92f*w,y+14,1);
        var groups=session.Baked!.Brain.Manifest.RewardGroups;
        float rowHeight=.16f*h/Math.Max(6,groups.Count);
        for(int i=0;i<groups.Count;i++)
        {
            var group=groups[i];var signal=cortex.Signals.GetValueOrDefault(group.Name);
            float row=y+28+i*rowHeight;
            Label(group.Name[..Math.Min(14,group.Name.Length)],.69f*w,row,1);
            Label(FormattableString.Invariant($"+{signal.Reward:F3}"),.79f*w,row,1);
            Label(FormattableString.Invariant($"-{signal.Punishment:F3}"),.855f*w,row,1);
            Label(FormattableString.Invariant($"{signal.Net:+0.000;-0.000;0.000}"),.92f*w,row,1);
        }
        Label(session.Adaptive is {} adaptive?FormattableString.Invariant($"ONLINE R {session.LearningReward:F3} / TD {adaptive.LastTdError:F3}"):FormattableString.Invariant($"CENTRAL PAIN -{session.PrimitivePain:F3} / THINK BIAS +{session.Config.ThinkingBias:F3}"),.69f*w,.778f*h,1);
    }
    private void CortexRewardReadout(MazeSession session,float w,float h)
    {
        if(session.BakedRuntime is not {} runtime||session.CortexRewards is not {} cortex)return;
        float x=.70f*w,y=.857f*h;
        Label($"LOCAL FACTOR = dR + BIAS {session.Config.ThinkingBias:F3}",x,y,1);
        int row=0;
        foreach(var group in session.Baked!.Brain.Manifest.RewardGroups)
        {
            string name=group.Name[..Math.Min(10,group.Name.Length)];double derivative=runtime.RewardDerivatives.GetValueOrDefault(group.Name),factor=runtime.GroupFactors.GetValueOrDefault(group.Name)+session.Config.ThinkingBias;
            string detail=cortex.RewardFactorOverrides.ContainsKey(group.Name)
                ?FormattableString.Invariant($"{name} REWARD OFF / BIAS {factor:+0.00}")
                :FormattableString.Invariant($"{name} dR {derivative:+0.00;-0.00;0.00} g {factor:+0.00;-0.00;0.00} T {cortex.Targets[group.Name]:F2}");
            Label(detail,x,y+14+row++*12,1);
        }
        float cx=.96f*w,cy=.93f*h,radius=.026f*h;
        for(int i=0;i<MazeGame.TactileCount;i++)
        {
            float angle=2*MathF.PI*i/MazeGame.TactileCount-MathF.PI/2,value=session.Game.Tactile[i];
            Rect(cx+MathF.Cos(angle)*radius-3,cy+MathF.Sin(angle)*radius-3,6,6,.15f+.85f*value,.2f*(1-value),.3f*(1-value));
        }
        Label("TOUCH 16",cx-24,cy+radius+6,1);
    }
    private void InitializeMazeGraphInput()
    {
        if(mazeGraphScrollRegistered)return;
        ScrollInput+=amount=>{if(MazeGraphVisible)mazeGraphWheel+=amount;};mazeGraphScrollRegistered=true;
    }
}
