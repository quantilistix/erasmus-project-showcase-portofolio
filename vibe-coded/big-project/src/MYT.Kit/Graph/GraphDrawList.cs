using System.Numerics;
using System.Runtime.InteropServices;
using MYT.Kit.Gui;

namespace MYT.Kit.Graph;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct GraphVertex(Vector3 Position, GuiColor Color, Vector2 Uv);

/// <summary>Two vertices per weight, followed by depth-tested neurons and a separate text overlay. Coordinates are pixels;
/// depth is in [0,1], with smaller values closer to the camera.</summary>
public sealed class GraphDrawList
{
    private readonly List<GraphVertex> guides=[];
    private readonly List<GraphVertex> lines=[];
    private readonly List<GraphVertex> highlights=[];
    private readonly List<GraphVertex> neurons=[];
    private readonly List<GraphVertex> overlays=[];
    public ReadOnlySpan<GraphVertex> Guides=>CollectionsMarshal.AsSpan(guides);
    public ReadOnlySpan<GraphVertex> Lines=>CollectionsMarshal.AsSpan(lines);
    public ReadOnlySpan<GraphVertex> Highlights=>CollectionsMarshal.AsSpan(highlights);
    public ReadOnlySpan<GraphVertex> Neurons=>CollectionsMarshal.AsSpan(neurons);
    public ReadOnlySpan<GraphVertex> Overlays=>CollectionsMarshal.AsSpan(overlays);
    public GuiRect Clip {get;set;}
    public void Clear(){guides.Clear();lines.Clear();highlights.Clear();neurons.Clear();overlays.Clear();}
    /// <summary>Translucent orientation lines, drawn before weights without writing depth.</summary>
    public void Guide(Vector3 a,Vector3 b,GuiColor color)
    {
        guides.Add(new(a,color,new(2,2)));guides.Add(new(b,color,new(2,2)));
    }
    public void Line(Vector3 a,Vector3 b,GuiColor color)
    {
        lines.Add(new(a,color,new(2,2)));lines.Add(new(b,color,new(2,2)));
    }
    public void Highlight(Vector3 a,Vector3 b,GuiColor color)
    {
        highlights.Add(new(a,color,new(2,2)));highlights.Add(new(b,color,new(2,2)));
    }
    public void OverlayRect(GuiRect rect,GuiColor color)
    {
        GraphVertex a=new(new(rect.Left,rect.Top,.5f),color,new(2,2));
        GraphVertex b=new(new(rect.Right,rect.Top,.5f),color,new(2,2));
        GraphVertex c=new(new(rect.Right,rect.Bottom,.5f),color,new(2,2));
        GraphVertex d=new(new(rect.Left,rect.Bottom,.5f),color,new(2,2));
        overlays.Add(a);overlays.Add(b);overlays.Add(c);overlays.Add(a);overlays.Add(c);overlays.Add(d);
    }
    public void Neuron(Vector3 p,float radius,GuiColor color,bool round=true)
    {
        Vector2 uv=round?new(1,1):new(2,2);
        GraphVertex a=new(p+new Vector3(-radius,-radius,0),color,round?new(-1,-1):uv);
        GraphVertex b=new(p+new Vector3(radius,-radius,0),color,round?new(1,-1):uv);
        GraphVertex c=new(p+new Vector3(radius,radius,0),color,uv);
        GraphVertex d=new(p+new Vector3(-radius,radius,0),color,round?new(-1,1):uv);
        neurons.Add(a);neurons.Add(b);neurons.Add(c);neurons.Add(a);neurons.Add(c);neurons.Add(d);
    }
}
