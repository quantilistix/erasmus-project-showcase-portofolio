using GlmSharp;
using MYT.Kit.Structural;
using VoxelGame;

namespace VoxelTests;

internal static partial class WorldTests
{
    private static bool GpuDamageLaws()
    {
        using GpuBlastSimulation simulation = CreateGpuPressure();
        List<GpuDamageRequest> requests = [];
        List<(double Health, ushort Damage)> expected = [];
        Random random = new(6617);
        for (int i = 0; i < 400; i++)
        {
            Voxel voxel = new((BlockType)random.Next(1, Blocks.Count), (ushort)random.Next(65535));
            BlockMaterial material = voxel.Material;
            double amount = random.NextDouble() * 100;
            requests.Add(GpuDamageRequest.Raw(material, voxel.Health, amount));
            double health = material.Unbreakable ? voxel.Health : material.HealthAfter(voxel.Health, amount);
            expected.Add((health, Voxel.ToDamage(1 - health)));
            Tool tool = new(random.NextDouble() * 1e8, .5 + random.NextDouble() * 25);
            CellStress stress = new(random.NextDouble() * 1e6, random.NextDouble() * 1e7, random.NextDouble() * 1e6);
            double seconds = .01 + random.NextDouble() * .1;
            requests.Add(GpuDamageRequest.Tool(voxel, tool, seconds, stress));
            amount = tool.Damage(material, voxel.Health, seconds, stress);
            health = amount > 0 ? material.HealthAfter(voxel.Health, amount) : voxel.Health;
            expected.Add((health, Voxel.ToDamage(1 - health)));
            requests.Add(GpuDamageRequest.Sustained(voxel, stress, seconds, 100));
            double ratio = material.ElasticLimit(voxel.Health).Ratio(stress);
            amount = !material.Unbreakable && ratio > 1 ? seconds * Math.Max(100, material.DamageToBreak(1)) * (ratio * ratio - 1) : 0;
            health = amount > 0 ? material.HealthAfter(voxel.Health, amount) : voxel.Health;
            expected.Add((health, Voxel.ToDamage(1 - health)));
        }
        var results = simulation.EvaluateDamage(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(requests));
        bool exact = results.Length == expected.Count;
        double error = 0;
        for (int i = 0; i < results.Length; i++)
        {
            exact &= (ushort)results[i].y == expected[i].Damage;
            error = Math.Max(error, Math.Abs(results[i].x - expected[i].Health));
        }
        return Check("raw, tool and sustained GPU health laws preserve ushort damage", exact ? 1 : 0, 1, 1)
            & Check("GPU/CPU health difference", error, 0, 1e-12)
            & Check("Vulkan validation errors", TestGpu.Vulkan.ValidationErrorCount, 0, 0);
    }

    private static GpuBlastSimulation CreateGpuPressure()
    {
        var gpu = TestGpu.Vulkan;
        return new(gpu.Vk, gpu.Device, gpu.PhysicalDevice, gpu.CommandPool, gpu.Queue, TestGpu.Shaders);
    }

    private static bool GpuPressureStencil()
    {
        using World world = CreateWorld();
        using GpuBlastSimulation pressure = CreateGpuPressure();
        pressure.Emit(new(-2.5, .5, .5));
        // Independent finite-volume acoustic reference. Its boundary is beyond
        // the stencil's numerical domain of dependence for these five steps.
        const int side = 33, middle = 16;
        var state = new vec4[side * side * side];
        var temporary = new vec4[state.Length];
        int Index(int x, int y, int z) => x + side * (y + side * z);
        const float subDt = (float)(World.Step / 2), courant = subDt * 64;
        float damping = MathF.Exp(-3 * subDt);
        double remaining = BlastPressure.PulseDuration;
        int[] strides = [1, side, side * side];
        dvec3[] positions = [new(-2.5, .5, .5), new(-.5, .5, .5), new(-4.5, .5, .5), new(-2.5, 2.5, .5), new(-2.5, .5, 2.5)];
        bool ok = true;
        for (int step = 0; step < 5; step++)
        {
            pressure.Advance(world, World.Step);
            for (int sub = 0; sub < 2; sub++)
            {
                double injected = Math.Min(World.Step / 2, remaining);
                state[Index(middle, middle, middle)].x += (float)(8 * injected / BlastPressure.PulseDuration);
                remaining -= injected;
                for (int z = 1; z < side - 1; z++) for (int y = 1; y < side - 1; y++) for (int x = 1; x < side - 1; x++)
                {
                    int i = Index(x, y, z);
                    vec4 value = state[i];
                    for (int axis = 0; axis < 3; axis++) value[axis + 1] -= courant * (state[i + strides[axis]].x - value.x);
                    temporary[i] = value;
                }
                for (int z = 1; z < side - 1; z++) for (int y = 1; y < side - 1; y++) for (int x = 1; x < side - 1; x++)
                {
                    int i = Index(x, y, z);
                    vec4 value = temporary[i];
                    float divergence = 0;
                    for (int axis = 0; axis < 3; axis++) divergence += value[axis + 1] - temporary[i - strides[axis]][axis + 1];
                    value.x -= courant * divergence;
                    state[i] = value * damping;
                }
            }
            var samples = pressure.Sample(positions);
            for (int i = 0; i < positions.Length; i++)
            {
                ivec3 offset = (ivec3)(positions[i] - positions[0]);
                float expected = Math.Max(0, state[Index(middle + offset.x, middle + offset.y, middle + offset.z)].x);
                ok &= Math.Abs(samples[i].Pressure - expected) < 2e-5;
            }
        }
        return Check("GPU acoustic stencil agrees with independent CPU reference", ok ? 1 : 0, 1, 1)
            & Check("Vulkan validation errors", TestGpu.Vulkan.ValidationErrorCount, 0, 0);
    }

