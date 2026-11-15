using System.Numerics;

namespace ArcadeLab.Editor;

public static class TransformGizmo
{
    public static Vector3 Axis(int index)=>index==0?Vector3.UnitX:index==1?Vector3.UnitY:Vector3.UnitZ;
    public static float SegmentDistance(Vector2 p,Vector2 a,Vector2 b)
    {
        var d=b-a;return Vector2.Distance(p,a+d*(d.LengthSquared()<.0001f?0:Math.Clamp(Vector2.Dot(p-a,d)/d.LengthSquared(),0,1)));
    }
    public static bool AxisParameter(PerspectiveCamera camera,Vector2 pixel,Vector3 origin,Vector3 axis,out float value)
    {
        var start=camera.Unproject(pixel,camera.Near);var ray=Vector3.Normalize(camera.Unproject(pixel,camera.Far)-start);
        float dot=Vector3.Dot(ray,axis),denominator=1-dot*dot;
        value=0;if(denominator<.002f)return false;
        var offset=start-origin;value=(Vector3.Dot(axis,offset)-dot*Vector3.Dot(ray,offset))/denominator;return true;
    }
    public static bool RingVector(PerspectiveCamera camera,Vector2 pixel,Vector3 origin,Vector3 normal,out Vector3 vector)
    {
        var start=camera.Unproject(pixel,camera.Near);var ray=Vector3.Normalize(camera.Unproject(pixel,camera.Far)-start);
        float dot=Vector3.Dot(ray,normal);vector=default;if(Math.Abs(dot)<.03f)return false;
        float t=Vector3.Dot(origin-start,normal)/dot;if(t<0)return false;
        vector=start+ray*t-origin;if(vector.LengthSquared()<.000001f)return false;vector=Vector3.Normalize(vector);return true;
    }
    public static float SignedAngle(Vector3 before,Vector3 after,Vector3 normal)
        =>MathF.Atan2(Vector3.Dot(normal,Vector3.Cross(before,after)),Vector3.Dot(before,after));
}
