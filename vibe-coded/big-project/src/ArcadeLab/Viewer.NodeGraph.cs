using NeuralGraph;
using System.Diagnostics;
using System.Numerics;
using MYT.Kit.Gui;

namespace ArcadeLab;

public sealed unsafe partial class Viewer
{
    private GraphTopology? graphTopology;
    private readonly List<(int Id,float X,float Y,int Layer)> graphNodes=[];
    private readonly List<(int From,int To,int Edge)> graphEdges=[];
    private readonly Dictionary<int,string> graphOutputLabels=[];
    private readonly Dictionary<int,string> graphInputLabels=[];
    private double graphParameterRefresh;
    private delegate void WeightReader(ReadOnlySpan<int> edges,Span<float> destination);
    private int[] graphWeightIndices=[];
    private float[] graphWeights=[];
    private void NodeGraph(GraphModel model,IGraphBackend backend,ReadOnlySpan<float> activity,int layers,int width,float w,float h)
        =>NodeGraphView(model.Topology,model.Parameters,model.Plasticity,backend.DownloadParameters,activity,default,false,layers,width,w,h);
    private void NodeGraph(PredictiveCodingModel model,IPredictiveCodingBackend backend,ReadOnlySpan<float> activity,ReadOnlySpan<float> errors,int layers,int width,float w,float h,int[]? motorOutputs=null)
        =>NodeGraphView(model.Topology,model.Weights,model.Plasticity,backend.DownloadParameters,activity,errors,true,layers,width,w,h,backend.ReadWeights,motorOutputs);
    private void NodeGraphView(GraphTopology topology,float[] parameters,float[] plasticity,Action download,ReadOnlySpan<float> activity,ReadOnlySpan<float> errors,bool predictive,int layers,int width,float w,float h,WeightReader? readWeights=null,int[]? motorOutputs=null)
    {
        int[] outputs=motorOutputs??topology.Outputs;
        if(!ReferenceEquals(graphTopology,topology))
        {
            graphTopology=topology;graphNodes.Clear();graphEdges.Clear();graphOutputLabels.Clear();graphInputLabels.Clear();graphParameterRefresh=0;
            var sampled=new Dictionary<int,(int Index,int Row,int Count)>();
            bool maze=outputs.Length==MazeSession.OutputCount;
            int[] outputOrder=Enumerable.Range(0,outputs.Length).ToArray();
            string[] buttonNames=["FWD","BACK","LEFT","RIGHT","STR L","STR R","FIRE","LOAD"];
            for(int layer=0;layer<layers+2;layer++)
            {
                int size=layer==0?topology.InputCount:layer==layers+1?outputs.Length:width;
                int rows=layer==0?96:128,columns=(size+rows-1)/rows;
                int sampleCount=Math.Min(layer==layers+1?24:layer==0?8:6,size);
                var samples=Enumerable.Range(0,sampleCount).Select(i=>sampleCount==1?0:i*(size-1)/(sampleCount-1)).ToArray();
                if(predictive&&maze&&layer==0&&size==MazeSession.InputCount)
                {
                    samples=[0,2*(MazeGame.VisionRayCount/2*3),2*((MazeGame.VisionRayCount-1)*3),2*MazeVision.FrameSize,2*(MazeVision.FrameSize+96),2*(MazeVision.FrameSize+192+8),2*MazeSession.PainFeatureOffset,2*(MazeSession.PainFeatureOffset+1)];
                    string[] names=["RGB L","RGB C","RGB R","PCM L","PCM R","HEALTH","DRIVE0","DRIVE1"];
                    for(int i=0;i<samples.Length;i++)graphInputLabels[samples[i]]=names[i];
                }
                for(int row=0;row<size;row++)
                {
                    int sampleRow=Array.IndexOf(samples,row);
                    if(layer!=layers+1 && sampleRow<0)continue;
                    int slot=layer==layers+1?outputOrder[row]:row;
                    int id=layer==0?row:layer==layers+1?outputs[slot]:topology.InputCount+(layer-1)*width+row;
                    float x,y;
                    if(layer==layers+1)
                    {
                        x=.935f;
                        y=maze?(row<8?.145f+.035f*row:slot==0?.39f:slot==10?.41f:slot==8?.46f:.435f+.005f*((slot-11)%6)):.14f+.30f*(row+.5f)/size;
                        if(maze&&slot>=11)x+=.008f*((slot-11)/6);
                        graphOutputLabels[id]=maze?(row<8?buttonNames[row]:slot==0?"VALUE":slot==8?"UNUSED":slot==10?"REWARD":slot==11?"VIEW12":""):$"OUT{slot}";
                    }
                    else
                    {
                        x=layer==0?.695f:.755f+.16f*(layer-1)/Math.Max(1,layers-1);
                        y=.145f+.28f*(sampleRow+.5f)/sampleCount;
                    }
                    int index=graphNodes.Count;graphNodes.Add((id,x,y,layer));
                    if(sampleRow>=0)sampled[id]=(index,sampleRow,sampleCount);
                }
            }
            // Explicitly sampled overview: every shown dot and line maps to actual topology.
            for(int edgeIndex=0;edgeIndex<topology.Edges.Length;edgeIndex++)
            {
                var edge=topology.Edges[edgeIndex];
                if(sampled.TryGetValue(edge.Source,out var source)&&sampled.TryGetValue(edge.Destination,out var target)&&
                   (source.Row==target.Row%source.Count||source.Row==(target.Row+source.Count/2)%source.Count))
                    graphEdges.Add((source.Index,target.Index,edgeIndex));
            }
            graphWeightIndices=graphEdges.Select(edge=>edge.Edge).ToArray();
            graphWeights=new float[graphWeightIndices.Length];
        }
        double now=(double)Stopwatch.GetTimestamp()/Stopwatch.Frequency;
        if(now-graphParameterRefresh>.5)
        {
            if(readWeights is not null)readWeights(graphWeightIndices,graphWeights);
            else download();
            graphParameterRefresh=now;
        }
        double cursorX=-100,cursorY=-100;int windowWidth=(int)w,windowHeight=(int)h;
        if(window!=0){Glfw.GetCursorPos(window,out cursorX,out cursorY);Glfw.GetWindowSize(window,out windowWidth,out windowHeight);}
        float mouseX=(float)(cursorX*w/Math.Max(1,windowWidth)),mouseY=(float)(cursorY*h/Math.Max(1,windowHeight));
        int hoveredNode=-1,hoveredEdge=-1;float nearestEdge=25,nearestNode=16;
        Rect(.66f*w,.025f*h,.335f*w,.505f*h,.025f,.04f,.065f);
        Label(predictive?"PREDICTIVE CODING":"NETWORK OVERVIEW",.675f*w,.04f*h,2);
        Label($"ACTUAL: {topology.InputCount} INPUTS   {layers} X {width} HIDDEN   {outputs.Length} OUT",.675f*w,.07f*h,1);
        Label("SHOWN: 8 INPUTS, 6 PER HIDDEN LAYER; EDGES SAMPLED",.675f*w,.085f*h,1);
        Label("INPUT",.68f*w,.11f*h,1);Label($"8/{topology.InputCount}",.68f*w,.125f*h,1);
        for(int layer=1;layer<=layers;layer++)
        {
            float x=(.755f+.16f*(layer-1)/Math.Max(1,layers-1))*w-8;
            Label($"H{layer}",x,.11f*h,1);
        }
        Label("8 KEYS",.935f*w,.11f*h,1);

        var batch=Graph;batch.Clip=new GuiRect(.66f*w,.14f*h,.335f*w,.32f*h);
        void Segment(float ax,float ay,float bx,float by,GuiColor color)
            =>batch.Line(new Vector3(ax,ay,.5f),new Vector3(bx,by,.5f),color);
        int displayedEdge=0;
        foreach(var (from,to,edgeIndex) in graphEdges)
        {
            var source=graphNodes[from];var target=graphNodes[to];
            bool skip=target.Layer-source.Layer>1;float weight=readWeights is null?parameters[edgeIndex]:graphWeights[displayedEdge];displayedEdge++;
            float strength=MathF.Abs(weight)/(MathF.Abs(weight)+.1f);
            float red=weight<0?.10f+.30f*strength:.035f,green=weight>=0?.08f+.24f*strength:.04f,blue=weight>=0?.12f+.30f*strength:.055f;
            float x1=source.X*w+(predictive?8:0),y1=source.Y*h,x2=target.X*w,y2=target.Y*h;
            var tint=new GuiColor((byte)(red*255),(byte)(green*255),(byte)(blue*255));
            var reverse=GuiColor.FromRgb(weight>=0?0x590A0Fu:0x0A4059u);
            int segments=skip?16:1;
            for(int part=0;part<segments;part++)
            {
                float t0=(float)part/segments,t1=(float)(part+1)/segments;
                float ax=x1+(x2-x1)*t0,ay=y1+(y2-y1)*t0-(skip?MathF.Sin(t0*MathF.PI)*.025f*h:0);
                float bx=x1+(x2-x1)*t1,by=y1+(y2-y1)*t1-(skip?MathF.Sin(t1*MathF.PI)*.025f*h:0);
                float dx=bx-ax,dy=by-ay,den=dx*dx+dy*dy;
                float t=den==0?0:Math.Clamp(((mouseX-ax)*dx+(mouseY-ay)*dy)/den,0,1);
                float distance=(mouseX-ax-dx*t)*(mouseX-ax-dx*t)+(mouseY-ay-dy*t)*(mouseY-ay-dy*t);
                if(distance<nearestEdge){nearestEdge=distance;hoveredEdge=edgeIndex;}
                if(skip&&part%3==2)continue;
                Segment(ax,ay,bx,by,tint);
                if(predictive)Segment(ax,ay+2,bx,by+2,reverse);
            }
            float tipX=x1+(x2-x1)*.82f,tipY=y1+(y2-y1)*.82f-(skip?MathF.Sin(.82f*MathF.PI)*.025f*h:0);
            Segment(tipX-3,tipY-3,tipX,tipY,tint);Segment(tipX-3,tipY+3,tipX,tipY,tint);
            if(predictive)
            {
                tipX=x2+(x1-x2)*.82f;tipY=y2+(y1-y2)*.82f-(skip?MathF.Sin(.82f*MathF.PI)*.025f*h:0)+2;
                Segment(tipX+3,tipY-3,tipX,tipY,reverse);Segment(tipX+3,tipY+3,tipX,tipY,reverse);
            }
        }

        foreach(var node in graphNodes)
        {
            int id=node.Id;float positive=Math.Clamp(activity[2*id],0,1),negative=Math.Clamp(activity[2*id+1],0,1);
            float x=node.X*w,y=node.Y*h;
            float distance=(mouseX-x)*(mouseX-x)+(mouseY-y)*(mouseY-y);
            if(distance<nearestNode){nearestNode=distance;hoveredNode=id;}
            float radius=node.Layer==layers+1 && graphOutputLabels.GetValueOrDefault(id,"") is not ("FWD" or "BACK" or "LEFT" or "RIGHT" or "STR L" or "STR R" or "FIRE" or "LOAD")?2:Math.Clamp(h*.0035f,2.5f,4.5f);
            batch.Neuron(new(x,y,.5f),radius+1,GuiColor.FromRgb(0x304552));
            batch.Neuron(new(x,y,.5f),radius,new GuiColor(5,(byte)(30+224*positive),15));
            if(!predictive)batch.Neuron(new(x+radius*.4f,y,.5f),radius*.6f,new GuiColor(5,12,(byte)(35+219*negative)));
            if(predictive)
            {
                float ep=errors[2*id],en=errors[2*id+1];
                batch.Neuron(new(x+7.5f,y-.5f,.5f),1.5f,new GuiColor((byte)(38+217*ep/(1+ep)),8,10),false);
                batch.Neuron(new(x+11.5f,y+2.5f,.5f),1.5f,new GuiColor(8,15,(byte)(38+217*en/(1+en))),false);
            }
            if(graphInputLabels.TryGetValue(id,out string? inputLabel))Label(inputLabel,.663f*w,y-3,1);
            if(graphOutputLabels.TryGetValue(id,out string? label)&&label.Length>0)
                Label(label,x+(predictive?16:7),y-3,1);
        }
        if(hoveredNode>=0)
        {
            int id=hoveredNode;
            Label(predictive?FormattableString.Invariant($"PAIR {id}  X {activity[2*id]:F3}  E+ {errors[2*id]:F3} E- {errors[2*id+1]:F3}"):FormattableString.Invariant($"NODE {id}  A+ {activity[2*id]:F3}  A- {activity[2*id+1]:F3}"),.675f*w,.465f*h,1);
            string bias=predictive?"LOCAL X > E +1   E > X -1":id<topology.InputCount?"CLAMPED INPUT":FormattableString.Invariant($"BIAS {parameters[topology.Edges.Length+id-topology.InputCount]:F3}");
            Label(bias+(predictive?"   NO BIAS":"   NO LOCAL ERROR SIGNAL"),.675f*w,.48f*h,1);
        }
        else if(hoveredEdge>=0)
        {
            var edge=topology.Edges[hoveredEdge];
            float weight=readWeights is null?parameters[hoveredEdge]:graphWeights[Array.IndexOf(graphWeightIndices,hoveredEdge)];
            Label(FormattableString.Invariant($"EDGE {edge.Source} > {edge.Destination}  W {weight:F4}"),.675f*w,.465f*h,1);
            Label(FormattableString.Invariant($"PLASTICITY {plasticity[hoveredEdge]:F3}  WEIGHTS REFRESH 2 HZ"),.675f*w,.48f*h,1);
        }
        else
        {
            Label(predictive?"SHARED E > X W / X > E -W":"FLOW LEFT > RIGHT CYAN +W RED -W",.675f*w,.465f*h,1);
            Label(predictive?"GREEN NORMAL  RED E+ BLUE E-  DASHED SKIPS":"HOVER FOR VALUES DASHED SKIPS GREEN/BLUE RATES",.675f*w,.48f*h,1);
        }
    }