    private static bool GpuPressureSources()
    {
        using World world = CreateWorld();
        using GpuBlastSimulation pressure = CreateGpuPressure();
        dvec3 origin = new(.5);
        pressure.Emit(origin);
        pressure.Advance(world, World.Step);
        double single = pressure.Sample([origin + dvec3.UnitX])[0].Pressure;
        int oneSourceCells = pressure.PressureCells;
        for (int i = 1; i < 27; i++) pressure.Emit(origin);
        pressure.Advance(world, World.Step);
        var loads = pressure.Sample([origin + new dvec3(2, 0, 0), origin - new dvec3(2, 0, 0), new(1000)]);
        Console.WriteLine($"   source {single:G6}, symmetric loads {loads[0].Pressure:G6}/{loads[1].Pressure:G6}, outside {loads[2].Pressure:G6}");
        bool ok = single > 0 && loads.All(l => double.IsFinite(l.Pressure) && double.IsFinite(l.Difference.Length));
        ok &= Math.Abs(loads[0].Pressure - loads[1].Pressure) < 1e-5;
        ok &= loads[2] == default;
        ok &= pressure.PressureCells < oneSourceCells * 27;
        Console.WriteLine($"   shared pressure cells: one source {oneSourceCells}, 27 sources {pressure.PressureCells}");
        int before = pressure.PressureCells;
        pressure.Emit(new(10000.5, .5, .5));
        ok &= pressure.PressureCells < before * 3;
        pressure.Advance(world, World.Step);
        var distant = pressure.Sample([new(10000.5, .5, .5), new(10001.5, .5, .5)]);
        Console.WriteLine($"   distant source loads {distant[0].Pressure:G6}/{distant[1].Pressure:G6}");
        ok &= distant.Any(l => l.Pressure > 0);
        return Check("superposition, symmetry, growing tiles and distant source isolation", ok ? 1 : 0, 1, 1)
            & Check("Vulkan validation errors", TestGpu.Vulkan.ValidationErrorCount, 0, 0);
    }

    private static bool GpuPressureDamage()
    {
        using World world = CreateWorld();
        world.Particles.Enabled = false;
        world.SetBlock(world.Map, new(1, 0, 0), BlockType.Dirt);
        world.SetBlock(world.Map, new(-1, 0, 0), BlockType.Bedrock);
        using GpuBlastSimulation pressure = CreateGpuPressure();
        pressure.Emit(new(.5));
        var changes = pressure.Advance(world, World.Step).ToArray();
        bool dirt = changes.Any(c => c.Cell == new ivec3(1, 0, 0) && c.Damage > 0);
        bool bedrock = changes.Any(c => c.Cell == new ivec3(-1, 0, 0));
        bool sameLaw = true;
        foreach (var change in changes)
        {
            Voxel voxel = change.Before;
            Tool blast = new(BlastPressure.PeakStress * change.Load.Pressure, 24 * change.Load.Pressure);
            double damage = blast.Damage(voxel.Material, voxel.Health, World.Step / BlastPressure.PulseDuration);
            ushort expected = Voxel.ToDamage(1 - voxel.Material.HealthAfter(voxel.Health, damage));
            sameLaw &= Math.Abs(change.Damage - expected) <= 1;
        }
        return Check("GPU damage uses the material law and spares bedrock", dirt && !bedrock && sameLaw ? 1 : 0, 1, 1)
            & Check("Vulkan validation errors", TestGpu.Vulkan.ValidationErrorCount, 0, 0);
    }

