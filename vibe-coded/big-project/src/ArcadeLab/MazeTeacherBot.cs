using System.Numerics;

namespace ArcadeLab;

/// <summary>Graph exploration with rounded routes, independent gaze and local combat steering.</summary>
internal sealed class MazeTeacherBot(int seed)
{
    private MazeNavigationGraph? graph;
    private int episode=-1,pathIndex;
    private List<Vector2> path=[];
    private readonly List<Vector2> enemies=[],avoid=[];
    private readonly List<TeacherProjectile> shots=[];
    private Vector2 steering,lastDodge,lastPosition;
    private MazeControls heldMovement;
    private float movementAge;
    private float stalled,planAge;
    private char supply,requestedSupply;
    internal string Goal { get; private set; }="EXPLORE";
    internal IReadOnlyList<Vector2> Path=>path;
    internal MazeNavigationGraph? Graph=>graph;
    internal bool Dodging { get; private set; }
    internal const float EngageRange=4.5f;

    internal MazeControls Controls(MazeGame game)
    {
        if(episode!=game.Episode)
        {
            episode=game.Episode;graph=new(game.MapWidth,game.MapHeight);ReleaseControl();lastPosition=game.Position;
        }
        graph!.Observe(game);game.TeacherCombatView(enemies,shots);
        Vector2? close=null;float nearest=EngageRange;
        foreach(var enemy in enemies)
        {
            float distance=Vector2.Distance(game.Position,enemy);
            if(distance<nearest){nearest=distance;close=enemy;}
        }
        bool armed=game.Ammo+game.Reserve>0;
        avoid.Clear();foreach(var enemy in enemies)if(!armed||Vector2.Distance(enemy,game.Position)>EngageRange)avoid.Add(enemy);
        char wanted=game.Health<70&&graph.HasSupply('H')?'H':
            game.Ammo+game.Reserve<16&&graph.HasSupply('A')?'A':'\0';
        planAge+=game.Speed;
        if(requestedSupply!=wanted||path.Count==0||pathIndex>=path.Count||stalled>30)
        {
            requestedSupply=wanted;PlanRoute(game);stalled=0;
        }
        // Reconsider danger as enemies move, while keeping exploration goals stable.
        if(avoid.Count>0&&planAge>30)PlanRoute(game);
        Goal=supply=='H'?"HEALTH":supply=='A'?"AMMO":"EXPLORE";
        Vector2 desired=Follow(game.Position);
        if(close is {} target&&armed&&game.Health>=35&&supply!='H'&&game.Ammo>0)
        {
            Goal="ENGAGE";var toward=target-game.Position;float distance=toward.Length();
            if(distance>.01f)
            {
                toward/=distance;
                desired=distance>2.8f?toward:distance<1.8f?-toward:new Vector2(-toward.Y,toward.X)*.65f;
                if(!graph.Clear(game.Position,game.Position+desired*.55f))desired=Follow(game.Position);
            }
        }
        Vector2 navigation=desired;
        // Distant enemies repel the route rather than becoming chase targets.
        foreach(var enemy in avoid)
        {
            var away=game.Position-enemy;float distance=away.Length();
            if(distance>.01f&&distance<6)
            {
                var force=away/distance*((6-distance)*(supply!='\0'?.1f:.7f));
                // Graph costs already choose a safer route. Local avoidance
                // steers sideways around a distant threat rather than cancelling
                // forward progress and bouncing between the same room ends.
                if(distance>2&&navigation.LengthSquared()>.01f)
                {
                    var course=Vector2.Normalize(navigation);float opposing=Vector2.Dot(force,course);
                    if(opposing<0)force-=course*opposing;
                }
                desired+=force;
            }
        }
        desired=Limit(desired,1);
        if(!graph.Clear(game.Position,game.Position+desired*.6f))desired=navigation;
        Vector2 travel=desired;
        desired=AvoidProjectiles(graph,game.Position,desired,shots,lastDodge,out bool dodging);
        Dodging=dodging;lastDodge=dodging?desired:Vector2.Zero;
        float gaze;
        if(close is {} aim&&game.Ammo>0)gaze=MathF.Atan2(aim.Y-game.Position.Y,aim.X-game.Position.X);
        else if(travel.LengthSquared()>.01f)
        {
            float scan=graph.At((int)game.Position.X,(int)game.Position.Y)?.Kind==MazeNavigationGraph.RegionKind.Corridor?.25f:.65f;
            gaze=MathF.Atan2(travel.Y,travel.X)+scan*MathF.Sin(game.Tick*game.Speed*.012f);
        }
        else gaze=game.Heading;
        float angle=MathF.IEEERemainder(gaze-game.Heading,2*MathF.PI);
        MazeControls controls=MathF.Abs(angle)>.085f*game.Speed*.51f?(angle>0?MazeControls.TurnRight:MazeControls.TurnLeft):MazeControls.None;
        float heading=game.Heading+((controls&MazeControls.TurnRight)!=0?1:(controls&MazeControls.TurnLeft)!=0?-1:0)*.085f*game.Speed;
        controls|=Movement(game,desired,heading);
        if(game.Ammo==0&&game.Reserve>0&&!game.Reloading)controls|=MazeControls.Reload;
        if(close is {} firing&&game.Ammo>0&&!game.Reloading)
        {
            float error=MathF.IEEERemainder(MathF.Atan2(firing.Y-game.Position.Y,firing.X-game.Position.X)-heading,2*MathF.PI);
            if(MathF.Abs(error)<MathF.Atan2(.18f,Vector2.Distance(firing,game.Position)))controls|=MazeControls.Fire;
        }
        stalled=Vector2.DistanceSquared(lastPosition,game.Position)<.00002f?stalled+game.Speed:0;
        lastPosition=game.Position;return controls;
    }
    internal void ReleaseControl(){path.Clear();pathIndex=0;steering=Vector2.Zero;heldMovement=MazeControls.None;movementAge=100;stalled=0;}

