using System.Numerics;
using GlmSharp;
using MYT.Kit.Gui;

namespace ArcadeLab.Editor;

public sealed partial class EditorApp
{
    private bool rotateMode,gizmoDragging;
    private int gizmoAxis=-1;
    private Vector3 gizmoPivot;
    private void DrawTransformMode(float x,float y)
    {
        if(Button("MOVE",x,y,78,!rotateMode))rotateMode=false;
        if(Button("ROTATE",x+84,y,88,rotateMode))rotateMode=true;
    }
    private bool DrawGizmo(SceneModule scene,PerspectiveCamera camera,bool interactive)
    {
        var module=scene.Module;var pivot=gizmoDragging?gizmoPivot:scene.Center;
        if(!camera.TryProject(pivot,out var center))return false;
        float radius=65/Math.Max(.001f,camera.PixelsPerUnit(pivot));
        var cursor=new Vector2(mouseX,mouseY);int hover=-1;float nearest=8;
        void Line(Vector3 a,Vector3 b,GuiColor color)
        {
            if(!camera.ProjectSegment(a,b,out var pa,out var pb))return;
            // Handles use the foreground overlay so they remain usable inside dense modules.
            var from=new Vector2(pa.X,pa.Y);var to=new Vector2(pb.X,pb.Y);
            if(!ViewportGeometry.ClipLine(CanvasContent,ref from,ref to))return;
            int steps=Math.Max(1,(int)Vector2.Distance(from,to));
            for(int i=0;i<=steps;i++)
            {
                var p=Vector2.Lerp(from,to,(float)i/steps);
                viewer.Graph.OverlayRect(new GuiRect(p.X-1,p.Y-1,2,2).Scaled(gui.Scale),color);
            }
        }
        for(int axis=0;axis<3;axis++)
        {
            var direction=TransformGizmo.Axis(axis);var color=axis==0?GraphOrientation.XColor:axis==1?GraphOrientation.YColor:GraphOrientation.ZColor;
            if(gizmoDragging&&axis==gizmoAxis)color=GuiColor.White;
            void Segment(Vector3 a,Vector3 b)
            {
                Line(a,b,color);
                if(interactive&&camera.ProjectSegment(a,b,out var pa,out var pb))
                {
                    float distance=TransformGizmo.SegmentDistance(cursor,new(pa.X,pa.Y),new(pb.X,pb.Y));
                    if(distance<nearest){nearest=distance;hover=axis;}
                }
            }
            if(rotateMode)
            {
                var u=TransformGizmo.Axis((axis+1)%3);var v=Vector3.Cross(direction,u);
                for(int i=0;i<72;i++)
                {
                    float a=i*MathF.Tau/72,b=(i+1)*MathF.Tau/72;
                    Segment(pivot+(u*MathF.Cos(a)+v*MathF.Sin(a))*radius,pivot+(u*MathF.Cos(b)+v*MathF.Sin(b))*radius);
                }
            }
            else
            {
                var tip=pivot+direction*radius;Segment(pivot,tip);
                var side=TransformGizmo.Axis((axis+1)%3)*radius*.12f;
                Segment(tip,tip-direction*radius*.22f+side);Segment(tip,tip-direction*radius*.22f-side);
                if(camera.TryProject(tip,out var point))viewer.GraphLabel(gui,new(point.X+3,point.Y-8,18,18),"XYZ"[axis].ToString(),color);
            }
        }
        if(interactive&&click&&hover>=0)
        {
            gizmoDragging=true;gizmoAxis=hover;gizmoPivot=pivot;dragging=false;dragCheckpoint=false;
        }
        if(gizmoDragging&&mouseDown&&!click)
        {
            var previous=new Vector2(oldMouseX/gui.Scale,oldMouseY/gui.Scale);var axis=TransformGizmo.Axis(gizmoAxis);
            if(Vector2.DistanceSquared(previous,cursor)>.01f)
            {
                if(!dragCheckpoint){Checkpoint(positionsOnly:true);dragCheckpoint=true;}
                if(rotateMode)
                {
                    float angle;
                    if(TransformGizmo.RingVector(camera,previous,gizmoPivot,axis,out var before)&&TransformGizmo.RingVector(camera,cursor,gizmoPivot,axis,out var after))
                        angle=TransformGizmo.SignedAngle(before,after,axis);
                    else angle=((cursor.X-previous.X)-(cursor.Y-previous.Y))*.01f;
                    var rotation=Quaternion.CreateFromAxisAngle(axis,angle);
                    module.Rotation=ModuleTransform.Euler(rotation*ModuleTransform.Rotation(module.Rotation));
                    module.Position=ModuleTransform.Position(gizmoPivot+Vector3.Transform(ModuleTransform.Vector(module.Position)-gizmoPivot,rotation));
                }
                else
                {
                    float amount;
                    if(TransformGizmo.AxisParameter(camera,previous,pivot,axis,out float before)&&TransformGizmo.AxisParameter(camera,cursor,pivot,axis,out float after))amount=after-before;
                    else amount=(previous.Y-cursor.Y)/Math.Max(.001f,camera.PixelsPerUnit(pivot));
                    var delta=axis*amount;module.Position+=ModuleTransform.Position(delta);gizmoPivot+=delta;
                }
                Invalidate(positionsOnly:true);scene.Synchronize(linkScene!.Graph.Neurons);
                // Nearest-neighbor links depend on placement; rebuild once after the gesture.
            }
        }
        return gizmoDragging||hover>=0;
    }
}
