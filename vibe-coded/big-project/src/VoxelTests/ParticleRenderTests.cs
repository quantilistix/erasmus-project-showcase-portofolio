using GlmSharp;
using Silk.NET.GLFW;
using VoxelGame;

namespace VoxelTests;

internal static class ParticleRenderTests
{
    public static unsafe bool Run(GraphicsContext graphics, Renderer renderer, World world, string captures)
    {
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++) world.Map.Grid[new(x, y, 0)] = new(BlockType.Stone, 0);
        world.Map.MeshDirty.UnionWith(world.Map.Grid.TakeChangedChunks());
        world.Particles.EmitExplosion(new(8, 8, 4));
        // Let the flash expire without moving particles: comparisons use identical data.
        Span<vec4> flashes = stackalloc vec4[4];
        world.Particles.ReadFlashes(flashes); world.Particles.ReadFlashes(flashes);
        Camera camera = new(new(8, -5, 9), new dvec3(0, 13, -5).Normalized, 1.2, 640.0 / 480);
        BlockParticles empty = new();
        renderer.Video.DynamicShadows = false;
        renderer.Video.SpecularLighting = false;
        int sequence = 0;
        byte[] Capture(bool geometry, bool particles)
        {
            renderer.GeometryParticles = geometry && graphics.SupportsGeometryShader;
            renderer.Sync(world, camera.Position);
            renderer.WaitForLighting(world.Bodies, b => (b.Position, b.Orientation));
            Glfw.GetApi().PollEvents();
            if (!graphics.BeginFrame(out Frame frame)) throw new InvalidOperationException("Particle frame unavailable");
            string path = Path.Combine(captures, $"particle-{sequence++}.ppm");
            graphics.CapturePath = path;
            renderer.Draw(frame, camera, world.Bodies, b => (b.Position, b.Orientation),
                particles ? world.Particles : empty, [], null, [], [], false);
            graphics.EndFrame(frame);
            byte[] ppm = File.ReadAllBytes(path);
            int start = 0;
            for (int i = 0; i < 3; i++) start = Array.IndexOf(ppm, (byte)'\n', start) + 1;
            return ppm[start..];
        }
        byte[] background = Capture(true, false);
        byte[] geometryImage = Capture(true, true);
        bool single = renderer.ParticleDrawCalls == 1 && renderer.ParticlesDrawn == world.Particles.Count;
        byte[] instanceImage = Capture(false, true);
        int differences = 0, visible = 0;
        for (int i = 0; i < background.Length; i++)
        {
            if (Math.Abs(geometryImage[i] - instanceImage[i]) > 2) differences++;
            if (Math.Abs(geometryImage[i] - background[i]) > 8) visible++;
        }
        bool ok = single && differences < 20 && visible > 100;
        Console.WriteLine($"   one particle draw: {single}; visible channels {visible}; geometry/instanced differences >2: {differences}; {(ok ? "PASS" : "FAIL")}");

        // Keep camera and cloud fixed. Directional sunlight must use its billboard normal.
        renderer.Video.AmbientBrightness = 0;
        renderer.Video.BlockLighting = false;
        vec3 normal = -(vec3)camera.Forward;
        renderer.Daylight = renderer.Daylight with { SunDirection = normal, SunIntensity = 1 };
        byte[] front = Capture(true, true);
        renderer.Daylight = renderer.Daylight with { SunDirection = -normal };
        byte[] back = Capture(true, true);
        int directional = 0;
        for (int i = 0; i < front.Length; i++)
            if (Math.Abs(geometryImage[i] - background[i]) > 8 && front[i] > back[i] + 8) directional++;
        bool normals = directional > 100;
        ok &= normals;
        Console.WriteLine($"   billboard normal responds to sun direction: {directional} particle channels brighten; {(normals ? "PASS" : "FAIL")}");

        // Disable sunlight/sky; a nearby lit furnace must illuminate the cloud locally.
        renderer.Video.Sun = false;
        renderer.Video.BlockLighting = true;
        world.Map.Grid[new(8, 7, 3)] = new(BlockType.Furnace, 0, Lit: true);
        world.Map.MeshDirty.Add(ivec3.Zero);
        byte[] localLight = Capture(true, true);
        world.Map.Grid[new(8, 7, 3)] = new(BlockType.Furnace, 0);
        world.Map.MeshDirty.Add(ivec3.Zero);
        byte[] dark = Capture(true, true);
        int local = 0;
        for (int i = 0; i < localLight.Length; i++)
            if (Math.Abs(geometryImage[i] - background[i]) > 8 && localLight[i] > dark[i] + 8) local++;
        bool lighting = local > 100;
        ok &= lighting;
        Console.WriteLine($"   fragment-position block lighting: {local} particle channels brighten near a furnace; {(lighting ? "PASS" : "FAIL")}");
        if (ok) Directory.Delete(captures, true);
        else Console.WriteLine($"   Particle comparison images: {captures}");
        return ok;
    }
}
