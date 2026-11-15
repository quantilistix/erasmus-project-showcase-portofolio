using System.Numerics;
using GlmSharp;
using MYT.Kit.Gui;

namespace ArcadeLab.Editor;

public sealed partial class EditorApp
{
    private void DrawLinkScene()
    {
        if(document.Modules.Count==0){Label("ADD A MODULE TO START",CanvasContent.X+12,CanvasContent.Y+12,1);return;}
        try
        {
            if(linkScene is null||linkSceneRevision!=topologyRevision)
            {
                linkScene=new(document);linkSceneRevision=topologyRevision;
            }
            linkScene.SynchronizePositions();
            RenderLinkScene(linkScene);
        }
        catch(InvalidDataException e){status=e.Message;Label("PREVIEW: "+e.Message,CanvasContent.X+12,CanvasContent.Y+12,1);}
    }
    private void RenderLinkScene(LinkScene scene)
    {
        var ns=scene.Graph.Neurons;
        var editingLink=view==1?document.Links.FirstOrDefault(l=>l.Id==selectedLink):null;
        bool Visible(string id)=>editingLink is null||id==editingLink.Source.ModuleId||id==editingLink.Destination.ModuleId;
        FrameGraph(editingLink is null?ns:ns.Where(n=>Visible(n.ModuleId)).ToArray());
        var sourceMembers=editingLink is null?null:scene.PortIndices(editingLink.Source).ToHashSet();
        var destinationMembers=editingLink is null?null:scene.PortIndices(editingLink.Destination).ToHashSet();
        var camera=Camera(graphCenter,graphFit,graphSpan);
        if(dragging&&mouseDown&&!click&&selectedLink==""&&scene.ById.TryGetValue(selectedModule,out var moving))
        {
            var delta=new Vector2(mouseX-oldMouseX/gui.Scale,mouseY-oldMouseY/gui.Scale);
            if(delta.LengthSquared()>0)
            {
                if(!dragCheckpoint){Checkpoint(positionsOnly:true);dragCheckpoint=true;}
                var world=camera.Drag(moving.Center,delta,shift,dragAxis);
                moving.Module.Position+=new Position(world.X,world.Y,world.Z);Invalidate(positionsOnly:true);
                scene.SynchronizePositions();
            }
        }
        var batch=viewer.Graph;var clip=CanvasContent;batch.Clip=clip.Scaled(gui.Scale);
        gui.PushClip(clip);
        DrawOrientation(clip,graphCenter,graphFit,true);
        var cursor=new Vector2(mouseX,mouseY);bool interactive=view==0&&textTitle is null&&!uiCaptured&&choiceMenu is null&&inlineId is null&&clip.Contains(new vec2(mouseX,mouseY));
        Vector3 Pixel(Vector3 p)=>new(p.X*gui.Scale,p.Y*gui.Scale,p.Z);
        var labels=new List<(SceneModule Module,GuiRect Rect,string Text)>();
        SceneModule? boxHover=null,nodeHover=null;float boxDistance=float.PositiveInfinity,nodeDistance=100,nodeDepth=float.PositiveInfinity;
        foreach(var module in scene.Modules.Where(m=>Visible(m.Module.Id)).OrderByDescending(m=>camera.ToView(m.Center).Z))
        {
            bool active=module.Module.Id==selectedModule&&selectedLink=="";
            var color=active?GuiColor.White:ModuleColor(module.Module.Kind);
            float left=float.PositiveInfinity,top=float.PositiveInfinity,right=float.NegativeInfinity,bottom=float.NegativeInfinity;
            for(int corner=0;corner<8;corner++)for(int axis=0;axis<3;axis++)
            {
                int other=corner^(1<<axis);if(other<corner)continue;
                if(!camera.ProjectSegment(module.Corner(corner),module.Corner(other),out var a,out var b))continue;
                batch.Guide(Pixel(a),Pixel(b),color.WithAlpha(active?.85f:.4f));
                left=Math.Min(left,Math.Min(a.X,b.X));top=Math.Min(top,Math.Min(a.Y,b.Y));
                right=Math.Max(right,Math.Max(a.X,b.X));bottom=Math.Max(bottom,Math.Max(a.Y,b.Y));
            }
            if(float.IsFinite(left)&&ViewportGeometry.Overlaps(clip,GuiRect.FromEdges(left,top,right,bottom)))
            {
                string text=module.Module.Name[..Math.Min(22,module.Module.Name.Length)];
                int maxChars=Math.Max(8,(int)((clip.Width-16)/6.75f));if(text.Length>maxChars)text=text[..(maxChars-3)]+"...";
                float w=gui.Style.Font.Measure(text)*9/gui.Style.Font.Height+8;
                var rect=new GuiRect(Math.Clamp(left,clip.Left+4,clip.Right-w-4),Math.Clamp(top-21,clip.Top+4,clip.Bottom-43),w,18);
                labels.Add((module,rect,text));
            }
            if(interactive&&module.IntersectRay(camera,cursor,out float distance)&&distance<boxDistance){boxDistance=distance;boxHover=module;}
        }
        string? linkHover=null;float linkDistance=36;var arrowed=new HashSet<string>();int shownEdges=0;
        void Consider(Vector3 a,Vector3 b,string id)
        {
            if(!interactive)return;
            var delta=new Vector2(b.X-a.X,b.Y-a.Y);float length=delta.LengthSquared();
            float t=length==0?0:Math.Clamp(Vector2.Dot(cursor-new Vector2(a.X,a.Y),delta)/length,0,1);
            float distance=Vector2.DistanceSquared(cursor,new Vector2(a.X,a.Y)+delta*t);
            if(distance<linkDistance){linkDistance=distance;linkHover=id;}
        }
        void Weight(Vector3 a,Vector3 b,GuiColor color,bool highlight=false)
        {
            if(highlight)batch.Highlight(Pixel(a),Pixel(b),color);else batch.Line(Pixel(a),Pixel(b),color);
        }
        void Arrowhead(Vector3 a,Vector3 b,GuiColor color,bool highlight)
        {
            var delta=new Vector2(b.X-a.X,b.Y-a.Y);if(delta.LengthSquared()<16)return;
            var direction=Vector2.Normalize(delta);var normal=new Vector2(-direction.Y,direction.X);
            var tip=Vector3.Lerp(a,b,.65f);
            var back=new Vector2(tip.X,tip.Y)-direction*10;
            Weight(new(back+normal*4,tip.Z),tip,color,highlight);Weight(new(back-normal*4,tip.Z),tip,color,highlight);
        }
        for(int index=0;index<scene.Graph.Edges.Count;index++)
        {
            var edge=scene.Graph.Edges[index];var source=ns[edge.Source];var destination=ns[edge.Destination];
            if(!Visible(source.ModuleId)||!Visible(destination.ModuleId)||(editingLink is not null&&scene.Graph.EdgeLinkIds[index] is {} linkId&&linkId!=selectedLink))continue;
            var p=source.Position;var q=destination.Position;
            if(!camera.ProjectSegment(new(p.X,p.Y,p.Z),new(q.X,q.Y,q.Z),out var a,out var b))continue;
            if(Math.Max(a.X,b.X)<clip.Left||Math.Min(a.X,b.X)>clip.Right||Math.Max(a.Y,b.Y)<clip.Top||Math.Min(a.Y,b.Y)>clip.Bottom)continue;
            string? link=scene.Graph.EdgeLinkIds[index];bool active=link is not null&&selectedLink==link;
            bool incident=link is not null&&selectedLink==""&&(source.ModuleId==selectedModule||destination.ModuleId==selectedModule);
            var color=GuiColor.FromRgb(active?0xC6A66Fu:incident?0x376C70u:link is not null?0x244654u:edge.Weight<0?0x612652u:0x144252u);
            Weight(a,b,color);shownEdges++;
            if(link is not null)
            {
                Consider(a,b,link);
                if(active)Weight(a,b,color,true);
                if(arrowed.Add(link))Arrowhead(a,b,color,active);
            }
        }
        // Empty/invalid conceptual links remain selectable, without inventing neural edges.
        foreach(var link in document.Links)
        {
            if(scene.NonemptyLinks.Contains(link.Id)||(editingLink is not null&&link.Id!=selectedLink))continue;
            if(!scene.ById.TryGetValue(link.Source.ModuleId,out var source)||!scene.ById.TryGetValue(link.Destination.ModuleId,out var destination))continue;
            if(!camera.ProjectSegment(source.Center,destination.Center,out var a,out var b))continue;
            var color=GuiColor.FromRgb(scene.Graph.LinkErrors.ContainsKey(link.Id)?0xEF8490u:0xB5A274u);
            for(int dash=0;dash<12;dash+=2)batch.Guide(Pixel(Vector3.Lerp(a,b,dash/12f)),Pixel(Vector3.Lerp(a,b,(dash+1)/12f)),color);
            Consider(a,b,link.Id);
        }
        var connectionLabels=DrawConnections(scene,camera,Consider,Weight,Arrowhead);
        int shownNodes=0;
        for(int i=0;i<ns.Count;i++)
        {
            var n=ns[i];if(!Visible(n.ModuleId))continue;var p=n.Position;var world=new Vector3(p.X,p.Y,p.Z);
            if(!camera.TryProject(world,out var screen))continue;
            var module=scene.ById[n.ModuleId].Module;
            float radius=(module.Kind==ModuleKind.Clock?4:3)*Math.Clamp(camera.Distance/camera.ToView(world).Z,.4f,2.5f);
            if(!ViewportGeometry.MarkerVisible(clip,screen,radius))continue;
            var color=GuiColor.FromRgb(module.Kind==ModuleKind.Clock?0x82A5EFu:n.ActivationCap==8?0xF4BE69u:n.Role==NeuronRole.Input?0x64CDEBu:n.Role==NeuronRole.Hidden?0x6CDBB2u:0xB499EFu);
            if(editingLink is not null)
                color=sourceMembers!.Contains(i)?GuiColor.FromRgb(0xFFE19D):destinationMembers!.Contains(i)?GuiColor.FromRgb(0x64CDEB):color.WithAlpha(.25f);
            batch.Neuron(Pixel(screen),radius*gui.Scale,color);shownNodes++;
            float d=Vector2.DistanceSquared(cursor,new(screen.X,screen.Y));
            if(interactive&&d<=nodeDistance&&(d<nodeDistance-.1f||screen.Z<nodeDepth)){nodeDistance=d;nodeDepth=screen.Z;nodeHover=scene.ById[n.ModuleId];}
        }
        SceneModule? labelHover=null;
        var occupied=new List<GuiRect> {new(clip.Right-100,clip.Top+10,96,90)};
        foreach(var label in labels.OrderByDescending(l=>l.Module.Module.Id==selectedModule).ThenBy(l=>camera.ToView(l.Module.Center).Z))
        {
            var rect=GraphLabelLayout.Place(label.Rect,new(clip.Left+4,clip.Top+4,clip.Width-8,clip.Height-36),occupied);
            occupied.Add(rect);
            if(Math.Abs(rect.X-label.Rect.X)+Math.Abs(rect.Y-label.Rect.Y)>10)
                batch.Guide(Pixel(new(label.Rect.Center.x,label.Rect.Center.y,.5f)),Pixel(new(rect.Center.x,rect.Center.y,.5f)),ModuleColor(label.Module.Module.Kind).WithAlpha(.45f));
            bool active=selectedModule==label.Module.Module.Id&&selectedLink=="";
            viewer.GraphLabel(gui,rect,label.Text,active?GuiColor.White:ModuleColor(label.Module.Module.Kind),active);
            if(interactive&&rect.Contains(new vec2(mouseX,mouseY)))labelHover=label.Module;
        }
        foreach(var label in connectionLabels)
        {
            var rect=GraphLabelLayout.Place(label.Rect,new(clip.Left+4,clip.Top+4,clip.Width-8,clip.Height-36),occupied);occupied.Add(rect);
            viewer.GraphLabel(gui,rect,label.Text,label.Color,label.Id==selectedLink);
            if(interactive&&rect.Contains(new vec2(mouseX,mouseY))){linkHover=label.Id;labelHover=null;nodeHover=null;}
        }
        bool handleHit=view==0&&selectedLink==""&&scene.ById.TryGetValue(selectedModule,out var gizmoModule)&&DrawGizmo(gizmoModule,camera,interactive);
        if(interactive&&click&&!handleHit)
        {
            var module=labelHover??nodeHover??(linkHover is null||linking?boxHover:null);
            if(module is not null)
            {
                if(linking)
                {
                    if(linkOrigin is null){linkOrigin=module.Module.Id;status="SELECT DESTINATION MODULE";}
                    else
                    {
                        Checkpoint();var origin=scene.ById[linkOrigin].Module;
                        var originMembers=NeuronGroups.Resolve(origin,scene.ById[origin.Id].Neurons.Select(i=>ns[i]).ToArray());
                        var targetMembers=NeuronGroups.Resolve(module.Module,module.Neurons.Select(i=>ns[i]).ToArray());
                        var sourceGroup=origin.Groups.FirstOrDefault(g=>g.Kind==GroupKind.Output&&originMembers[g.Id].Length>0);
                        var destinationGroup=module.Module.Groups.FirstOrDefault(g=>g.Kind==GroupKind.Input&&targetMembers[g.Id].Length>0)
                            ??module.Module.Groups.FirstOrDefault(g=>targetMembers[g.Id].Length>0);
                        var link=new BrainLink {Name=origin.Name+" -> "+module.Module.Name,
                            Source=new(){ModuleId=linkOrigin,Scope=sourceGroup is null?EndpointScope.AllNeurons:EndpointScope.Groups,GroupIds=sourceGroup is null?[]:[sourceGroup.Id]},
                            Destination=new(){ModuleId=module.Module.Id,Scope=destinationGroup is null?EndpointScope.AllNeurons:EndpointScope.Groups,GroupIds=destinationGroup is null?[]:[destinationGroup.Id]}};
                        document.Links.Add(link);selectedLink=link.Id;linking=false;linkOrigin=null;
                    }
                }
                else{selectedModule=module.Module.Id;selectedLink="";selectedPair=-1;fieldPage=0;dragging=!rotateMode;dragCheckpoint=false;}
            }
            else if(linkHover is not null)
            {
                selectedLink=linkHover;selectedPair=-1;fieldPage=0;
                if(scene.Graph.LinkErrors.TryGetValue(linkHover,out var error))status=error;
            }
            else {selectedModule=selectedLink="";selectedPair=-1;linkOrigin=null;dragging=false;}
        }
        string summary=$"{shownNodes}/{ns.Count} PAIRS | {shownEdges:N0}/{scene.Graph.Edges.Count:N0} EDGES | ARROWS: MODULE LINKS";
        if(scene.Graph.LinkErrors.Count>0)summary+=$" | {scene.Graph.LinkErrors.Count} INVALID";
        int chars=Math.Max(8,(int)((clip.Width-16)/6.75f));if(summary.Length>chars)summary=summary[..(chars-3)]+"...";
        viewer.GraphLabel(gui,new(clip.Left+6,clip.Bottom-24,Math.Min(clip.Width-12,gui.Style.Font.Measure(summary)*9/gui.Style.Font.Height+8),18),summary,GuiColor.FromRgb(0xB6CDDC));
        gui.PopClip();
    }
}
