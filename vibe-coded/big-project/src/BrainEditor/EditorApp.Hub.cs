using System.Globalization;
using System.Text.Json;
using GlmSharp;
using MYT.Kit.Gui;

namespace ArcadeLab.Editor;

public sealed partial class EditorApp
{
    private ModuleGraph? hubHistogramGraph;
    private int[] hubHistogram=[];
    private static int HubCount(BrainModule module)=>module.NeuronCount>0?module.NeuronCount:checked(module.Inputs+module.Hidden+module.Outputs);
    private static bool HubExponentEnabled(BrainModule module)
    {
        int available=HubCount(module)-1;
        return available>1&&Math.Min(module.PowerLaw.MinDegree,available)<Math.Min(module.PowerLaw.MaxDegree,available);
    }
    private static void PrepareHubEdit(BrainModule module,bool countChanged,bool distributionChanged)
    {
        if(countChanged)
        {
            Generation.Require(module.NeuronCount is >0 and <=Generation.MaxNeurons,"Hub neuron count must be between 1 and 20,000.");
            int limit=Math.Max(1,module.NeuronCount-1);
            module.PowerLaw.MaxDegree=Math.Min(module.PowerLaw.MaxDegree,limit);
            module.PowerLaw.MinDegree=Math.Min(module.PowerLaw.MinDegree,module.PowerLaw.MaxDegree);
        }
        if(distributionChanged)
        {
            int limit=Math.Max(1,HubCount(module)-1);
            Generation.Require(module.PowerLaw.MaxDegree<=limit,$"Maximum degree cannot exceed {limit}: each neuron can connect to each other neuron once.");
        }
    }
    private void DrawHubDistribution(BrainModule module,float x,float y)
    {
        void Set(Action<BrainModule> change,bool count=false)
        {
            var copy=JsonSerializer.Deserialize<BrainModule>(JsonSerializer.Serialize(module,BrainDocument.Json),BrainDocument.Json)!;
            change(copy);PrepareHubEdit(copy,count,!count);ReplaceModule(copy);status="UPDATED HUB DISTRIBUTION";
        }
        void Field(string id,string label,float top,string value,Action<string> commit,bool enabled=true)
        {
            Label(label,x,top,1);var rect=new GuiRect(x,top+15,402,29);
            if(enabled)Inline("hub-"+id,rect,value,commit);
            else {gui.Rect(rect,gui.Style.FieldBackground);gui.Label(rect,value+" (fixed degree)",gui.Style.TextDisabled);}
        }
        int count=HubCount(module);
        Field("count","NEURONS",y,count.ToString(CultureInfo.InvariantCulture),text=>Set(m=>m.NeuronCount=int.Parse(text,CultureInfo.InvariantCulture),true));
        Field("minimum","MINIMUM OUTGOING DEGREE",y+49,module.PowerLaw.MinDegree.ToString(CultureInfo.InvariantCulture),text=>Set(m=>m.PowerLaw.MinDegree=int.Parse(text,CultureInfo.InvariantCulture)));
        Field("maximum",$"MAXIMUM OUTGOING DEGREE (UP TO {Math.Max(0,count-1)})",y+98,module.PowerLaw.MaxDegree.ToString(CultureInfo.InvariantCulture),text=>Set(m=>m.PowerLaw.MaxDegree=int.Parse(text,CultureInfo.InvariantCulture)));
        Field("exponent","EXPONENT: HIGHER CONCENTRATES AT MINIMUM",y+147,module.PowerLaw.Exponent.ToString(CultureInfo.InvariantCulture),text=>Set(m=>m.PowerLaw.Exponent=double.Parse(text,CultureInfo.InvariantCulture)),HubExponentEnabled(module));
        if(preview is null)return;
        if(!ReferenceEquals(hubHistogramGraph,preview))
        {
            hubHistogramGraph=preview;
            var degrees=new int[preview.Neurons.Count];foreach(var edge in preview.Edges)degrees[edge.Source]++;
            hubHistogram=new int[degrees.Max()+1];foreach(int degree in degrees)hubHistogram[degree]++;
        }
        int minimum=Array.FindIndex(hubHistogram,n=>n>0),maximum=hubHistogram.Length-1;
        int total=preview.Neurons.Count,atMinimum=hubHistogram[minimum];
        float graphTop=y+211,graphHeight=Math.Max(45,Math.Min(100,height-graphTop-178));
        Label("GENERATED OUTGOING DEGREES (NOT INCOMING)",x,graphTop-14,1);
        var plot=new GuiRect(x,graphTop,402,graphHeight);
        gui.Rect(plot,GuiColor.FromRgb(0x101D2A));gui.Outline(plot,GuiColor.FromRgb(0x39556F));
        int bins=Math.Min(32,maximum-minimum+1),largest=0;
        var counts=new int[bins];
        for(int k=minimum;k<=maximum;k++)counts[Math.Min(bins-1,(int)((long)(k-minimum)*bins/(maximum-minimum+1)))]+=hubHistogram[k];
        foreach(int n in counts)largest=Math.Max(largest,n);
        string? hovered=null;
        for(int i=0;i<bins;i++)
        {
            float barWidth=(plot.Width-8)/bins;
            float barHeight=counts[i]==0?0:Math.Max(1,(plot.Height-8)*counts[i]/largest);
            var bar=new GuiRect(plot.X+4+i*barWidth,plot.Bottom-4-barHeight,Math.Max(1,barWidth-2),barHeight);
            gui.Rect(bar,GuiColor.FromRgb(0x65DACC));
            if(new GuiRect(bar.X,plot.Y,barWidth,plot.Height).Contains(new vec2(mouseX,mouseY)))
            {
                int start=minimum+(int)Math.Ceiling((double)i*(maximum-minimum+1)/bins);
                int end=minimum+(int)Math.Ceiling((double)(i+1)*(maximum-minimum+1)/bins)-1;
                hovered=$"DEGREE {start}"+(end>start?$"..{end}":"")+$": {counts[i]} NEURONS";
            }
        }
        Label(hovered??$"RANGE {minimum}..{maximum} | MEAN {(double)preview.Edges.Count/total:F2} | {preview.Edges.Count:N0} EDGES",x,plot.Bottom+9,1);
        Label($"{atMinimum:N0}/{total:N0} NEURONS AT MINIMUM ({(double)atMinimum/total:P1})",x,plot.Bottom+27,1);
        string hint=count<2?"ONE NEURON: NO OTHER NEURONS TO CONNECT TO"
            :minimum==maximum?"FIXED RANGE: EXPONENT CANNOT CHANGE DEGREES"
            :count==2?"TWO NEURONS: NO INTERIOR PERCENTILES"
            :atMinimum>=total*.95?"TAIL CONCENTRATED: LOWER EXPONENT FOR VARIETY"
            :"MIN AND MAX INCLUDED; INTERIOR USES POWER LAW";
        Label(hint,x,plot.Bottom+48,1);
        Label("INTEGER DEGREES MAY STAY SAME FOR SMALL EDITS",x,plot.Bottom+66,1);
    }
}
