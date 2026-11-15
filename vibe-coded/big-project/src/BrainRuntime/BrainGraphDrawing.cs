using System.Numerics;
using MYT.Kit.Graph;
using MYT.Kit.Gui;

namespace ArcadeLab.Editor;

/// <summary>The editor's perspective graph primitives, also used for live maze inspection.</summary>
public static class BrainGraphDrawing
{
    public static GuiColor NeuronColor(GeneratedNeuron neuron,ModuleKind kind)=>GuiColor.FromRgb(
        kind==ModuleKind.Clock?0x82A5EFu:neuron.ActivationCap==8?0xF4BE69u:
        neuron.Role==NeuronRole.Input?0x64CDEBu:neuron.Role==NeuronRole.Hidden?0x6CDBB2u:0xB499EFu);

    public static int Edges(GraphDrawList batch,PerspectiveCamera camera,GuiRect clip,Vector3[] viewPoints,
        IReadOnlyList<(int Source,int Destination,float Weight)> edges,float scale=1)
    {
        int shown=0;
        foreach(var edge in edges)
        {
            if(!camera.ProjectViewSegment(viewPoints[edge.Source],viewPoints[edge.Destination],out var from,out var to))continue;
            if(Math.Max(from.X,to.X)<clip.Left||Math.Min(from.X,to.X)>clip.Right||Math.Max(from.Y,to.Y)<clip.Top||Math.Min(from.Y,to.Y)>clip.Bottom)continue;
            batch.Line(Pixel(from,scale),Pixel(to,scale),GuiColor.FromRgb(edge.Weight<0?0x612652u:0x144252u));shown++;
        }
        return shown;
    }
    public static void Neuron(GraphDrawList batch,Vector3 screen,float radius,GuiColor color,float scale=1)
        =>batch.Neuron(Pixel(screen,scale),radius*scale,color);
    private static Vector3 Pixel(Vector3 p,float scale)=>new(p.X*scale,p.Y*scale,p.Z);
}
