using System.Numerics;

namespace ArcadeLab.Editor;

/// <summary>Z-up orbit basis. Camera coordinates are screen-right, screen-down, forward.</summary>
public static class GraphCamera
{
    public const float DefaultYaw=-.45f,DefaultPitch=.45f;
    public static Vector3 Rotate(Vector3 p,float yaw,float pitch)
    {
        float x=p.X*MathF.Cos(yaw)+p.Y*MathF.Sin(yaw),forward=-p.X*MathF.Sin(yaw)+p.Y*MathF.Cos(yaw);
        return new(x,-forward*MathF.Sin(pitch)-p.Z*MathF.Cos(pitch),forward*MathF.Cos(pitch)-p.Z*MathF.Sin(pitch));
    }
    public static Vector3 Unrotate(Vector3 p,float yaw,float pitch)
    {
        float forward=-p.Y*MathF.Sin(pitch)+p.Z*MathF.Cos(pitch),z=-p.Y*MathF.Cos(pitch)-p.Z*MathF.Sin(pitch);
        return new(p.X*MathF.Cos(yaw)-forward*MathF.Sin(yaw),p.X*MathF.Sin(yaw)+forward*MathF.Cos(yaw),z);
    }
    public static Vector3 Drag(Vector2 delta,float pixelsPerUnit,float yaw,float pitch,bool depth=false,char axis=' ')
    {
        float amount=(delta.X-delta.Y)/pixelsPerUnit;
        return axis switch
        {
            'X'=>new(amount,0,0),'Y'=>new(0,amount,0),'Z'=>new(0,0,amount),
            _=>Unrotate(depth?new Vector3(0,0,-delta.Y/pixelsPerUnit):new Vector3(delta/pixelsPerUnit,0),yaw,pitch)
        };
    }

}

/// <summary>Pinhole projection with Vulkan [0,1] depth. Near/far clipping happens before division;
/// the resulting screen-space vertices can use the existing native line-list pipeline.</summary>
public readonly record struct PerspectiveCamera(Vector3 Target,Vector2 Origin,float FocalLength,float Distance,float Near,float Far,float Yaw,float Pitch)
{
    public static PerspectiveCamera Create(Vector3 target,Vector2 origin,float fit,float zoom,float viewportHeight,float span,float yaw,float pitch)
    {
        float focal=Math.Max(100,viewportHeight*.9f),near=Math.Max(.001f,span*.01f);
        float distance=Math.Max(near*2,focal/Math.Max(.0001f,fit*zoom));
        return new(target,origin,focal,distance,near,distance+Math.Max(2,span)*100,yaw,pitch);
    }
    /// <summary>Grab-pan: translate camera and target along their right/up plane, preserving the lens center.</summary>
    public Vector3 PanTranslation(Vector2 delta)=>-GraphCamera.Unrotate(new Vector3(delta*(Distance/FocalLength),0),Yaw,Pitch);
    public Vector3 ZoomAnchorTranslation(PerspectiveCamera next,Vector2 cursor)
        =>Unproject(cursor,Distance)-next.Unproject(cursor,next.Distance);
    public Vector3 ToView(Vector3 world)=>GraphCamera.Rotate(world-Target,Yaw,Pitch)+new Vector3(0,0,Distance);
    public float PixelsPerUnit(Vector3 world)=>FocalLength/Math.Max(Near,ToView(world).Z);
    public Vector3 ProjectView(Vector3 p)=>new(Origin+new Vector2(p.X,p.Y)*(FocalLength/p.Z),
        Math.Clamp(Far/(Far-Near)-Far*Near/((Far-Near)*p.Z),0,1));
    public bool TryProject(Vector3 world,out Vector3 screen)
    {
        var p=ToView(world);
        if(p.Z<Near||p.Z>Far){screen=new(float.NaN);return false;}
        screen=ProjectView(p);return true;
    }
    public bool ProjectSegment(Vector3 a,Vector3 b,out Vector3 from,out Vector3 to)
        =>ProjectViewSegment(ToView(a),ToView(b),out from,out to);
    public bool ProjectViewSegment(Vector3 a,Vector3 b,out Vector3 from,out Vector3 to)
    {
        from=to=default;
        if(a.Z<Near&&b.Z<Near||a.Z>Far&&b.Z>Far)return false;
        if(a.Z<Near)a=Vector3.Lerp(a,b,(Near-a.Z)/(b.Z-a.Z));
        else if(b.Z<Near)b=Vector3.Lerp(b,a,(Near-b.Z)/(a.Z-b.Z));
        if(a.Z>Far)a=Vector3.Lerp(a,b,(Far-a.Z)/(b.Z-a.Z));
        else if(b.Z>Far)b=Vector3.Lerp(b,a,(Far-b.Z)/(a.Z-b.Z));
        // Roundoff at the clipped plane must not produce negative Vulkan depth.
        a.Z=Math.Clamp(a.Z,Near,Far);b.Z=Math.Clamp(b.Z,Near,Far);
        from=ProjectView(a);to=ProjectView(b);
        from.Z=Math.Clamp(from.Z,0,1);to.Z=Math.Clamp(to.Z,0,1);
        return true;
    }
    public Vector3 Unproject(Vector2 screen,float viewDepth)
        =>Target+GraphCamera.Unrotate(new Vector3((screen-Origin)*(viewDepth/FocalLength),viewDepth-Distance),Yaw,Pitch);
    public Vector3 Drag(Vector3 world,Vector2 delta,bool depth=false,char axis=' ')
        =>GraphCamera.Drag(delta,PixelsPerUnit(world),Yaw,Pitch,depth,axis);
}