    private void NetworkErrorBar(double loss,float w,float h)
    {
        // Fixed logarithmic display: large changing losses remain distinguishable.
        // Every paired error contributes its square. Statistics never drive the update.
        float fill=(float)Math.Clamp(Math.Log10(1+Math.Max(0,loss))/12,0,1);
        float x=.675f*w,y=.512f*h,width=.31f*w,height=.012f*h;
        Label(FormattableString.Invariant($"TOTAL ERROR SUM E2 {loss:F3}   LOG 0 TO 1E12"),x,.497f*h,1);
        Rect(x-1,y-1,width+2,height+2,.21f,.25f,.33f);
        Rect(x,y,width,height,.055f,.065f,.10f);
        Rect(x,y,width*fill,height,.76f,.35f,.93f);
        foreach(float tick in (ReadOnlySpan<float>)[1f,1000f,1000000f,1000000000f,1000000000000f])
        {
            float at=MathF.Log10(1+tick)/12;
            Rect(x+width*Math.Clamp(at,0,1)-1,y,1,height,.42f,.45f,.54f);
        }
    }

    private long lastCombatFrame=-1;
    private double combatShownUntil;
    private int recentHits,recentKills;
    private float recentIdealCombat,recentPredictedCombat;
    private void CombatRewardReadout(MazeSession session,float w,float h)
    {
        if(session.IsBakedBrain)
        {
            Label("CORTEX REWARDS: MOTOR / CENTRAL / SPATIAL",.035f*w,.85f*h,1);
            Label("VISIBLE HIT / ALL MISSES; KILL / EXIT / DEATH",.035f*w,.862f*h,1);
            Label($"HITS {session.TotalHits} KILLS {session.TotalKills} MISSES {session.TotalMisses}",.035f*w,.875f*h,1);
            Label("ALL CORTICES: IMPLICIT THINKING BIAS",.035f*w,.89f*h,1);
            Label("TOUCH RING: FRONT 0 / RIGHT 4 / BACK 8 / LEFT 12",.035f*w,.905f*h,1);
            Label(session.Training?"TRAIN: CLAMP REWARD TARGETS, RESTORE FREE STATE":"PLAY: dR/dt + IMPLICIT BIAS, TEACHING FROZEN",.035f*w,.92f*h,1);
            return;
        }

        double now=(double)Stopwatch.GetTimestamp()/Stopwatch.Frequency;
        if(session.Frames!=lastCombatFrame)
        {
            lastCombatFrame=session.Frames;
            if(session.Game.Events.Hits>0||session.Game.Events.Kills>0)
            {
                recentHits=session.Game.Events.Hits;recentKills=session.Game.Events.Kills;
                recentIdealCombat=recentHits*MazeGame.HitReward+recentKills*MazeGame.KillReward;
                recentPredictedCombat=session.Pleasure;combatShownUntil=now+2;
            }
        }
        Label("EXPLORATION REWARD OFF",.035f*w,.85f*h,1);
        Label(session.Training?FormattableString.Invariant($"INACTIVITY PAIN {session.Game.StationaryTeacherPain:F3}  MAX 1.0"):"PLAY USES LEARNED REWARD",.035f*w,.862f*h,1);
        Label(session.Training?FormattableString.Invariant($"DARKNESS {session.Game.VisualDarknessScore:F3}  HEAD P {session.Pleasure:F4}  IDEAL VIS {session.Game.VisualDarknessTeacherPleasure:F4}"):FormattableString.Invariant($"DARKNESS {session.Game.VisualDarknessScore:F3}  FROZEN REWARD HEAD P {session.Pleasure:F4}"),.035f*w,.92f*h,1);
        Label(FormattableString.Invariant($"HITS {session.TotalHits}  KILLS {session.TotalKills}  APPLIED R {session.LearningReward:F3}"),.035f*w,.875f*h,1);
        if(now<combatShownUntil)
        {
            Label(FormattableString.Invariant($"HIT {recentHits} KILL {recentKills}  PREDICTED P {recentPredictedCombat:F3}"),.035f*w,.89f*h,1);
            Label(session.Training?FormattableString.Invariant($"IDEAL COMBAT +{recentIdealCombat:F3}  USED IN TRAINING"):"PLAY USES FROZEN HEAD   NO IDEAL COMBAT REWARD",.035f*w,.905f*h,1);
        }
    }
}
