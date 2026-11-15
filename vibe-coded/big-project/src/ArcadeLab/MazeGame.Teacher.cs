using System.Numerics;

namespace ArcadeLab;

public sealed partial class MazeGame
{
    // Local teacher perception only; these values are never neural sensory ports.
    internal char TeacherVisibleMapTile(int x,int y)
    {
        if(x<0||y<0||x>=MapWidth||y>=MapHeight)return '\0';
        var point=new Vector2(x+.5f,y+.5f);float distance=Vector2.Distance(position,point);
        if(distance>6)return '\0';
        if(map[y][x]!='#')return TeacherVisibleFloor(x,y);
        // Reveal the visible wall face, without looking through it at floor behind.
        var face=distance>.001f?point+(position-point)/distance*.75f:point;
        return Visible(position,face)?'#':'\0';
    }
    internal char TeacherVisibleFloor(int x,int y)
    {
        var point=new Vector2(x+.5f,y+.5f);
        if(x<0||y<0||x>=MapWidth||y>=MapHeight||Vector2.DistanceSquared(position,point)>36||!Visible(position,point))return '\0';
        return map[y][x]=='E'?'.':map[y][x];
    }
    internal void TeacherCombatView(List<Vector2> targets,List<TeacherProjectile> shots)
    {
        targets.Clear();shots.Clear();
        foreach(var enemy in enemies)
            if(enemy.Health>0&&Vector2.DistanceSquared(position,enemy.Position)<144&&Visible(position,enemy.Position))targets.Add(enemy.Position);
        foreach(var shot in projectiles)
            if(shot.Enemy&&Vector2.DistanceSquared(position,shot.Position)<64&&Visible(position,shot.Position))shots.Add(new(shot.Position,shot.Velocity));
    }
    internal Vector2? TeacherVisibleEnemy()
    {
        Vector2? target=null;float nearest=36;
        foreach(var enemy in enemies)
        {
            float distance=Vector2.DistanceSquared(position,enemy.Position);
            if(enemy.Health>0&&distance<nearest&&Visible(position,enemy.Position)){nearest=distance;target=enemy.Position;}
        }
        return target;
    }
}

internal readonly record struct TeacherProjectile(Vector2 Position,Vector2 Velocity);
