using GlmSharp;
using StbImageSharp;
using MYT.Kit.Vulkan;
using Silk.NET.GLFW;
using VoxelGame;

namespace VoxelTests;

internal static class SettingsTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios =
    [
        ("settings-controls", Settings),
        ("leaves-visibility", Leaves),
        ("lighting-scene", Lighting),
        ("held-key-state", HeldKeys),
        ("physical-keyboard-x11", PhysicalKeys),
        ("lighting-cache", LightingCache)
    ];
    private static bool Expect(string label, bool success)
    {
        Console.WriteLine($"   {(success ? "ok" : "BAD")} {label}");
        return success;
    }
    private static bool Settings()
    {
        string path = Path.Combine(Path.GetTempPath(), $"myt-settings-{Guid.NewGuid()}", "settings.json");
        try
        {
            GameSettings settings = new();
            settings.Bind(GameAction.Forward, (int)Keys.R);
            bool ok = Expect("conflicting bindings swap", settings.Binding(GameAction.Shoot) == (int)Keys.W);
            settings.Bind(GameAction.Mine, (int)Keys.P);
            settings.TransparentLeaves = false;
            settings.Particles = false;
            settings.DynamicShadows = true;
            settings.Save(path);
            var loaded = GameSettings.Load(path);
            ok &= Expect("video and mouse/keyboard bindings survive saving", !loaded.TransparentLeaves && !loaded.Particles && loaded.DynamicShadows
                && loaded.Binding(GameAction.Forward) == (int)Keys.R && loaded.Binding(GameAction.Mine) == (int)Keys.P);
            File.WriteAllText(path, "{\"Keybinds\":null,\"AmbientBrightness\":999}");
            loaded = GameSettings.Load(path);
            ok &= Expect("old or hand-edited settings recover defaults", loaded.Binding(GameAction.Shoot) == (int)Keys.R && loaded.AmbientBrightness == 100 && loaded.Particles);
            ok &= Expect("F3 and Escape stay reserved", !Controls.Valid((int)Keys.F3) && !Controls.Valid((int)Keys.Escape)
                && Controls.Valid(-3) && !Controls.Valid(-4));
            return ok;
        }
        finally { if (File.Exists(path)) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
    private static bool Leaves()
    {
        Grid3D<Voxel> grid = new();
        grid[new ivec3(1,1,1)] = new Voxel(BlockType.Leaves, 0);
        grid[new ivec3(2,1,1)] = new Voxel(BlockType.Leaves, 0);
        Voxel[] cells = new Voxel[VoxelMesher.PaddedVolume];
        VoxelMesher.CopyPadded(grid, ivec3.Zero, cells);
        int[,] layers = new int[Blocks.Count, 7];
        uint[] fancy = VoxelMesher.BuildRenderMeshReference(cells, layers, true);
        uint[] fast = VoxelMesher.BuildRenderMeshReference(cells, layers, false);
        bool ok = Expect("fancy leaves retain both internal faces", fancy.Length == 12 * 8 && fast.Length == 10 * 8);
        double Area(uint[] mesh)
        {
            dvec3 Point(uint a) => new(a & 31, (a >> 5) & 31, (a >> 10) & 31);
            double area = 0;
            for (int i = 0; i < mesh.Length; i += 8)
                area += dvec3.Cross(Point(mesh[i+2])-Point(mesh[i]), Point(mesh[i+4])-Point(mesh[i])).Length;
            return area;
        }
        ok &= Expect("greedy merging preserves every fancy leaf interface", Area(VoxelMesher.BuildMergedRenderMesh(cells, layers, true)) == 12
            && Area(VoxelMesher.BuildMergedRenderMesh(cells, layers, false)) == 10);
        grid[new ivec3(3,1,1)] = new Voxel(BlockType.StoneSlab, 0);
        VoxelMesher.CopyPadded(grid, ivec3.Zero, cells);
        var halfFaces = BlockShapes.Faces(cells, false, true).ToArray();
        ok &= Expect("shape mesher keeps leaf interfaces but removes internal half-cell faces",
            halfFaces.Count(f => f.Voxel.Type == BlockType.Leaves) == 48);
        string texture = Path.Combine(TestGpu.Shaders, "..", "images", "oak_leaves_transparent.png");
        byte[] original = ImageResult.FromMemory(File.ReadAllBytes(texture), ColorComponents.RedGreenBlueAlpha).Data;
        byte[] cutout = Renderer.LeafCutout(original);
        int holes = 0, opaque = 0;
        for (int i = 0; i < cutout.Length; i += 4)
        {
            if (cutout[i+3] == 0) holes++;
            else if (cutout[i+3] == 255) opaque++;
        }
        ok &= Expect("authored gray leaf holes become transparent while green leaves remain opaque", holes > 0 && opaque > 0);
        return ok;
    }
    private static bool Lighting()
    {
        bool ok = Expect("glass and fancy leaves transmit light", LightingScene.Mask(new Voxel(BlockType.Glass, 0), false) == 0
            && LightingScene.Mask(new Voxel(BlockType.Leaves, 0), true) == 0 && LightingScene.Mask(new Voxel(BlockType.Leaves, 0), false) == 255);
        ok &= Expect("slabs occlude their occupied half only", LightingScene.Mask(new Voxel(BlockType.StoneSlab, 0), true) == 15);
        Grid3D<Voxel> grid = new();
        for (int y = 0; y < 16; y++)
        for (int x = -16; x < 48; x++) grid[new ivec3(x,y,0)] = new Voxel(BlockType.Stone,0);
        for (int z = 1; z <= 6; z++)
        for (int y = 4; y <= 10; y++)
        for (int x = 4; x <= 10; x++)
            if (x == 4 || x == 10 || y == 4 || y == 10 || z == 6)
                grid[new ivec3(x,y,z)] = new Voxel(BlockType.Stone,0);
        LightChunk[] Snapshot()
        {
            return grid.Chunks.Select(chunk =>
            {
                byte[] masks = new byte[4096]; List<ivec3> lights = [];
                for (int i = 0; i < 4096; i++)
                {
                    masks[i] = (byte)LightingScene.Mask(chunk.Cells[i],true);
                    if (chunk.Cells[i].Lit) lights.Add(chunk.Origin+new ivec3(i&15,(i>>4)&15,i>>8));
                }
                return new LightChunk(chunk.Coordinate,masks,[.. lights]);
            }).ToArray();
        }
        LightVolume closed = VoxelLightField.Bake(Snapshot(),[]);
        ok &= Expect("sealed room receives neither sky light nor sunlight", LightVolume.Sky(closed.Sample(new(7,7,3))) == 0 && LightVolume.Sun(closed.Sample(new(7,7,3))) == 0);
        ok &= Expect("outside air and negative chunks receive sky light", LightVolume.Sky(closed.Sample(new(3,7,3))) == 1 && LightVolume.Sky(closed.Sample(new(-1,7,3))) == 1);
        grid[new ivec3(4,4,3)] = default;
        LightVolume corner = VoxelLightField.Bake(Snapshot(),[]);
        ok &= Expect("diagonal cracks do not leak through solid wall corners", LightVolume.Sky(corner.Sample(new(7,7,3))) == 0);
        grid[new ivec3(4,7,2)] = default; grid[new ivec3(4,7,3)] = default;
        LightVolume open = VoxelLightField.Bake(Snapshot(),[]);
        float inner = LightVolume.Sky(open.Sample(new(7,7,3)));
        ok &= Expect("sky light propagates through a doorway and fades indoors", inner > 0 && inner < 1);
        grid[new ivec3(1,1,1)] = new Voxel(BlockType.Furnace,0,Lit:true);
        LightVolume lit = VoxelLightField.Bake(Snapshot(),[]);
        float far = LightVolume.Block(lit.Sample(new(41,1,1)));
        ok &= Expect("block light propagates beyond 15 cells without a fixed cutoff", far > 0 && far < LightVolume.Block(lit.Sample(new(11,1,1))));
        float axial = LightVolume.Block(lit.Sample(new(11,1,1))), diagonal = LightVolume.Block(lit.Sample(new(-5,9,1)));
        ok &= Expect("spherical propagation approximates equal Euclidean radii", Math.Abs(axial-diagonal)/axial < 0.2);
        ok &= Expect("block light can turn through connected air without surface reflection", LightVolume.Block(lit.Sample(new(7,7,3))) > 0);
        grid[new ivec3(1,1,1)] = new Voxel(BlockType.Furnace,0);
        LightVolume extinguished = VoxelLightField.Bake(Snapshot(),[]);
        ok &= Expect("extinguishing a source removes its propagated light", LightVolume.Block(extinguished.Sample(new(41,1,1))) == 0);
        grid[new ivec3(4,7,2)] = new Voxel(BlockType.Stone,0); grid[new ivec3(4,7,3)] = new Voxel(BlockType.Stone,0);
        LightVolume resealed = VoxelLightField.Bake(Snapshot(),[]);
        ok &= Expect("closing the doorway removes stale sky light",LightVolume.Sky(resealed.Sample(new(7,7,3))) == 0);
        for (int y = 4; y <= 10; y++)
        for (int x = 4; x <= 10; x++) grid[new ivec3(x,y,6)] = new Voxel(BlockType.StoneSlab,0);
        LightVolume slabRoof = VoxelLightField.Bake(Snapshot(),[]);
        ok &= Expect("a closed slab roof does not leak sky light through its solid half",LightVolume.Sky(slabRoof.Sample(new(7,7,3))) == 0);
        return ok;
    }
    private static bool LightingCache()
    {
        VoxelMap map = new(1); map.Grid[new ivec3(1,1,0)] = new Voxel(BlockType.Stone,0);
        using LightingScene scene = new();
        scene.Invalidate(map,ivec3.Zero,true); scene.Build([map],b => (b.Position,b.Orientation),force:true); scene.Wait();
        int version = scene.Version;
        bool ok = Expect("incremental field becomes ready",scene.Ready && scene.SampleRgb(new(1,1,1)).x > 0);
        map.Grid[new ivec3(1,1,0)] = new Voxel(BlockType.Stone,100);
        scene.Invalidate(map,ivec3.Zero,true); scene.Build([map],b => (b.Position,b.Orientation),force:true); scene.Wait();
        ok &= Expect("damage-only changes reuse the cached transport field",scene.Version == version);
        map.Grid[new ivec3(1,1,1)] = new Voxel(BlockType.Furnace,0,Lit:true);
        scene.Invalidate(map,ivec3.Zero,true); scene.Build([map],b => (b.Position,b.Orientation),force:true); scene.Wait();
        ok &= Expect("source changes invalidate the field",scene.Version > version && scene.SampleRgb(new(2,1,1)).x > 1);
        VoxelObject body = new(2) { PendingPosition = new dvec3(4,4,4) };
        body.Grid[ivec3.Zero] = new Voxel(BlockType.Stone,0);
        scene.Invalidate(body,ivec3.Zero,true);
        scene.Build([map,body],b => (b.Position,b.Orientation),force:true); scene.Wait();
        version = scene.Version;
        body.PendingPosition += new dvec3(1e-8,-1e-8,0);
        scene.Build([map,body],b => (b.Position,b.Orientation),force:true); scene.Wait();
        ok &= Expect("resting-object roundoff does not trigger another full bake",scene.Version == version);
        body.PendingPosition += new dvec3(0.5,0,0);
        scene.Build([map,body],b => (b.Position,b.Orientation),force:true); scene.Wait();
        ok &= Expect("moving objects cast shadows without occupying ambient grid cells",scene.SampleRgb(new(4,4,4)).x > 0);
        scene.Clear();
        return ok & Expect("world reset discards the old light field",!scene.Ready && scene.SampleRgb(new(2,1,1)) == vec3.Zero);
    }
    private static bool PhysicalKeys()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Console.WriteLine("   SKIP: no X11 display"); return true;
        }
        Glfw glfw = Glfw.GetApi();
        if (!glfw.Init()) { Console.WriteLine("   SKIP: display unavailable"); return true; }
        try
        {
            Console.WriteLine($"   native context {glfw.Context.GetType().Name}, X11 export {glfw.Context.TryGetProcAddress("glfwGetX11Display", out _)}");
            PhysicalKeyboard? physical = PhysicalKeyboard.TryCreate(glfw, [Keys.W, Keys.Space, Keys.ShiftLeft]);
            physical?.Update();
            return Expect("physical key bitmap can be read through GLFW's X11 connection", physical != null);
        }
        finally { glfw.Terminate(); }
    }

    private static bool HeldKeys()
    {
        KeyboardState keyboard = new();
        keyboard.Handle(Keys.W,InputAction.Press); keyboard.Update();
        bool ok = Expect("press is one edge and a held key",keyboard.Down(Keys.W) && keyboard.Pressed(Keys.W));
        for (int i = 0; i < 10000; i++)
        {
            if (i%3 == 0) keyboard.Handle(Keys.W,InputAction.Repeat);
            keyboard.Update();
            if (!keyboard.Down(Keys.W) || keyboard.Pressed(Keys.W)) return Expect("hold survives frames and auto-repeat",false);
        }
        ok &= Expect("hold survives frames and auto-repeat",true);
        keyboard.Handle(Keys.A,InputAction.Press); keyboard.Update();
        keyboard.Handle(Keys.A,InputAction.Release); keyboard.Update();
        ok &= Expect("releasing another key does not drop movement",keyboard.Down(Keys.W) && !keyboard.Down(Keys.A));
        keyboard.Handle(Keys.W,InputAction.Release); keyboard.Update();
        ok &= Expect("release clears the held key",!keyboard.Down(Keys.W));
        keyboard.Handle(Keys.W,InputAction.Press); keyboard.Clear(); keyboard.Update();
        ok &= Expect("focus loss clears physical and pending state",!keyboard.Down(Keys.W) && !keyboard.Pressed(Keys.W));
        keyboard.Handle(Keys.E,InputAction.Press); keyboard.Handle(Keys.E,InputAction.Release); keyboard.Update();
        ok &= Expect("a short tap between frames preserves its press edge",keyboard.Pressed(Keys.E) && !keyboard.Down(Keys.E));
        keyboard.Reconcile(Keys.W, true); keyboard.Update();
        ok &= Expect("polling recovers a missed press", keyboard.Down(Keys.W) && keyboard.Pressed(Keys.W));
        keyboard.Reconcile(Keys.W, true); keyboard.Update();
        ok &= Expect("polling a hold creates no repeated edge", keyboard.Down(Keys.W) && !keyboard.Pressed(Keys.W));
        keyboard.Reconcile(Keys.W, false); keyboard.Update();
        ok &= Expect("polling recovers a missed release", !keyboard.Down(Keys.W));
        keyboard.Handle(Keys.W, InputAction.Press, physicalPolling: true);
        keyboard.Reconcile(Keys.W, true); keyboard.Update();
        for (int i = 0; i < 100; i++)
        {
            keyboard.Handle(Keys.W, InputAction.Release, physicalPolling: true);
            keyboard.Reconcile(Keys.W, true); keyboard.Update();
            if (!keyboard.Down(Keys.W) || keyboard.Pressed(Keys.W)) return Expect("false native release cannot drop or retrigger held movement", false);
        }
        ok &= Expect("false native release cannot drop or retrigger held movement", true);
        keyboard.Reconcile(Keys.W, false); keyboard.Update();
        ok &= Expect("actual physical release stops movement", !keyboard.Down(Keys.W));
        keyboard.Handle(Keys.E, InputAction.Press, physicalPolling: true);
        keyboard.Handle(Keys.E, InputAction.Release, physicalPolling: true);
        keyboard.Reconcile(Keys.E, false); keyboard.Update();
        ok &= Expect("physical polling preserves short tap edges", keyboard.Pressed(Keys.E) && !keyboard.Down(Keys.E));
        byte[] keys = new byte[32]; keys[3] = 1 << 5;
        return ok & Expect("X11 bitmap uses native keycodes without byte sign errors",
            PhysicalKeyboard.IsDown(keys, 29) && !PhysicalKeyboard.IsDown(keys, 28)
            && !PhysicalKeyboard.IsDown(keys, -1) && !PhysicalKeyboard.IsDown(keys, 256));
    }
}
