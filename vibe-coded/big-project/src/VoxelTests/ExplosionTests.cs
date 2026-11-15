using GlmSharp;
using VoxelGame;

namespace VoxelTests;

internal static partial class WorldTests
{
    private static void AdvanceBlast(World world, double seconds, bool moveParticles = false)
    {
        int steps = (int)Math.Ceiling(seconds / World.Step);
        for (int i = 0; i < steps; i++)
        {
            world.AdvanceExplosions(World.Step);
            if (moveParticles) world.Particles.Update(world, World.Step);
        }
    }

    private static bool TntExplosion()
    {
        using World world = CreateWorld();
        ivec3 tnt = new(0, 0, 5), weak = new(1, 0, 5), strong = new(5, 0, 5);
        world.SetBlock(world.Map, tnt - ivec3.UnitZ, BlockType.Bedrock);
        world.SetBlock(world.Map, tnt, BlockType.Tnt);
        // A dirt bridge breaks away, leaving the more distant stone intact to detach.
        for (int x = weak.x; x < strong.x; x++)
            world.SetBlock(world.Map, new ivec3(x, 0, 5), BlockType.Dirt);
        world.SetBlock(world.Map, strong, BlockType.Stone);
        var nearby = world.Spawn([(ivec3.Zero, BlockType.IronBlock)], new dvec3(3, 2, 5), dquat.Identity);
        var far = world.Spawn([(ivec3.Zero, BlockType.IronBlock)], new dvec3(17, 0, 5), dquat.Identity);
        var player = world.SpawnPlayer(new dvec3(8, 0, 5));
        var item = world.DropItem(new ItemStack(Items.Of(BlockType.Stone), 1), new dvec3(2, -2, 5), dvec3.Zero);
        bool fired = world.Detonate(world.Map, tnt);
        AdvanceBlast(world, .7);
        var detached = world.Objects.FirstOrDefault(o => o != nearby && o != far);
        return Check("TNT consumed immediately", fired && world.Map.Grid[tnt].Type == BlockType.Air ? 1 : 0, 1, 1)
            & Check("weak dirt broke", world.Map.Grid[weak].Type == BlockType.Air ? 1 : 0, 1, 1)
            & Check("strong stone detached and accelerated", detached != null && detached.Grid.Occupied().Any(c => c.Value.Type == BlockType.Stone)
                && detached.LinearVelocity.x > 0 ? 1 : 0, 1, 1)
            & Check("nearby object pushed outward", nearby.LinearVelocity.x, .001, 42)
            & Check("distant object unchanged", far.LinearVelocity.Length, 0, 0)
            & Check("player pushed outward", player.LinearVelocity.x, .1, 42)
            & Check("player takes blast damage", player.Health, .01, .999)
            & Check("dropped item pushed outward", item.LinearVelocity.x, .1, 42)
            & Check("dust and blast particles emitted", world.Particles.Count, 736, BlockParticles.MaxParticles)
            & Check("TNT cannot fire twice", world.Detonate(world.Map, tnt) ? 1 : 0, 0, 0);
    }

    private static bool TntChain()
    {
        using World world = CreateWorld();
        ivec3 first = new(0, 0, 5), second = new(15, 0, 5), third = new(30, 0, 5);
        foreach (ivec3 cell in new[] { first, second, third }) world.SetBlock(world.Map, cell, BlockType.Tnt);
        world.Detonate(world.Map, first);
        AdvanceBlast(world, 1.5);
        return Check("chain reaches TNT outside first blast", world.Bodies.Sum(b => b.Grid.Occupied().Count(c => c.Value.Type == BlockType.Tnt)), 0, 0)
            & Check("chain bursts respect particle limit", world.Particles.Count, 3 * 736, 3 * 736);
    }

    private static bool TntPunch()
    {
        using World world = CreateWorld();
        var player = world.SpawnPlayer(new dvec3(0, 0, 4));
        var tnt = world.Spawn([(ivec3.Zero, BlockType.Tnt)], new dvec3(3, 0, 5), dquat.Identity);
        bool mined = world.Mine(new VoxelHit(tnt, ivec3.Zero, -ivec3.UnitX, 3), player.Eye, dvec3.UnitX, Tool.Hand);
        return Check("punch consumes TNT without a mined drop", !mined && tnt.Grid.Count == 0 ? 1 : 0, 1, 1)
            & Check("punch emits an explosion", world.Particles.Count, 736, 736);
    }