    private static bool GpuPressureMirror()
    {
        using World world = CreateWorld();
        world.Particles.Enabled = false;
        world.SetBlock(world.Map, new(1, 0, 0), BlockType.Bedrock);
        using GpuBlastSimulation pressure = CreateGpuPressure();
        pressure.Emit(new(.5));
        pressure.Advance(world, World.Step);
        bool firstUpload = pressure.UploadedBytes > 0;
        pressure.Advance(world, World.Step);
        bool unchanged = pressure.UploadedBytes == 0;
        world.SetBlock(world.Map, new(2, 0, 0), BlockType.Dirt);
        pressure.Advance(world, World.Step);
        bool edited = pressure.UploadedBytes > 0;
        world.Map.Grid.RemoveChunk(new(0));
        pressure.Advance(world, World.Step);
        return Check("independent GPU mirror tracks edits and unloads, skips unchanged chunks", firstUpload && unchanged && edited && pressure.ResidentChunks == 0 ? 1 : 0, 1, 1)
            & Check("Vulkan validation errors", TestGpu.Vulkan.ValidationErrorCount, 0, 0);
    }

    private static bool GpuPressureWorld()
    {
        using World world = CreateWorld(gpuBlasts: true);
        world.Particles.Enabled = false;
        world.SetBlock(world.Map, new(0), BlockType.Tnt);
        world.SetBlock(world.Map, new(1, 0, 0), BlockType.Tnt);
        world.SetBlock(world.Map, new(0, 1, 0), BlockType.Dirt);
        bool fired = world.Detonate(world.Map, new(0));
        bool dirtDamaged = false;
        for (int step = 0; step < 8; step++)
        {
            world.AdvanceExplosions(World.Step);
            dirtDamaged |= world.Map.Grid[new ivec3(0, 1, 0)] is { Type: BlockType.Air } or { Damage: > 0 }
                || world.Objects.Count > 0;
        }
        return Check("GPU explosion applies mirror changes, chain ignition and CPU fragmentation", fired && world.Map.Grid[new ivec3(1, 0, 0)].Type != BlockType.Tnt && dirtDamaged ? 1 : 0, 1, 1)
            & Check("Vulkan validation errors", TestGpu.Vulkan.ValidationErrorCount, 0, 0);
    }

    private static bool GpuPressureObjects()
    {
        using World blocked = CreateWorld();
        using World open = CreateWorld();
        var blocks = from x in Enumerable.Range(-6, 13) from z in Enumerable.Range(-6, 13)
                     select (new ivec3(x, 0, z), BlockType.Bedrock);
        VoxelObject wall = blocked.Spawn(blocks, new(3, 0, 0), dquat.FromAxisAngle(Math.PI / 2, dvec3.UnitZ));
        using GpuBlastSimulation pressure = CreateGpuPressure();
        using GpuBlastSimulation reference = CreateGpuPressure();
        pressure.Emit(new(.5)); reference.Emit(new(.5));
        for (int step = 0; step < 2; step++) { pressure.Advance(blocked, World.Step); reference.Advance(open, World.Step); }
        dvec3 point = new(4.5, .5, .5);
        double behind = pressure.Sample([point])[0].Pressure;
        double unobstructed = reference.Sample([point])[0].Pressure;
        if (wall.Body != null) wall.Body.Position = new(50, 0, 0);
        else wall.PendingPosition = new(50, 0, 0);
        double released = 0;
        for (int step = 0; step < 3; step++)
        {
            pressure.Advance(blocked, World.Step);
            released = Math.Max(released, pressure.Sample([point])[0].Pressure);
        }
        Console.WriteLine($"   behind rotated wall {behind:G5}, open air {unobstructed:G5}, after moving wall {released:G5}");
        return Check("GPU voxel lookup and tile candidates track rotated and moving objects", behind < 1e-10 && unobstructed > 0 && released > 0 ? 1 : 0, 1, 1)
            & Check("Vulkan validation errors", TestGpu.Vulkan.ValidationErrorCount, 0, 0);
    }
}
