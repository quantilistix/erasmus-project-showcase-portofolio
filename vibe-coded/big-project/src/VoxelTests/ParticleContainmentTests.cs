using GlmSharp;
using VoxelGame;

namespace VoxelTests;

internal static partial class WorldTests
{
    private static dvec3[] EffectCenters(World world, dvec3 center)
    {
        Camera camera = new(center - 40 * dvec3.UnitY, dvec3.UnitY, Math.PI / 2, 1);
        var vertices = world.Particles.BuildVertices(camera, new int[Blocks.Count, 6], 100, dustFirstLayer: 100, explosionFirstLayer: 200);
        List<dvec3> points = [];
        for (int i = 0; i < vertices.Length; i += 6)
            points.Add(((dvec3)vertices[i].Position + (dvec3)vertices[i + 2].Position) / 2);
        return [.. points];
    }

    private static bool ParticleContainment()
    {
        bool ok = true;
        foreach (bool rotated in new[] { false, true })
        {
            using World world = CreateWorld();
            world.Physics.Settings.Gravity = dvec3.Zero;
            ivec3 tnt = new(0, 0, 5);
            List<(ivec3, BlockType)> shell = [];
            for (int z = -1; z <= 1; z++)
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                shell.Add((tnt + new ivec3(x, y, z), x == 0 && y == 0 && z == 0 ? BlockType.Tnt : BlockType.Bedrock));
            VoxelBody body = world.Map;
            if (rotated)
                body = world.Spawn(shell, new dvec3(4, 2, 1), dquat.FromAxisAngle(.71, new dvec3(1, 2, 3).Normalized));
            else
                foreach (var (cell, type) in shell) world.SetBlock(body, cell, type);
            dvec3 center = body.ToWorld(VoxelBody.CellCenter(tnt));
            world.Detonate(body, tnt);
            bool Inside(dvec3 point)
            {
                dvec3 local = body.ToLocal(point) - VoxelBody.CellCenter(tnt);
                return Math.Abs(local.x) < .5 && Math.Abs(local.y) < .5 && Math.Abs(local.z) < .5;
            }
            var initial = EffectCenters(world, center);
            ok &= Check($"{(rotated ? "rotated" : "map")} chamber: all effects spawn in clear air", initial.Length == 736 && initial.All(Inside) ? 1 : 0, 1, 1);
            AdvanceBlast(world, .2, moveParticles: true);
            var after = EffectCenters(world, center);
            ok &= Check($"{(rotated ? "rotated" : "map")} chamber: pressure cannot push effects through walls", after.Length == 736 && after.All(Inside) ? 1 : 0, 1, 1);
        }
        using (World world = CreateWorld())
        {
            world.Physics.Settings.Gravity = dvec3.Zero;
            dvec3 center = new(.5, .5, 5.5);
            world.Particles.EmitExplosion(center, world);
            for (int z = 4; z <= 6; z++)
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
                world.SetBlock(world.Map, new ivec3(x, y, z), BlockType.Bedrock);
            world.Particles.Update(world, .01);
            ok &= Check("stationary particles engulfed by geometry cannot leak back out", world.Particles.Count, 0, 0);
        }
        return ok;
    }

    private static bool ParticleCannon()
    {
        bool ok = true;
        foreach (bool slabs in new[] { false, true })
        {
            using World world = CreateWorld();
            world.Physics.Settings.Gravity = dvec3.Zero;
            ivec3 tnt = new(0, 0, 5);
            for (int x = -1; x < 4; x++)
            for (int y = -1; y <= 1; y++)
            for (int z = -1; z <= 1; z++)
            {
                if (x != -1 && y == 0 && z == 0) continue;
                BlockOrientation orientation = x == -1 ? BlockOrientation.SlabEast
                    : y == -1 ? BlockOrientation.SlabNorth : y == 1 ? BlockOrientation.SlabSouth
                    : z == -1 ? BlockOrientation.SlabTop : BlockOrientation.SlabBottom;
                world.SetBlock(world.Map, tnt + new ivec3(x, y, z), slabs ? BlockType.BedrockSlab : BlockType.Bedrock, orientation);
            }
            world.SetBlock(world.Map, tnt, BlockType.Tnt);
            world.Detonate(world.Map, tnt);
            bool contained = true;
            int exited = 0;
            for (int step = 0; step < 30; step++)
            {
                AdvanceBlast(world, World.Step, moveParticles: true);
                foreach (dvec3 point in EffectCenters(world, VoxelBody.CellCenter(tnt)))
                {
                    if (point.x >= 4) exited++;
                    else contained &= point.x > 0 && Math.Abs(point.y - .5) < .5 && Math.Abs(point.z - 5.5) < .5;
                }
            }
            string label = slabs ? "slab cannon" : "cannon";
            ok &= Check($"{label} keeps dust and puffs inside its bore until the muzzle", contained ? 1 : 0, 1, 1)
                & Check($"{label} still ejects particles through its open muzzle", exited, 1, int.MaxValue);
        }
        return ok;
    }
}