    private static bool TntParticles()
    {
        using World world = CreateWorld();
        world.Physics.Settings.Gravity = new dvec3(0, 0, -9.81);
        world.Particles.EmitExplosion(dvec3.Zero);
        Camera camera = new(new dvec3(0, -40, 0), dvec3.UnitY, Math.PI / 2, 1);
        int[,] layers = new int[Blocks.Count, 6];
        static dvec3[] Centers(ReadOnlySpan<ParticleVertex> vertices, int effect)
        {
            List<dvec3> result = [];
            for (int i = 0; i < vertices.Length; i += 6)
                if (effect == 2 ? vertices[i].UvLayer.z < 0 : vertices[i].UvLayer.z >= 100 && vertices[i].UvLayer.z < 105)
                    result.Add(((dvec3)vertices[i].Position + (dvec3)vertices[i + 2].Position) / 2);
            return [.. result];
        }
        var before = world.Particles.BuildVertices(camera, layers, 100, dustFirstLayer: 100, explosionFirstLayer: 200);
        bool pixelScale = true;
        for (int i = 0; i < before.Length; i += 6)
            if (before[i].UvLayer.z < 0 || before[i].UvLayer.z >= 100 && before[i].UvLayer.z < 105)
            {
                double edge = ((dvec3)before[i + 1].Position - (dvec3)before[i].Position).Length;
                pixelScale &= Math.Abs(edge / BlockParticles.TexturePixels - BlockParticles.ChipSize / BlockParticles.ChipPixels) < 1e-6;
            }
        var puffs = Centers(before, 2);
        var dust = Centers(before, 1);
        world.Physics.Settings.Gravity = dvec3.Zero;
        world.Particles.Update(world, .01);
        var resting = Centers(world.Particles.BuildVertices(camera, layers, 100, dustFirstLayer: 100, explosionFirstLayer: 200), 1);
        bool startsAtRest = resting.All(p => dust.Any(q => (p - q).Length < 1e-6));
        world.Physics.Settings.Gravity = new dvec3(0, 0, -9.81);
        world.Pressure.Emit(dvec3.Zero);
        world.Pressure.Emit(new dvec3(-2, 0, 0));
        AdvanceBlast(world, .1, moveParticles: true);
        var after = world.Particles.BuildVertices(camera, layers, 100, dustFirstLayer: 100, explosionFirstLayer: 200);
        var movedPuffs = Centers(after, 2);
        var movedDust = Centers(after, 1);
        bool opaque = true;
        for (int i = 0; i < after.Length; i++) opaque &= after[i].Opacity == 1;
        bool animated = false;
        for (int i = 0; i < after.Length; i += 6)
            if (after[i].UvLayer.z <= -202 && after[i].UvLayer.z >= -207) animated = true;
        bool ok = Check("dust starts at rest before any pressure", startsAtRest ? 1 : 0, 1, 1)
            & Check("particles remain opaque through their lifetime", opaque ? 1 : 0, 1, 1)
            & Check("explosion advances through sprite frames", animated ? 1 : 0, 1, 1)
            & Check("16 blast puffs", puffs.Length, 16, 16)
            & Check("dust and explosion pixels match block chips", pixelScale ? 1 : 0, 1, 1)
            & Check("720 outward dust particles", dust.Length, 720, 720)
            & Check("blast puffs also receive air physics and blast velocity", movedPuffs.Average(p => p.x) - puffs.Average(p => p.x), .001, 8)
            & Check("dust receives blast velocity", movedDust.Average(p => p.x) - dust.Average(p => p.x), .01, 8);
        AdvanceBlast(world, .8, moveParticles: true);
        ok &= Check("all dust survives until one second", world.Particles.Count, 720, 720);
        var floating = Centers(world.Particles.BuildVertices(camera, layers, 100, dustFirstLayer: 100, explosionFirstLayer: 200), 1);
        ok &= Check("emitted dust floats upward after slowing down", floating.Average(p => p.z) - dust.Average(p => p.z), .1, 2);
        world.Particles.Update(world, 2.1);
        ok &= Check("random dust lifetimes leave some particles after three seconds", world.Particles.Count, 100, 620);
        world.Particles.Update(world, 2);
        return ok & Check("all dust expires by five seconds", world.Particles.Count, 0, 0);
    }

    private static bool TntPlayerDamage()
    {
        using World world = CreateWorld();
        var player = world.SpawnPlayer(dvec3.Zero);
        dvec3 center = new(.5, .5, 5.5);
        double DamageAt(double distance, bool invulnerable = false)
        {
            player.Health = 1;
            player.Invulnerable = invulnerable;
            player.MoveTo(dvec3.Zero);
            player.Body.Position += center + new dvec3(distance, 0, 0) - player.Body.WorldCenterOfMass;
            world.SetBlock(world.Map, new ivec3(0, 0, 5), BlockType.Tnt);
            world.Detonate(world.Map, new ivec3(0, 0, 5));
            AdvanceBlast(world, .5);
            return player.Health;
        }
        double near = DamageAt(3), far = DamageAt(6);
        bool ok = Check("near blast hurts more than distant blast", near < far && far < 1 ? 1 : 0, 1, 1)
            & Check("outside blast radius is unharmed", DamageAt(17), 1, 1)
            & Check("invulnerability prevents blast damage", DamageAt(3, true), 1, 1)
            & Check("invulnerable player still receives knockback", player.LinearVelocity.x, .1, 42)
            & Check("point-blank blast can kill", DamageAt(0), 0, 0);
        return ok;
    }

    private static bool TntFlashes()
    {
        BlockParticles particles = new() { Enabled = false };
        particles.EmitExplosion(new dvec3(1, 2, 3));
        Span<vec4> lights = stackalloc vec4[4];
        bool ok = Check("particle setting suppresses visuals", particles.Count, 0, 0);
        for (int frame = 0; frame < 2; frame++)
        {
            particles.ReadFlashes(lights);
            double expected = frame == 0 ? 1 : .25;
            ok &= Check($"fast flash frame {frame + 1}", lights[0].w, expected - 1e-6, expected + 1e-6);
        }
        particles.ReadFlashes(lights);
        ok &= Check("flash fully removed", lights[0].w, 0, 0);
        for (int i = 0; i < 20; i++) particles.EmitExplosion(new dvec3(i, 0, 0));
        particles.ReadFlashes(lights);
        ok &= Check("chain retains four most recent flashes", lights[0].x, 16, 16);
        return ok & Check("nominal blast radius is 16 blocks", World.ExplosionRadius / VoxelBody.CellSize, 16, 16);
    }
}
