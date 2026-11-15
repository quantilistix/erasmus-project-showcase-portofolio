using System.Numerics;

namespace ArcadeLab;

public enum MazeAction { Wait, Forward, Backward, TurnLeft, TurnRight, StrafeLeft, StrafeRight, Fire, ForwardFire, Reload }
public readonly record struct MazeEnemySpawn(float X, float Y, int Health = 3);
public sealed record MazeScenario(string[] Map, Vector2 Player, float Heading, MazeEnemySpawn[] Enemies, float Health = 100, int Ammo = 8, int Reserve = 24);
public readonly record struct MazeEvents(int Hits, int Kills, int AmmoPickups, float Healed, float Damage, bool Completed, int MissedShots = 0, int VisibleShotHits = 0, int UnseenShotMisses = 0);

/// <summary>2D world physics. Only Pixels, Audio and primitive sensations reach the agent.
/// Teacher labels are computed exclusively when Step(..., teaching: true) is requested.</summary>
public sealed partial class MazeGame
{
    public const int DefaultMazeSize = 41, CompletionsPerStage = 5, MaximumDifficultyStage = 4;
    // 0.2 health per simulated second (60 physics ticks); 0.1 per wall second at default half speed/60 fps.
    public const float HealthDrainPerSecond = .2f;
    public const float WallPainRate = .1f;
    public const float DeathPenalty = 8f;
    public const float HitReward = 4f, KillReward = 16f;
    public const float MissedShotPenalty = .65f, ExplorationReward = 1f, NewRegionReward = 3f, MaximumPleasure = 256f;
    public const float VisualDarknessRewardRate = 100f;
    public const float StationaryGrace = 30, StationaryRamp = 270, StationaryPainRate = 1f, StationaryRadius = .65f;
    // Legacy dimensions retain checkpoint indexing; world vision is a 1D ray line.
    public const int ViewWidth = 24, ViewHeight = 18, HudBarPixels = 24;
    public const int PixelCount = VisionRayCount + 2 * HudBarPixels;
    public const int AudioSize = 64, SampleRate = AudioSize * 60, ActionCount = 10, MagazineSize = 8;
    private readonly Random random;
    private readonly MazeScenario? scenario;
    private char[][] map = [];
    private readonly List<Enemy> enemies = [];
    private readonly List<Projectile> projectiles = [];
    private readonly HashSet<(int, int)> explored = [], exploredRegions = [];
    private Vector2 position;
    private float heading, cooldown, reload, age, footstep, stationary;
    public float VisualDarknessScore { get; private set; }
    public float VisualDarknessTeacherPleasure { get; private set; }
    // Exploration labels have a finite episode budget and never pay for revisits.
    public float ExplorationTeacherPleasure { get; private set; }
    public float ExplorationBalance { get; private set; }
    private readonly SineMixer soundMixer = new(SampleRate);
    private readonly List<SoundEvent> sounds = [];
    public IReadOnlyList<SoundEvent> Sounds => sounds;
    private Vector2 stationaryAnchor;
    public int NewCellsThisFrame { get; private set; }
    public int NewRegionsThisFrame { get; private set; }
    public float StationaryTeacherPain { get; private set; }
    public float WallCollisionPain { get; private set; }
    public float AppliedIdealReward => TeacherTarget.Pleasure - TeacherTarget.Pain;
    /// <summary>Luminance projection retained for diagnostics; neural vision uses RgbPixels.</summary>
    public float[] Pixels { get; } = new float[PixelCount];
    public float[] RgbPixels { get; } = new float[MazeVision.FrameSize];
    public float[] Audio { get; } = new float[AudioSize];
    public float[] AudioRight { get; } = new float[AudioSize];
    public float[] MapPixels { get; private set; } = [];
    public int MapWidth => map[0].Length;
    public int MapHeight => map.Length;
    public Vector2 Position => position; // Observer/debug only, never a neural input.
    public float Heading => heading;
    public float Health { get; private set; }
    public int Ammo { get; private set; }
    public int Reserve { get; private set; }
    public int Tick { get; private set; }
    public int Score { get; private set; }
    public int Episode { get; private set; }
    public int ExitPathLength { get; private set; }
    public float EpisodeTimeLimit { get; private set; } = 3600;
    public bool Done => Health <= 0;
    public int DifficultyStage { get; private set; }
    public long Level { get; private set; }=1;
    public bool AmmoPickupsUnlocked=>Level>=2;
    public bool EnemiesUnlocked=>Level>=3;
    public int CorridorWidth => DifficultyStage<2?3:DifficultyStage<4?2:1;
    public int RoomMinimumSide => new[]{9,7,5,3,3}[DifficultyStage];
    public int RoomMaximumSide => new[]{13,11,9,7,3}[DifficultyStage];
    public void SetCompletedMazes(int completed)
    { if(completed<0)throw new ArgumentOutOfRangeException(nameof(completed));Level=(long)completed+1;DifficultyStage=Math.Min(MaximumDifficultyStage,completed/CompletionsPerStage); }
    public bool TrainingLayout { get; private set; }
    public void SetTrainingLayout(bool enabled) => TrainingLayout = enabled;
    public bool Completed { get; private set; }
    public bool DiedThisFrame { get; private set; }
    public float PrimitiveDeathPenalty => DiedThisFrame ? DeathPenalty : 0;
    public float Speed { get; private set; }
    public bool Reloading => reload > 0;
    public int ProjectileCount => projectiles.Count;
    public int LivingEnemies => enemies.Count(e => e.Health > 0);
    public MazeEvents Events { get; private set; }
    public RewardTeachingTarget TeacherTarget { get; private set; }
    public bool TeacherAvailable { get; private set; }
    public float PrimitivePleasure => Math.Clamp(Events.Healed / 40, 0, 1);
    private float MissingHealthSquared => (1 - Health / 100) * (1 - Health / 100);
    public float PrimitivePain => Math.Clamp(Events.Damage / 25 + PrimitiveDeathPenalty + WallCollisionPain + .3f * MissingHealthSquared, 0, 1);
    // Ongoing low-health discomfort is time-scaled, so half speed does not double its cost per journey.
    public float PrimitiveDamagePenalty => Math.Clamp(Events.Damage / 25, 0, 1);
    public float PrimitiveLowHealthCost => .006f * Speed * MissingHealthSquared;
    public float PrimitivePainCost => PrimitiveDamagePenalty + WallCollisionPain + PrimitiveDeathPenalty + PrimitiveLowHealthCost;
    public float PrimitiveLearningReward => PrimitivePleasure - PrimitivePainCost;
    public MazeGame(int seed = 42, float speed = .5f, MazeScenario? scenario = null, bool trainingLayout = false)
    { random = new(seed); this.scenario = scenario; TrainingLayout = trainingLayout; SetSpeed(speed); Reset(); }
    public void SetSpeed(float speed)
    { if (!float.IsFinite(speed) || speed < .05f || speed > 2) throw new ArgumentOutOfRangeException(nameof(speed)); Speed = speed; }
    public void Reset()
    {
        Episode++; ExplorationBalance=ExplorationTeacherPleasure=0; Tick = Score = 0; VisualDarknessTeacherPleasure=0; ExitPathLength = 0; EpisodeTimeLimit = 3600; Health = 100; Ammo = 0; Reserve = 0;
        Completed = DiedThisFrame = false; age = cooldown = reload = footstep = stationary = 0; soundMixer.Clear(); sounds.Clear();
        projectiles.Clear(); enemies.Clear(); explored.Clear(); exploredRegions.Clear(); observerTrail.Clear(); NewCellsThisFrame = NewRegionsThisFrame = 0; StationaryTeacherPain = WallCollisionPain = 0; Events = default; TeacherTarget = default; TeacherAvailable = false;
        if (scenario is null) Generate();
        else
        {
            if (scenario.Map.Length < 3 || scenario.Map.Any(r => r.Length != scenario.Map[0].Length) || scenario.Map[0].Length < 3)
                throw new ArgumentException("Scenario must be rectangular and at least 3x3.");
            if (!float.IsFinite(scenario.Health) || scenario.Health <= 0 || scenario.Health > 100 || scenario.Ammo < 0 || scenario.Ammo > MagazineSize || scenario.Reserve < 0 || scenario.Reserve > 96)
                throw new ArgumentException("Invalid scenario health or ammunition.");
            Health = scenario.Health; Ammo = scenario.Ammo; Reserve = scenario.Reserve;
            map = scenario.Map.Select(r => r.ToCharArray()).ToArray(); position = scenario.Player; heading = scenario.Heading;
            foreach (var e in scenario.Enemies) enemies.Add(new(new(e.X, e.Y), e.Health, 45));
            if (!Free(position, .2f)) throw new ArgumentException("Player must spawn in open space.");
        }
        stationaryAnchor = position; explored.Add(Cell(position)); exploredRegions.Add(((int)position.X/5,(int)position.Y/5)); observerTrail.Enqueue(position);
        MapPixels = new float[MapWidth * MapHeight]; ResetTactile(); Render(); Array.Clear(Audio); Array.Clear(AudioRight);
    }
    private void Generate()
    {
        // Both modes generate a fresh large maze. Training only changes encounter difficulty/resources.
        int n = DefaultMazeSize;
        map = Enumerable.Range(0, n).Select(_ => Enumerable.Repeat('#', n).ToArray()).ToArray();
        if(DifficultyStage<MaximumDifficultyStage)GenerateOpenLayout(n);
        else
        {
        var stack = new Stack<(int x, int y)>(); stack.Push((1, 1)); map[1][1] = '.';
        (int x, int y)[] dirs = [(2, 0), (-2, 0), (0, 2), (0, -2)];
        while (stack.Count > 0)
        {
            var p = stack.Peek(); random.Shuffle(dirs); bool added = false;
            foreach (var d in dirs)
            {
                int x = p.x + d.x, y = p.y + d.y;
                if (x <= 0 || y <= 0 || x >= n - 1 || y >= n - 1 || map[y][x] != '#') continue;
                map[y][x] = map[p.y + d.y / 2][p.x + d.x / 2] = '.'; stack.Push((x, y)); added = true; break;
            }
            if (!added) stack.Pop();
        }
        // Rooms connect to the carved corridors; the border always remains solid.
        for (int i = 0; i < n * n / 180; i++)
        {
            int roomX = 1 + 2 * random.Next((n - 3) / 2), roomY = 1 + 2 * random.Next((n - 3) / 2);
            for (int y = roomY; y <= roomY + 2; y++) for (int x = roomX; x <= roomX + 2; x++) map[y][x] = '.';
        }
        for (int y = 1; y <= 3; y++) for (int x = 1; x <= 3; x++) map[y][x] = '.';
        // Loops allow flanking and multiple routes instead of a single corridor solution.
        for (int i = 0; i < n * n / 18; i++)
        {
            int x = random.Next(2, n - 2), y = random.Next(2, n - 2);
            if ((map[y][x - 1] == '.' && map[y][x + 1] == '.') || (map[y - 1][x] == '.' && map[y + 1][x] == '.')) map[y][x] = '.';
        }
        }
        position = new(1.5f, 1.5f); heading = random.Next(2) * MathF.PI / 2;
        // Exit is the furthest reachable floor by path length.
        var distances = new Dictionary<(int, int), int> { [(1, 1)] = 0 }; var queue = new Queue<(int x, int y)>(); queue.Enqueue((1, 1));
        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            foreach (var d in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            { var next = (p.x + d.Item1, p.y + d.Item2); if (map[next.Item2][next.Item1] != '#' && distances.TryAdd(next, distances[p] + 1)) queue.Enqueue(next); }
        }
        var exit = distances.MaxBy(p => p.Value).Key; map[exit.Item2][exit.Item1] = 'E';
        ExitPathLength = distances[exit];
        // Allow navigation, turning, combat and detours on a longer route without the old short timeout.
        EpisodeTimeLimit = Math.Max(3600, ExitPathLength * 40);
        if (TrainingLayout)
        {
            // Reachable nearby opportunities teach primitive healing and nonprimitive ammo rewards.
            foreach (var item in new[] { (distance: 2, tile: 'H'), (distance: 3, tile: 'A') })
            {
                if(item.tile=='A'&&!AmmoPickupsUnlocked)continue;
                var choices = distances.Keys.Where(p => distances[p] == item.distance && map[p.Item2][p.Item1] == '.').ToArray();
                if (choices.Length > 0) { var p = choices[random.Next(choices.Length)]; map[p.Item2][p.Item1] = item.tile; }
            }
        }
        var floors = distances.Keys.Where(p => distances[p] >= 5 && map[p.Item2][p.Item1] == '.').ToArray(); random.Shuffle(floors);
        int density=(TrainingLayout?96:48)+(MaximumDifficultyStage-DifficultyStage)*24;
        int population = Math.Clamp(distances.Count / density, DifficultyStage<MaximumDifficultyStage?3:TrainingLayout?3:6, 48);
        population = Math.Min(population, floors.Length / 3);
        for (int i = 0; i < population * 3; i++)
        {
            var p = floors[i];
            if (i < population)
            {
                if(EnemiesUnlocked)enemies.Add(new(new(p.Item1 + .5f, p.Item2 + .5f), TrainingLayout ? 2 : 3, random.Next(TrainingLayout ? 75 : 35, 110)));
            }
            else if(i>=population*2)map[p.Item2][p.Item1]='H';
            else if(AmmoPickupsUnlocked)map[p.Item2][p.Item1]='A';
        }
    }
    private static (int, int) Cell(Vector2 p) => ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y));
    private char Tile(Vector2 p) { var (x, y) = Cell(p); return x < 0 || y < 0 || y >= MapHeight || x >= MapWidth ? '#' : map[y][x]; }
    private bool Free(Vector2 p, float radius) => Tile(p + new Vector2(radius, radius)) != '#' && Tile(p + new Vector2(-radius, radius)) != '#' && Tile(p + new Vector2(radius, -radius)) != '#' && Tile(p - new Vector2(radius, radius)) != '#';
    private Vector2 Move(Vector2 p, Vector2 delta)
    { if (Free(p + new Vector2(delta.X, 0), .18f)) p.X += delta.X; if (Free(p + new Vector2(0, delta.Y), .18f)) p.Y += delta.Y; return p; }
    private bool Visible(Vector2 a, Vector2 b)
    {
        int steps = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b) / .12f));
        for (int i = 1; i < steps; i++) if (Tile(Vector2.Lerp(a, b, (float)i / steps)) == '#') return false;
        return true;
    }
    private void Sound(float tone, float volume, Vector2? origin = null)
    {
        float duration = tone <= 110 ? .09f : tone == 800 || tone == 350 ? .18f : tone >= 560 ? .12f : .10f;
        float pan = 0;
        if (origin.HasValue && Vector2.DistanceSquared(origin.Value, position) > .00001f)
            pan = Math.Clamp(Vector2.Dot(Vector2.Normalize(origin.Value - position), new(-MathF.Sin(heading), MathF.Cos(heading))), -1, 1);
        var sound = new SoundEvent(tone, volume, duration, pan); sounds.Add(sound); soundMixer.Add(sound);
    }
    public void Step(MazeAction action,bool teaching)=>Step(MazeControlPolicy.FromLegacy(action),teaching);
    public void Step(MazeControls controls, bool teaching)
    {
        if((controls&~MazeControlPolicy.All)!=0)throw new ArgumentOutOfRangeException(nameof(controls));
        if (Done) throw new InvalidOperationException("Reset a finished maze before stepping.");
        DiedThisFrame=false;VisualDarknessTeacherPleasure=ExplorationTeacherPleasure=0;
        sounds.Clear(); NewCellsThisFrame = NewRegionsThisFrame = 0; StationaryTeacherPain = WallCollisionPain = 0; Events = default; TeacherTarget = default; TeacherAvailable = teaching; Tick++; age += Speed;
        cooldown = MathF.Max(0, cooldown - Speed);
        if (reload > 0) { reload -= Speed; if (reload <= 0) { int transfer = Math.Min(MagazineSize - Ammo, Reserve); Ammo += transfer; Reserve -= transfer; Sound(280, .5f); } }
        float turn = (((controls&MazeControls.TurnRight)!=0?1:0)-((controls&MazeControls.TurnLeft)!=0?1:0))*.085f;
        heading = (heading + turn * Speed) % (2 * MathF.PI);
        Vector2 forward = new(MathF.Cos(heading), MathF.Sin(heading)), side = new(-forward.Y, forward.X), delta = Vector2.Zero;
        if ((controls&MazeControls.Forward)!=0) delta += forward;
        if ((controls&MazeControls.Backward)!=0) delta -= forward;
        if ((controls&MazeControls.StrafeLeft)!=0) delta -= side;
        if ((controls&MazeControls.StrafeRight)!=0) delta += side;
        if(delta.LengthSquared()>1)delta=Vector2.Normalize(delta);
        var before = position;
        Vector2 requested = delta * .10f * Speed;
        position = Move(position, requested);
        Vector2 actual = position - before;
        // The collision solver accepts/rejects each axis, allowing sliding along a wall.
        // Ignore float-rounding differences when an axis moved freely.
        Vector2 blocked = new(MathF.Abs(actual.X-requested.X)>.00001f?requested.X:0,
                              MathF.Abs(actual.Y-requested.Y)>.00001f?requested.Y:0);
        if(requested.LengthSquared()>0)
            WallCollisionPain=WallPainRate*Speed*Math.Clamp(blocked.Length()/requested.Length(),0,1);
        if(Vector2.DistanceSquared(before,position)>.000001f) { observerTrail.Enqueue(position);if(observerTrail.Count>240)observerTrail.Dequeue(); }
        footstep += Vector2.Distance(before, position);
        if (footstep >= .55f) { footstep -= .55f; Sound(110, .35f); }
        if ((controls&MazeControls.Reload)!=0 && reload <= 0 && Ammo < MagazineSize && Reserve > 0) { reload = 40; Sound(260, .4f); }
        if ((controls&MazeControls.Fire)!=0 && Ammo > 0 && reload <= 0 && cooldown <= 0)
        { Ammo--; cooldown = 12; Shoot(position, heading, false, .018f); Sound(650, .9f); }
        int hits = 0, kills = 0, pickups = 0, misses = 0, visibleHits = 0, unseenMisses = 0; float damage = 0, healed = 0;
        char tile = Tile(position); var (cx, cy) = Cell(position);
        if (tile == 'H' && Health < 100) { healed = MathF.Min(40, 100 - Health); Health += healed; map[cy][cx] = '.'; Sound(350, .7f); }
        if (tile == 'A' && Reserve < 96) { Reserve = Math.Min(96, Reserve + 16); pickups++; map[cy][cx] = '.'; Sound(450, .6f); }
        foreach (var e in enemies)
        {
            if (e.Health <= 0) continue;
            e.Cooldown -= Speed; e.Walk += Speed;
            float distance = Vector2.Distance(e.Position, position);
            if (distance < 8 && Visible(e.Position, position))
            {
                Vector2 direction = Vector2.Normalize(position - e.Position + new Vector2(.00001f));
                var strafe = new Vector2(-direction.Y, direction.X) * MathF.Sin(e.Walk * .035f);
                e.Position = Move(e.Position, (strafe + (distance > 4 ? direction * .5f : Vector2.Zero)) * .045f * Speed);
                if (e.Cooldown <= 0) { Shoot(e.Position, MathF.Atan2(direction.Y, direction.X), true, .12f); e.Cooldown = random.Next(40, 85); Sound(560, .65f / (1 + distance * .2f), e.Position); }
                if ((int)(e.Walk / 22) != (int)((e.Walk - Speed) / 22)) Sound(90, .3f / (1 + distance * .3f), e.Position);
            }
        }
        // Swept substeps prevent fast projectiles tunnelling through walls or actors.
        for (int i = projectiles.Count - 1; i >= 0; i--)
        {
            var p = projectiles[i]; bool remove = false; int steps = Math.Max(1, (int)MathF.Ceiling(p.Velocity.Length() * Speed / .07f));
            for (int s = 0; s < steps && !remove; s++)
            {
                p.Position += p.Velocity * (Speed / steps); p.Life -= Speed / steps;
                if (Tile(p.Position) == '#' || p.Life <= 0)
                {
                    if (!p.Enemy)
                    {
                        misses++;if(p.VisibleTargets.Count==0)unseenMisses++;
                        // An audible wall/range impact is perceptual evidence, not a primitive reward.
                        Sound(190, .45f / (1 + Vector2.Distance(p.Position, position) * .15f), p.Position);
                    }
                    remove = true; continue;
                }
                if (p.Enemy && Vector2.Distance(p.Position, position) < .24f) { damage += 15; Health = MathF.Max(0, Health - 15); remove = true; Sound(140, .95f, p.Origin); }
                if (!p.Enemy) foreach (var e in enemies)
                    if (e.Health > 0 && Vector2.Distance(p.Position, e.Position) < .28f)
                    { hits++;if(p.VisibleTargets.Contains(e))visibleHits++; e.Health--; if (e.Health == 0) { kills++; Score++; Sound(800, .8f, e.Position); } else Sound(720, .6f, e.Position); remove = true; break; }
            }
            if (remove) projectiles.RemoveAt(i);
        }
        // Passive survival cost is separate from projectile damage: no hit sound or damage pulse.
        // It affects both modes through visible health and the existing low-health discomfort.
        Health = MathF.Max(0, Health - HealthDrainPerSecond * Speed / 60f);
        DiedThisFrame = Health <= 0;
        // Reaching the exit is a one-time event; the session chooses when to advance the world.
        bool completedThisFrame = !Completed && tile == 'E' && Health > 0;
        Completed |= completedThisFrame;
        Events = new(hits, kills, pickups, healed, damage, completedThisFrame, misses, visibleHits, unseenMisses);
        UpdateTactile(blocked,damage);
        Render();
        // Darkness alone is not progress: do not reward staring at a dark wall.
        NewCellsThisFrame = explored.Add(Cell(position)) ? 1 : 0;
        NewRegionsThisFrame = exploredRegions.Add((cx/5,cy/5)) ? 1 : 0;
        if (teaching)
        {
            // Only actual displacement resets local inactivity. Speed scales elapsed
            // simulation time, not pain amplitude: prolonged inactivity reaches full pain.
            if (Vector2.Distance(position, stationaryAnchor) >= StationaryRadius) { stationaryAnchor = position; stationary = 0; }
            else stationary += Speed;
            StationaryTeacherPain = StationaryPainRate * Math.Clamp((stationary - StationaryGrace) / StationaryRamp, 0, 1);
            ExplorationTeacherPleasure=Math.Min(32-ExplorationBalance,NewCellsThisFrame*ExplorationReward+NewRegionsThisFrame*NewRegionReward);
            ExplorationBalance+=ExplorationTeacherPleasure;
            TeacherTarget = new(Math.Clamp(hits * HitReward + kills * KillReward + pickups * .25f + (completedThisFrame ? 128 : 0) + ExplorationTeacherPleasure, 0, MaximumPleasure),
                Math.Clamp(StationaryTeacherPain + misses * MissedShotPenalty, 0, 1));
        }
        SynthesizeAudio();
    }
    private bool EnemyInCamera(Enemy target)
    {
        if(target.Health<=0)return false;
        for(int ray=0;ray<VisionRayCount;ray++)
        {
            float angle=heading+((ray+.5f)/VisionRayCount-.5f)*VisionFieldOfView;
            var direction=new Vector2(MathF.Cos(angle),MathF.Sin(angle));
            float hit=RayCircle(direction,target.Position,.28f);
            if(hit<VisionRange&&hit<=CastVisionRay(direction).Distance+.0001f)return true;
        }
        return false;
    }
    private void Shoot(Vector2 origin, float angle, bool enemy, float spread)
    { angle += ((float)random.NextDouble() * 2 - 1) * spread; float speed = enemy ? .60f + (float)random.NextDouble() * .25f : .95f; var projectile=new Projectile(origin,new Vector2(MathF.Cos(angle),MathF.Sin(angle))*speed,enemy);
        if(!enemy)foreach(var target in enemies)if(EnemyInCamera(target))projectile.VisibleTargets.Add(target);
        projectiles.Add(projectile); }
    private void Render()
    {
        Array.Clear(Pixels); Array.Clear(RgbPixels);
        RenderFirstPersonVision();
        // HUD light is screen-space and is not attenuated by world distance.
        for(int x=0;x<HudBarPixels;x++)
        {
            if(x<Health/100*HudBarPixels)SetVisionPixel(VisionRayCount+x,.25f,.88f,.60f);else SetVisionPixel(VisionRayCount+x,.12f,.18f,.23f);
            if(x<Ammo*2)SetVisionPixel(VisionRayCount+HudBarPixels+x,1,.81f,.35f);
            else if(x<16)SetVisionPixel(VisionRayCount+HudBarPixels+x,.08f,.09f,.12f);
            else if(x-16<Reserve/12)SetVisionPixel(VisionRayCount+HudBarPixels+x,.64f,.65f,.87f);
            else SetVisionPixel(VisionRayCount+HudBarPixels+x,.08f,.09f,.12f);
        }
        if(Reloading)SetVisionPixel(VisionRayCount+HudBarPixels+23,1,.65f,.15f);
        VisualDarknessScore=VisualDarkness.Measure(RgbPixels);
        for (int y = 0; y < MapHeight; y++) for (int x = 0; x < MapWidth; x++) MapPixels[y * MapWidth + x] = map[y][x] switch { '#' => .35f, 'H' => .75f, 'A' => .6f, 'E' => 1, _ => .04f };
        foreach (var e in enemies) if (e.Health > 0) MapPixels[(int)e.Position.Y * MapWidth + (int)e.Position.X] = .8f;
        MapPixels[(int)position.Y * MapWidth + (int)position.X] = 1;
    }
    private void SetVisionPixel(int pixel,float r,float g,float b)
    {
        int index=3*pixel; RgbPixels[index]=r; RgbPixels[index+1]=g; RgbPixels[index+2]=b;
        Pixels[pixel]=.2126f*r+.7152f*g+.0722f*b;
    }
    private void SynthesizeAudio() => soundMixer.RenderStereo(Audio, AudioRight);
    private sealed class Enemy(Vector2 position, int health, float cooldown)
    { public Vector2 Position = position; public int Health = health; public float Cooldown = cooldown, Walk; }
    private sealed class Projectile(Vector2 position, Vector2 velocity, bool enemy)
    { public Vector2 Position = position; public readonly Vector2 Origin = position; public readonly Vector2 Velocity = velocity; public readonly bool Enemy = enemy; public float Life = 90; public readonly HashSet<Enemy> VisibleTargets=[]; }
}