    private void PlanRoute(MazeGame game)
    {
        supply=requestedSupply;path=graph!.Plan(game.Position,supply,avoid,seed);
        // A remembered supply may not yet have a discovered, body-safe route.
        // Keep exploring until it becomes reachable instead of waiting for it.
        if(path.Count==0&&supply!='\0'){supply='\0';path=graph.Plan(game.Position,supply,avoid,seed);}
        pathIndex=1;planAge=0;
    }
    private Vector2 Follow(Vector2 position)
    {
        if(pathIndex>=path.Count)return Vector2.Zero;
        while(pathIndex<path.Count-1)
        {
            var delta=path[pathIndex]-path[pathIndex-1];float length=delta.LengthSquared();
            if(Vector2.Distance(position,path[pathIndex])<.22f||length>0&&Vector2.Dot(position-path[pathIndex],delta)>=0)pathIndex++;
            else break;
        }
        if(Vector2.Distance(position,path[^1])<.18f){pathIndex=path.Count;return Vector2.Zero;}
        // Measure lookahead from the nearest point on the current segment,
        // rather than aiming at a far endpoint and overcorrecting across a room.
        var start=path[pathIndex-1];var segment=path[pathIndex]-start;
        float projection=segment.LengthSquared()>.0001f?Math.Clamp(Vector2.Dot(position-start,segment)/segment.LengthSquared(),0,1):1;
        Vector2 look=start+segment*projection;float remaining=.85f;
        for(int i=pathIndex;i<path.Count;i++)
        {
            var next=path[i];float length=Vector2.Distance(look,next);
            var candidate=length>remaining?Vector2.Lerp(look,next,remaining/length):next;
            if(!graph!.Clear(position,candidate,.18f))break;
            look=candidate;if(length>=remaining)break;remaining-=length;
        }
        var desired=look-position;return Limit(desired/.4f,1);
    }
    private MazeControls Movement(MazeGame game,Vector2 desired,float heading)
    {
        // Smooth desired world velocity, then hold a discrete button combination
        // with hysteresis. No per-frame error diffusion between left/right keys.
        steering=Dodging||steering.LengthSquared()<.001f?desired:Vector2.Lerp(steering,desired,1-MathF.Exp(-game.Speed/6));
        var forward=new Vector2(MathF.Cos(heading),MathF.Sin(heading));var right=new Vector2(-forward.Y,forward.X);
        Vector2 Velocity(MazeControls buttons)
        {
            var value=forward*(((buttons&MazeControls.Forward)!=0?1:0)-((buttons&MazeControls.Backward)!=0?1:0))+
                right*(((buttons&MazeControls.StrafeRight)!=0?1:0)-((buttons&MazeControls.StrafeLeft)!=0?1:0));
            return Limit(value,1);
        }
        bool Safe(Vector2 velocity)=>graph!.Clear(game.Position,game.Position+velocity*MathF.Max(.18f,.1f*game.Speed),.18f);
        float best=float.PositiveInfinity;MazeControls selected=MazeControls.None;
        for(int f=-1;f<=1;f++)for(int r=-1;r<=1;r++)
        {
            var buttons=(f>0?MazeControls.Forward:f<0?MazeControls.Backward:MazeControls.None)|(r>0?MazeControls.StrafeRight:r<0?MazeControls.StrafeLeft:MazeControls.None);
            var velocity=Velocity(buttons);if(!Safe(velocity))continue;
            float score=Vector2.DistanceSquared(steering,velocity);
            if(score<best){best=score;selected=buttons;}
        }
        movementAge+=game.Speed;
        var heldVelocity=Velocity(heldMovement);float heldScore=Vector2.DistanceSquared(steering,heldVelocity);
        bool safeHeld=Safe(heldVelocity);
        if(!Dodging&&safeHeld&&heldMovement!=MazeControls.None&&
           (movementAge<6||heldScore<best+.12f))selected=heldMovement;
        if(desired.LengthSquared()<.015f)selected=MazeControls.None;
        if(selected!=heldMovement){heldMovement=selected;movementAge=0;}
        return selected;
    }
    internal static Vector2 AvoidProjectiles(MazeNavigationGraph graph,Vector2 position,Vector2 desired,IReadOnlyList<TeacherProjectile> shots,Vector2 previous,out bool dodging)
    {
        dodging=false;
        float Risk(Vector2 move)
        {
            float risk=0;
            foreach(var shot in shots)
            {
                var relative=shot.Position-position;var velocity=shot.Velocity-move*.1f;
                float speed=velocity.LengthSquared();if(speed<.00001f)continue;
                float time=-Vector2.Dot(relative,velocity)/speed;
                if(time<0||time>12)continue;
                float separation=(relative+velocity*time).Length();
                risk+=MathF.Pow(MathF.Max(0,.65f-separation),2)*100/(1+time*.08f);
            }
            return risk;
        }
        if(Risk(desired)<.05f)return desired;
        dodging=true;Vector2 best=desired;float score=float.PositiveInfinity;
        for(int i=-1;i<16;i++)
        {
            var candidate=i<0?Vector2.Zero:new Vector2(MathF.Cos(i*MathF.PI/8),MathF.Sin(i*MathF.PI/8));
            if(!graph.Clear(position,position+candidate*.6f))continue;
            float value=Risk(candidate)+.3f*Vector2.DistanceSquared(desired,candidate)+.1f*Vector2.DistanceSquared(previous,candidate);
            if(value<score){score=value;best=candidate;}
        }
        return best;
    }
    private static Vector2 Limit(Vector2 value,float length)=>value.LengthSquared()>length*length?Vector2.Normalize(value)*length:value;
}
