using GlmSharp;
using VoxelGame;

namespace VoxelTests;

internal static class WaterRenderingTests
{
    public static bool Run(GraphicsContext graphics, Renderer renderer, World world, string captures)
    {
        Camera camera = new(new dvec3(.5, -3, 1.5), dvec3.UnitY, Math.PI / 3, 640.0 / 480);
        renderer.Video.DynamicShadows = false;
        renderer.Video.BlockLighting = false;
        renderer.Video.Sun = false;
        renderer.Video.SpecularLighting = false;
        renderer.Video.AmbientOcclusion = false;
        int sequence = 0;
        byte[] Capture(BlockType front, BlockType back, bool merged, bool indirect, int depth = 1, bool enclosed = false)
        {
            for (int y = 0; y <= 20; y++)
                world.Map.Grid[new ivec3(0, y, 1)] = y < depth ? new Voxel(front, 0)
                    : y == depth ? new Voxel(back, 0, Lit: back == BlockType.Furnace) : default;
            if (enclosed)
            {
                for (int y = 0; y <= depth; y++)
                foreach (ivec3 side in new[] { new ivec3(-1, y, 1), new ivec3(1, y, 1), new ivec3(0, y, 0), new ivec3(0, y, 2) })
                    world.Map.Grid[side] = new Voxel(BlockType.Stone, 0);
                world.Map.Grid[new ivec3(0, -1, 1)] = new Voxel(BlockType.Stone, 0);
            }
            world.Map.MeshDirty.UnionWith(world.Map.Grid.Chunks.Select(c => c.Coordinate));
            VoxelMesher.MergeRenderFaces = merged;
            renderer.IndirectDraws = indirect;
            renderer.Sync(world, camera.Position);
            renderer.WaitForLighting(world.Bodies, b => (b.Position, b.Orientation));
            if (!graphics.BeginFrame(out Frame frame)) throw new InvalidOperationException("Water test frame unavailable");
            string path = Path.Combine(captures, $"water-{sequence++}.ppm");
            graphics.CapturePath = path;
            renderer.Draw(frame, camera, world.Bodies, b => (b.Position, b.Orientation), world.Particles,
                [], null, [], [], false);
            graphics.EndFrame(frame);
            byte[] ppm = File.ReadAllBytes(path);
            int offset = 0;
            for (int i = 0; i < 3; i++) offset = Array.IndexOf(ppm, (byte)'\n', offset) + 1;
            return ppm[offset..];
        }

        byte[] background = Capture(BlockType.Air, BlockType.Stone, true, true);
        byte[] water = Capture(BlockType.Water, BlockType.Stone, true, true);
        byte[] differentBackground = Capture(BlockType.Water, BlockType.OakPlanks, true, true);
        byte[] unitFaces = Capture(BlockType.Water, BlockType.Stone, false, true);
        byte[] direct = Capture(BlockType.Water, BlockType.Stone, true, false);
        int visible = 0, transmitted = 0;
        // The central face stays clear of silhouettes and texture boundaries.
        for (int y = 210; y < 270; y++)
        for (int x = 290; x < 350; x++)
        {
            int pixel = (x + 640 * y) * 3;
            if (Enumerable.Range(0, 3).Any(c => Math.Abs(water[pixel+c] - background[pixel+c]) > 8)) visible++;
            if (Enumerable.Range(0, 3).Any(c => Math.Abs(water[pixel+c] - differentBackground[pixel+c]) > 3)) transmitted++;
        }
        bool passed = visible > 1000 && transmitted > 1000
            && water.AsSpan().SequenceEqual(unitFaces) && water.AsSpan().SequenceEqual(direct);
        Console.WriteLine($"   water: {visible}/3600 visible pixels, {transmitted}/3600 transmit the background; merged/unit and indirect/direct identical: {passed}");

        double Difference(byte[] a, byte[] b)
        {
            double sum = 0;
            for (int y = 230; y < 250; y++)
            for (int x = 310; x < 330; x++)
            for (int c = 0; c < 3; c++)
                sum += Math.Abs(a[(x + 640 * y) * 3 + c] - b[(x + 640 * y) * 3 + c]);
            return sum / 1200;
        }
        byte[] deepStone = Capture(BlockType.Water, BlockType.Stone, true, true, 4);
        byte[] deepWood = Capture(BlockType.Water, BlockType.OakPlanks, true, true, 4);
        double shallowTransmission = Difference(water, differentBackground);
        double deepTransmission = Difference(deepStone, deepWood);
        bool depthPass = shallowTransmission > 3 && deepTransmission < shallowTransmission * .65;
        Console.WriteLine($"   background transmission decreases with depth: {shallowTransmission:F3} -> {deepTransmission:F3}; {depthPass}");
        passed &= depthPass;

        byte[] acrossChunk = Capture(BlockType.Water, BlockType.Stone, true, true, 18);
        byte[] acrossChunkUnit = Capture(BlockType.Water, BlockType.Stone, false, false, 18);
        bool seamPass = acrossChunk.AsSpan().SequenceEqual(acrossChunkUnit);
        Console.WriteLine($"   raymarch across chunk boundary: identical merged/indirect and unit/direct pixels: {seamPass}");
        passed &= seamPass;

        renderer.Video.AmbientBrightness = 0;
        byte[] unlit = Capture(BlockType.Water, BlockType.Stone, true, true, 4);
        renderer.Video.AmbientBrightness = 70;
        byte[] lit = Capture(BlockType.Water, BlockType.Stone, true, true, 4);
        bool lightingPass = Difference(unlit, lit) > 8;
        Console.WriteLine($"   water responds to world ambient lighting: {lightingPass}");
        passed &= lightingPass;

        camera = new Camera(new dvec3(.5, 15.25, 1.5), dvec3.UnitY, Math.PI / 3, 640.0 / 480);
        byte[] underwaterBackground = Capture(BlockType.Air, BlockType.Stone, true, true, 18);
        byte[] underwater = Capture(BlockType.Water, BlockType.Stone, true, true, 18);
        byte[] underwaterUnit = Capture(BlockType.Water, BlockType.Stone, false, false, 18);
        double underwaterMeshError = underwater.Zip(underwaterUnit, (a, b) => Math.Abs(a - b)).Average();
        bool underwaterPass = Difference(underwaterBackground, underwater) > 8
            && underwaterMeshError < .01;
        Console.WriteLine($"   underwater rays cross chunks and tint solid receivers: {underwaterPass} (mean mesh difference {underwaterMeshError:F6})");
        passed &= underwaterPass;

        // No water-air faces exist in this fixture. The volume must still render,
        // without adding coplanar water geometry to the surrounding solid receivers.
        renderer.Video.BlockLighting = true;
        byte[] enclosedAir = Capture(BlockType.Air, BlockType.Furnace, true, true, 18, enclosed: true);
        byte[] enclosedWater = Capture(BlockType.Water, BlockType.Furnace, true, true, 18, enclosed: true);
        int waterQuads = 0;
        uint waterLayer = (uint)renderer.FaceLayers[(int)BlockType.Water, 0];
        foreach (var chunk in world.Map.Grid.Chunks)
        {
            uint[] mesh = VoxelMesher.BuildRenderMesh(world.Map.Grid, chunk.Coordinate, renderer.FaceLayers);
            for (int q = 1; q < mesh.Length; q += 8)
                if ((mesh[q] & 255u) == waterLayer) waterQuads++;
        }
        bool enclosedPass = waterQuads == 0 && Difference(enclosedAir, enclosedWater) > 8;
        Console.WriteLine($"   sealed underwater volume without contact surfaces: {enclosedPass} ({waterQuads} water quads)");
        passed &= enclosedPass;
        if (passed) Directory.Delete(captures, true);
        else Console.WriteLine($"   Water images: {captures}");
        return passed;
    }
}
