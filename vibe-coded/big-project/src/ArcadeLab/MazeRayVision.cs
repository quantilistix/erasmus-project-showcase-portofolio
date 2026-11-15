using System.Numerics;

namespace ArcadeLab;

public sealed partial class MazeGame
{
    public const int VisionRayCount = 384;
    public const float VisionFieldOfView = 2 * MathF.PI / 3, VisionRange = 20;
    /// <summary>Diagnostic depth only. Neural input contains RGB light, not these distances.</summary>
    public float[] VisionDepth { get; } = new float[VisionRayCount];
    private void RenderFirstPersonVision()
    {
        for(int ray=0;ray<VisionRayCount;ray++)
        {
            float angle=heading+((ray+.5f)/VisionRayCount-.5f)*VisionFieldOfView;
            Vector2 direction=new(MathF.Cos(angle),MathF.Sin(angle));
            (float distance,uint color)=CastVisionRay(direction);
            VisionDepth[ray]=distance; float brightness=DistanceBrightness(distance);
            SetVisionPixel(ray,((color>>16)&255)/255f*brightness,((color>>8)&255)/255f*brightness,(color&255)/255f*brightness);
        }
    }
    private (float Distance,uint Color) CastVisionRay(Vector2 direction)
    {
        var (x,y)=Cell(position); int sx=direction.X>=0?1:-1,sy=direction.Y>=0?1:-1;
        float dx=direction.X==0?float.PositiveInfinity:MathF.Abs(1/direction.X),dy=direction.Y==0?float.PositiveInfinity:MathF.Abs(1/direction.Y);
        float mx=(sx>0?x+1-position.X:position.X-x)*dx,my=(sy>0?y+1-position.Y:position.Y-y)*dy;
        float nearest=VisionRange; uint color=0;
        // Grid traversal finds the first wall and any pickup/exit intercepted before it.
        for(int step=0;step<128;step++)
        {
            char tile=Tile(new(x+.5f,y+.5f));
            if(tile is 'H' or 'A' or 'E')
            {
                float hit=RayCircle(direction,new(x+.5f,y+.5f),tile=='E'?.4f:.28f);
                if(hit<nearest) { nearest=hit; color=tile switch { 'H'=>0x6EF0B0u,'A'=>0xFFE19Du,_=>0x76EDE5u }; }
            }
            bool vertical=mx<my; float boundary=vertical?mx:my;
            if(boundary>=nearest || boundary>VisionRange)break;
            if(vertical) { x+=sx;mx+=dx; }else { y+=sy;my+=dy; }
            if(Tile(new(x+.5f,y+.5f))=='#')
            { nearest=boundary; color=vertical?0x7F9DB5u:0x647F98u;break; }
        }
        foreach(var enemy in enemies)
        {
            if(enemy.Health<=0)continue;
            float hit=RayCircle(direction,enemy.Position,.28f);
            if(hit<nearest) { nearest=hit; color=0xED6370; }
        }
        foreach(var projectile in projectiles)
        {
            float hit=RayCircle(direction,projectile.Position,.045f);
            if(hit<nearest) { nearest=hit;color=projectile.Enemy?0xFFA861u:0xFFF2B0u; }
        }
        return (nearest,color);
    }
    private float RayCircle(Vector2 direction,Vector2 centre,float radius)
    {
        Vector2 offset=centre-position; float along=Vector2.Dot(offset,direction);
        if(along<=0)return float.PositiveInfinity;
        float perpendicular=offset.LengthSquared()-along*along,rr=radius*radius;
        if(perpendicular>rr)return float.PositiveInfinity;
        return MathF.Max(0,along-MathF.Sqrt(MathF.Max(0,rr-perpendicular)));
    }
}
