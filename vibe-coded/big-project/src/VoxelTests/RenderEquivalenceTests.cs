using GlmSharp;
using StbImageSharp;
using MYT.Kit.Structural;
using MYT.Kit.Vulkan;
using Silk.NET.GLFW;
using VoxelGame;

namespace VoxelTests;

/// <summary>Explicit windowed check: compares rendered pixels against unit faces, including
/// changing damage without remeshing. Run with -- render-equivalence on a display/GPU.</summary>
internal static unsafe class RenderEquivalenceTests
{
    public static bool Run(bool computeOnly = false,bool shadowOnly = false,bool lightingBenchmark = false,bool roomOnly = false,bool skyOnly = false,bool waterOnly = false,bool lodOnly = false, bool particlesOnly = false, bool streamingOnly = false, bool asyncOnly = false, bool budgetOnly = false, bool gpuMeshOnly = false)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "assets"))) root = root.Parent;
        string assets = Path.Combine(root?.FullName ?? throw new InvalidOperationException("Repository assets missing"), "assets");
        Glfw glfw = Glfw.GetApi();
        if (!glfw.Init()) throw new InvalidOperationException("A display is required for render-equivalence");
        glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
        WindowHandle* window = glfw.CreateWindow(640, 480, "MYT render equivalence", null, null);
        if (window == null) throw new InvalidOperationException("Could not create render test window");
        bool savedMerge = VoxelMesher.MergeRenderFaces;
        string captures = Path.Combine(Path.GetTempPath(), "myt-render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(captures);
        try
        {
            using GraphicsContext graphics = new(glfw, window, validation: !lightingBenchmark);
            ItemSprites sprites = new(Path.Combine(assets, "images"));
            InterfaceArt art = new(Path.Combine(assets, "images"), sprites);
            using Renderer renderer = new(graphics, assets, art.Atlas, sprites) { SynchronousMeshing = true };
            var (queue, pool) = graphics.PhysicsQueue;
            using World world = new(graphics.Vk, graphics.Device, graphics.PhysicalDevice, pool, queue,
                Path.Combine(assets, "shaders"), graphics.StressQueue);
            if (gpuMeshOnly) return GpuMeshingTests.Run(graphics, renderer, world, captures);
            if (asyncOnly) return AsyncMeshingTests.Run(graphics, renderer, world, captures);
            if (budgetOnly) return BudgetLightingTests.Run(graphics, Path.Combine(assets,"shaders"), world);
            if (streamingOnly) return StreamingMeshingTests.Run(renderer, world);
            if (particlesOnly) return ParticleRenderTests.Run(graphics, renderer, world, captures);
            if (lodOnly) return WorldLodRenderTests.Run(graphics, renderer, world, captures);
            if (waterOnly) return WaterRenderingTests.Run(graphics, renderer, world, captures);
            if (computeOnly) return ComputeLightingTests.Run(graphics,Path.Combine(assets,"shaders"),world);
            if (lightingBenchmark) return ComputeLightingTests.Benchmark(graphics,Path.Combine(assets,"shaders"),world);
            if (shadowOnly) return ShadowTests.Run(graphics,renderer,world,captures);
            if (roomOnly) return EnclosedRoomTests.Run(graphics,renderer,world,captures);
            if (skyOnly) return DaylightTests.Sky(graphics,renderer,world,captures);
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                world.Map.Grid[new ivec3(x, y, 0)] = new Voxel(x > 11 && y > 11 ? BlockType.Glass : BlockType.Stone, 0);
            VoxelObject body = world.Spawn(
                TerrainGenerator.Box(new ivec3(10, 8, 2), BlockType.OakPlanks).Select(b => (b.Cell - new ivec3(3, 2, 1), b.Type)),
                new dvec3(8, 8, 4), dquat.FromAxisAngle(0.4, dvec3.UnitZ) * dquat.FromAxisAngle(0.3, dvec3.UnitY));
            world.ApplyTopologyChanges();
            foreach (VoxelBody b in world.Bodies)
            {
                ivec3[] cells = [.. b.Grid.Occupied().Select(c => c.Cell)];
                CellStress[] stress = [.. cells.Select(c => new CellStress(
                    b.Grid[c].Material.ElasticLimit(1).Tension * (0.1 + (c.x & 7) / 7.0), 0, 0))];
                b.SetStress(new StressSnapshot(b.TopologyVersion, cells, stress, true));
            }
            Camera camera = new(new dvec3(8, -5, 13), new dvec3(0, 13, -10).Normalized, 70 * Math.PI / 180, 640.0 / 480);
            int sequence = 0;
            byte[] Capture(bool merge, bool rebuild, bool showStress,bool settle = true)
            {
                VoxelMesher.MergeRenderFaces = merge;
                renderer.IncrementalMeshing = merge;
                if (rebuild)
                {
                    graphics.Vk.DeviceWaitIdle(graphics.Device);
                    renderer.Clear();
                    foreach (VoxelBody b in world.Bodies)
                        b.MeshDirty.UnionWith(b.Grid.Chunks.Select(c => c.Coordinate));
                }
                renderer.Sync(world, camera.Position);
                if (settle) renderer.WaitForLighting(world.Bodies,b => (b.Position,b.Orientation));
                glfw.PollEvents();
                if (!graphics.BeginFrame(out Frame frame)) throw new InvalidOperationException("Render test frame unavailable");
                string path = Path.Combine(captures, $"{sequence++}-{merge}-{showStress}.ppm");
                graphics.CapturePath = path;
                renderer.Draw(frame, camera, world.Bodies, b => (b.Position, b.Orientation), world.Particles,
                    [], null, [], [], showStress);
                graphics.EndFrame(frame);
                byte[] ppm = File.ReadAllBytes(path);
                int start = 0;
                for (int line = 0; line < 3; line++) start = Array.IndexOf(ppm, (byte)'\n', start) + 1;
                return ppm[start..];
            }
            void Damage(int pattern)
            {
                foreach (VoxelBody b in world.Bodies)
                    foreach (var (cell, _) in b.Grid.Occupied().ToArray())
                        b.SetDamage(cell, ((cell.x + 2 * cell.y + pattern) & 7) / 9.0);
            }
            bool Compare(byte[] expected, byte[] actual, string name)
            {
                long total = 0;
                int changed = 0;
                for (int i = 0; i < expected.Length; i += 3)
                {
                    int worst = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        int difference = Math.Abs(expected[i + c] - actual[i + c]);
                        total += difference;
                        worst = Math.Max(worst, difference);
                    }
                    if (worst > 8) changed++;
                }
                double mean = total / (double)expected.Length;
                // Allow texture/raster roundoff at rectangle edges, but reject wrong cell
                // colors, missing surfaces, texture orientation or stretched crack overlays.
                bool ok = mean < 0.1 && changed < expected.Length / 3 * 0.005;
                Console.WriteLine($"   {name}: mean channel difference {mean:F5}, pixels differing by >8: {changed}; {(ok ? "PASS" : "FAIL")}");
                return ok;
            }
            bool passed = true;
            renderer.Video.DynamicShadows = true;
            byte[] liveShadow = Capture(true,true,false,settle:false);
            renderer.Video.DynamicShadows = false;
            byte[] liveNoShadow = Capture(true,false,false,settle:false);
            int liveChanges = Enumerable.Range(0,liveShadow.Length).Count(i => Math.Abs(liveShadow[i]-liveNoShadow[i]) > 8);
            Console.WriteLine($"   GPU sun shadows respond without draining propagation: {liveChanges} channels; {(liveChanges > 100 ? "PASS" : "FAIL")}");
            passed &= liveChanges > 100;

            foreach (bool stress in new[] { false, true })
            {
                Damage(0);
                byte[] original = Capture(false, true, stress);
                Damage(3);
                byte[] originalChanged = Capture(false, false, stress);
                Damage(0);
                byte[] merged = Capture(true, true, stress);
                Damage(3);
                byte[] mergedChanged = Capture(true, false, stress);
                passed &= Compare(original, merged, $"cracks and rotated objects, stress={stress}");
                passed &= Compare(originalChanged, mergedChanged, $"data-only damage update, stress={stress}");
            }
            // Compare live partial uploads against a fresh independent reference mesh.
            // Keep the incremental mesh alive over successive edits, including a shrink
            // followed by growth and shape fallback transitions.
            foreach (BlockType type in new[] { BlockType.Stone, BlockType.Air, BlockType.Glass,
                BlockType.OakPlanksSlab, BlockType.Stone, BlockType.Air })
            {
                for (int x = 5; x < 10; x++)
                for (int y = 3; y < 7; y++)
                    world.Map.Grid[new ivec3(x, y, 1)] = new Voxel(type, 0);
                world.Map.MeshDirty.Add(ivec3.Zero);
                byte[] edited = Capture(true, false, false);
                byte[] reference = Capture(false, true, false);
                passed &= Compare(reference, edited, $"incremental GPU edit {type}");
                // Reinitialize the incremental state, then the next iteration edits it.
                Capture(true, true, false);
            }
            byte[] withoutShadows = Capture(true, true, false);
            renderer.Video.DynamicShadows = true;
            byte[] withShadows = Capture(true, false, false);
            int changedByShadows = Enumerable.Range(0, withShadows.Length).Count(i => Math.Abs(withShadows[i] - withoutShadows[i]) > 8);
            Console.WriteLine($"   GPU sun shadows change {changedByShadows} channels; {(changedByShadows > 100 ? "PASS" : "FAIL")}");
            passed &= changedByShadows > 100;
            renderer.Video.Sun = false;
            byte[] ambient = Capture(true, false, false);
            renderer.Video.AmbientBrightness = 0;
            byte[] unlit = Capture(true, false, false);
            int[] shadowChannels = Enumerable.Range(0, ambient.Length).Where(i => withShadows[i] + 8 < withoutShadows[i]).ToArray();
            int visibleAmbient = shadowChannels.Count(i => ambient[i] > unlit[i] + 5);
            bool ambientPass = shadowChannels.Length > 100 && visibleAmbient > shadowChannels.Length * 0.8;
            Console.WriteLine($"   ambient remains visible in sun shadows: {visibleAmbient}/{shadowChannels.Length} channels; {(ambientPass ? "PASS" : "FAIL")}");
            passed &= ambientPass;
            ivec3 furnace = new(8, 3, 2);
            world.Map.Grid[furnace] = new Voxel(BlockType.Furnace, 0, Lit: true);
            world.Map.MeshDirty.Add(ivec3.Zero);
            byte[] lit = Capture(true, false, false,settle:false);
            int litChannels = Enumerable.Range(0, lit.Length).Count(i => lit[i] > unlit[i] + 8);
            Console.WriteLine($"   GPU furnace lights immediately without draining propagation: {litChannels} channels; {(litChannels > 100 ? "PASS" : "FAIL")}");
            passed &= litChannels > 100;
            world.Map.Grid[furnace] = new Voxel(BlockType.Furnace, 0);
            world.Map.MeshDirty.Add(ivec3.Zero);
            ivec3 distantFurnace = new(48, 3, 2);
            world.Map.Grid[distantFurnace] = new Voxel(BlockType.Furnace, 0, Lit: true);
            world.Map.MeshDirty.Add(Grid3D<Voxel>.ChunkOf(distantFurnace));
            byte[] distant = Capture(true, false, false);
            renderer.Video.BlockLighting = false;
            byte[] disabled = Capture(true, false, false);
            int distantChannels = Enumerable.Range(0, distant.Length).Count(i => distant[i] > disabled[i] + 2);
            Console.WriteLine($"   GPU light reaches beyond 15 blocks: {distantChannels} channels; {(distantChannels > 100 ? "PASS" : "FAIL")}");
            passed &= distantChannels > 100;
            renderer.Video = new GameSettings();
            Camera savedCamera = camera;
            camera = new Camera(new dvec3(6.5, -4, 9.7), new dvec3(2, 5, -0.2).Normalized, Math.PI / 3, 640.0 / 480);
            byte[] background = Capture(true, false, false);
            ivec3 leaf = new(8, 1, 9);
            world.Map.Grid[leaf] = new Voxel(BlockType.Leaves, 0);
            world.Map.MeshDirty.Add(ivec3.Zero);
            byte[] cutoutLeaves = Capture(true, false, false);
            renderer.Video.TransparentLeaves = false;
            byte[] opaqueLeaves = Capture(true, false, false);
            byte[] leafPixels = Renderer.LeafCutout(ImageResult.FromMemory(
                File.ReadAllBytes(Path.Combine(assets, "images", "oak_leaves_transparent.png")), ColorComponents.RedGreenBlueAlpha).Data);
            mat4 inverseView = camera.ViewProjection().Inverse;
            int holePixels = 0, incorrectHoles = 0, filledHoles = 0;
            for (int y = 0; y < 480; y++)
            for (int x = 0; x < 640; x++)
            {
                vec4 projected = inverseView * new vec4(2f * (x + 0.5f) / 640 - 1, 2f * (y + 0.5f) / 480 - 1, 0.5f, 1);
                dvec3 direction = new dvec3(projected.xyz / projected.w) - camera.Position;
                if (direction.y <= 0) continue;
                dvec3 point = camera.Position + direction * ((leaf.y - camera.Position.y) / direction.y);
                double u = point.x - leaf.x, v = 1 - (point.z - leaf.z);
                if (u < 0.01 || u > 0.99 || v < 0.01 || v > 0.99) continue;
                // Stay clear of texel boundaries so this check is about texture selection.
                double fu = u * 16 % 1, fv = v * 16 % 1;
                if (fu < 0.2 || fu > 0.8 || fv < 0.2 || fv > 0.8) continue;
                int texel = ((int)(v * 16) * 16 + (int)(u * 16)) * 4;
                if (leafPixels[texel + 3] != 0) continue;
                holePixels++;
                int pixel = (x + 640 * y) * 3;
                if (Enumerable.Range(0, 3).Any(c => Math.Abs(cutoutLeaves[pixel+c] - background[pixel+c]) > 2)) incorrectHoles++;
                if (Enumerable.Range(0, 3).Any(c => Math.Abs(opaqueLeaves[pixel+c] - background[pixel+c]) > 8)) filledHoles++;
            }
            bool leafPass = holePixels > 100 && incorrectHoles < holePixels * 0.01 && filledHoles > holePixels * 0.95;
            Console.WriteLine($"   perspective leaf cutouts: {holePixels} holes, {incorrectHoles} incorrect, {filledHoles} filled in opaque mode; {(leafPass ? "PASS" : "FAIL")}");
            passed &= leafPass;
            renderer.Video.TransparentLeaves = true;
            camera = savedCamera;
            // Grow both frame slots beyond their initial draw-record capacity.
            for (int i = 0; i < 320; i++)
                world.Spawn([(ivec3.Zero, BlockType.Stone)], new dvec3(4 + i % 20 * 0.4, 3 + i / 20 * 0.4, 3.5), dquat.Identity);
            world.ApplyTopologyChanges();
            // Direct indexed submission and multi-draw indirect must preserve exactly the
            // same pixels and surface order, including nonzero firstInstance data lookup.
            foreach (bool stress in new[] { false, true })
            {
                Damage(2);
                renderer.IndirectDraws = false;
                byte[] direct = Capture(true, true, stress);
                int directCalls = renderer.VoxelDrawCalls;
                renderer.IndirectDraws = true;
                byte[] batched = Capture(true, false, stress);
                bool identical = direct.AsSpan().SequenceEqual(batched);
                bool fewerCalls = directCalls > 256 && (!graphics.SupportsMultiDrawIndirect || renderer.VoxelDrawCalls < directCalls);
                Console.WriteLine($"   indirect submission, stress={stress}: identical pixels={identical}, draw calls {directCalls} -> {renderer.VoxelDrawCalls}; {(identical && fewerCalls ? "PASS" : "FAIL")}");
                passed &= identical && fewerCalls;
            }
            renderer.ParallelMeshing = false;
            byte[] serialMeshes = Capture(true, true, false);
            renderer.ParallelMeshing = true;
            byte[] parallelMeshes = Capture(true, true, false);
            bool sameMeshes = serialMeshes.AsSpan().SequenceEqual(parallelMeshes);
            Console.WriteLine($"   serial and parallel mesh batches: identical pixels={sameMeshes}; {(sameMeshes ? "PASS" : "FAIL")}");
            passed &= sameMeshes;
            if (passed) Directory.Delete(captures, recursive: true);
            else Console.WriteLine($"   Comparison images: {captures}");
            return passed;
        }
        finally
        {
            VoxelMesher.MergeRenderFaces = savedMerge;
            glfw.DestroyWindow(window);
            glfw.Terminate();
        }
    }
}
