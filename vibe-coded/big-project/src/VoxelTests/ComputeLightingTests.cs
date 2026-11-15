using GlmSharp;
using MYT.Kit.Vulkan;
using Silk.NET.Vulkan;
using VoxelGame;

namespace VoxelTests;

/// <summary>Runs the actual compute kernels; no CPU lighting oracle or convergence drain.</summary>
internal static class ComputeLightingTests
{
    /// <summary>
    /// GPU time of two diffusion steps over generated terrain, 16 × 16 columns, with every tile
    /// awake (MYT_LIGHT_REUSE=0's exhaustive work), then as it settles; and of a step's metadata
    /// work on the CPU after a block edit.
    /// </summary>
    public static bool Benchmark(GraphicsContext graphics,string shaders,World world)
    {
        TerrainGenerator generator = new(7);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
            world.LoadColumn(generator.GenerateColumn(new ivec2(x,y)));
        using ComputeLighting light = new(graphics,shaders) { ReuseUnchanged = false };
        light.Configure(new vec3(1.0f,1.3f,1.7f),true,true);
        List<double> awake = [], settling = [];
        for (int frame = 0; frame < 160; frame++)
        {
            if (frame == 80) light.ReuseUnchanged = true;
            uint index = (uint)(frame % GraphicsContext.FramesInFlight);
            light.Build(world.Bodies,b=>(b.Position,b.Orientation),true); light.Prepare(index);
            if (frame >= 20 && frame < 80) awake.Add(light.DiffuseMilliseconds);
            if (frame >= 100) settling.Add(light.DiffuseMilliseconds);
            graphics.Submit(cmd=>light.Record(new Frame(cmd,index,0)));
        }
        awake.Sort(); settling.Sort();
        System.Diagnostics.Stopwatch edit = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            ivec3 cell = new(40+i,40,60);
            world.Map.Grid[cell] = new Voxel(BlockType.Stone,0);
            light.Invalidate(world.Map,cell>>4,true);
            light.Build(world.Bodies,b=>(b.Position,b.Orientation),true);
        }
        Console.WriteLine($"   {light.CellCount:N0} cells: two steps with every tile awake {awake[awake.Count/2]:F3} ms median on the GPU; settling {settling[settling.Count/2]:F3} ms; a block edit's metadata {edit.Elapsed.TotalMilliseconds/20:F3} ms on the CPU, {light.MetadataBytes/1024} KB mirror");
        return true;
    }

    public static bool Run(GraphicsContext graphics,string shaders,World world)
    {
        using ComputeLighting light = new(graphics,shaders);
        using ComputeLighting reference = new(graphics,shaders)
        { ReuseUnchanged = false, CompactDispatch = false, IncrementalMetadata = false };
        light.Configure(vec3.Zero,true,false);
        reference.Configure(vec3.Zero,true,false);
        Voxel Furnace() => new Voxel(BlockType.Furnace,0) { Lit = true };
        // Identical sources ten chunks apart must progress together without a viewer/focus.
        world.Map.LoadedColumns = [new(0,0),new(1,0),new(10,0)]; world.Map.ColumnVersion++;
        world.Map.Grid[new ivec3(1,12,12)] = Furnace();
        world.Map.Grid[new ivec3(161,12,12)] = Furnace();
        for (int z=4;z<=8;z++) for (int y=4;y<=8;y++) for (int x=4;x<=8;x++)
            if (x==4 || x==8 || y==4 || y==8 || z==4 || z==8)
                world.Map.Grid[new ivec3(x,y,z)] = new Voxel(BlockType.Stone,0);
        world.Map.Grid[new ivec3(3,6,6)] = Furnace();
        world.Map.Grid[new ivec3(4,6,6)] = new Voxel(BlockType.Glass,0);
        bool equivalent = true;
        int stepIndex = 0;
        void Step(int iterations=2)
        {
            uint frameIndex=(uint)(stepIndex++ % GraphicsContext.FramesInFlight);
            foreach (ComputeLighting field in new[] { light, reference })
            {
                field.Build(world.Bodies,b=>(b.Position,b.Orientation),true); field.Prepare(frameIndex);
                graphics.Submit(cmd=>field.Record(new Frame(cmd,frameIndex,0),iterations));
            }
            // The incremental mirror and its uploads must hold what a full layout would.
            string? metadata = light.CheckMetadata();
            if (metadata != null) equivalent &= Expect($"incremental light metadata is consistent: {metadata}",false);
            var expectedShadows = reference.CapturePointShadows();
            var actualShadows = light.CapturePointShadows();
            if (actualShadows.Count != expectedShadows.Count || !actualShadows.All(p => expectedShadows.TryGetValue(p.Key,out float[]? other) && p.Value.SequenceEqual(other)))
                equivalent &= Expect("reused point shadow maps match maps traced afresh",false);
            var expected = reference.Capture().ToDictionary(c=>c.Coordinate);
            var actual = light.Capture();
            bool same = actual.Length==expected.Count && actual.All(c=>expected.TryGetValue(c.Coordinate,out var other)
                && c.GeometryHash==other.GeometryHash && c.Rgb.SequenceEqual(other.Rgb));
            equivalent &= Expect($"sleeping tiles match exhaustive diffusion exactly ({iterations} iterations)",same);
        }
        void Invalidate(ivec3 key) { light.Invalidate(world.Map,key,true); reference.Invalidate(world.Map,key,true); }
        void Restore(LightChunkState[] chunks)
        {
            light.Clear(); light.Restore(chunks);
            reference.Clear(); reference.Restore(chunks);
        }
        vec3 Sample(LightChunkState[] chunks,ivec3 p)
        {
            LightChunkState c=chunks.Single(c=>c.Coordinate==p>>4);
            uint v=c.Rgb.Length==1 ? c.Rgb[0] : c.Rgb[(p.x&15)+16*(p.y&15)+256*(p.z&15)];
            float scale=MathF.Pow(2,(int)(v>>27)-24);
            return new vec3(v&511,(v>>9)&511,(v>>18)&511)*scale;
        }
        bool Expect(string label,bool condition) { Console.WriteLine($"   {(condition ? "ok" : "BAD")} {label}"); return condition; }
        Step(); var first=light.Capture();
        vec3 near=Sample(first,new(2,12,12)), far=Sample(first,new(162,12,12));
        bool ok=Expect("near and distant loaded regions propagate in the same frame",near.x>0 && near==far);
        ok &= Expect("completely empty loaded columns own an air light grid",first.Any(c=>c.Coordinate==new ivec3(1,0,0)));
        ok &= Expect("air blocks retain their own RGB light value",near.x>near.y && near.y>near.z);
        int shadowDispatches=light.PointShadowDispatches;
        Step(32); var warm=light.Capture();
        ok &= Expect("unchanged point shadow maps are reused",light.PointShadowDispatches==shadowDispatches);
        float glass=Sample(warm,new(6,6,6)).x;
        ok &= Expect("RGB heat diffusion travels through glass into an enclosed room",glass>.001f);
        world.Map.Grid[new ivec3(4,6,6)]=new Voxel(BlockType.Stone,0);
        Invalidate(ivec3.Zero); Step(128);
        ok &= Expect("terrain edits rebuild cached point shadows",light.PointShadowDispatches>shadowDispatches);
        float closed=Sample(light.Capture(),new(6,6,6)).x;
        ok &= Expect("an opaque wall removes room lighting without leaking through the wall",closed<glass*.01f);
        world.Map.Grid[new ivec3(1,12,12)]=new Voxel(BlockType.Furnace,0);
        Invalidate(ivec3.Zero); Step(128);
        var cooled=light.Capture();
        ok &= Expect("extinguished sources cool while distant lit regions keep updating",Sample(cooled,new(2,12,12)).x<Sample(cooled,new(162,12,12)).x*.5f);
        string path=Path.Combine(Path.GetTempPath(),$"myt-light-{Guid.NewGuid():N}.dat");
        LightChunkState[] saved;
        try
        {
            WorldFile.Write(path,new WorldSnapshot([],[],default,new ItemStack[Inventory.Size],[]) { Lighting=cooled });
            saved=WorldFile.Read(path).Lighting;
            ok &= Expect("world save round-trips every light cell exactly",saved.Length==cooled.Length && saved.Zip(cooled).All(p=>p.First.Coordinate==p.Second.Coordinate && p.First.GeometryHash==p.Second.GeometryHash && p.First.Rgb.SequenceEqual(p.Second.Rgb)));
        }
        finally { File.Delete(path); }
        Restore(saved); Step();
        ok &= Expect("loaded light resumes from the saved GPU grid",Sample(light.Capture(),new(162,12,12)).x>.1f);
        // Neighboring grids and allocated values must survive storage-buffer growth.
        world.Map.Grid[new ivec3(320,6,6)]=Furnace(); Invalidate(new(20,0,0)); Step();
        ok &= Expect("streaming new chunks preserves existing light",Sample(light.Capture(),new(162,12,12)).x>.1f);
        var stale=light.Capture().Select(c=>c.Coordinate==new ivec3(10,0,0) ? c with { GeometryHash=c.GeometryHash^1u } : c).ToArray();
        Restore(stale); Step();
        ok &= Expect("mismatched saved geometry cannot restore stale light",Sample(light.Capture(),new(172,12,12)).x==0);
        // Let isolated outdoor regions converge, then change sources and topology. The
        // exact comparison checks every saved RGB word, including sleeping interiors.
        Step(128); Step(); Step();
        foreach (ComputeLighting field in new[] { light, reference }) field.Configure(new vec3(.4f,.2f,.1f),false,true);
        Step(); Step(32);
        // Reflected sunlight must follow the cycle too, rather than keeping the old
        // fixed noon sun alive in the ambient grid throughout the night.
        Daylight noon = DaylightCycle.Evaluate(0), night = DaylightCycle.Evaluate(DaylightCycle.DurationSeconds / 2);
        foreach (ComputeLighting field in new[] { light, reference })
            field.Configure(vec3.Zero,false,true,noon.SunDirection,noon.SunColor,noon.SunIntensity);
        Step(128);
        float reflectedDay = Sample(light.Capture(),new(9,6,6)).x;
        foreach (ComputeLighting field in new[] { light, reference })
            field.Configure(vec3.Zero,false,true,night.SunDirection,night.SunColor,night.SunIntensity);
        Step(128);
        float reflectedNight = Sample(light.Capture(),new(9,6,6)).x;
        ok &= Expect("reflected sunlight switches off at night",reflectedDay > .001f && reflectedNight < reflectedDay*.1f);
        foreach (ComputeLighting field in new[] { light, reference }) field.Configure(vec3.Zero,true,false);
        Step();
        world.Map.Grid[new ivec3(161,12,12)]=default;
        Invalidate(new(10,0,0)); Step();
        // A fully opaque chunk exercises the diffusion kernel's separate wall path.
        for (int z=0;z<16;z++) for (int y=0;y<16;y++) for (int x=0;x<16;x++)
            world.Map.Grid[new ivec3(x,y,z+16)]=new Voxel(BlockType.Stone,0);
        Invalidate(new(0,0,1)); Step(32); Step();
        world.Map.Grid.RemoveChunk(new(0,0,1));
        Invalidate(new(0,0,1)); Step();
        foreach (ComputeLighting field in new[] { light, reference }) field.Configure(new vec3(.2f),true,true);
        Step();
        ok &= Expect("removed opaque chunks become sunlit air",Sample(light.Capture(),new(8,8,20)).x>.19f);
        foreach (ComputeLighting field in new[] { light, reference }) field.Configure(vec3.Zero,true,false);
        world.Map.Grid[new ivec3(-1,8,8)]=Furnace();
        world.Map.Grid[new ivec3(0,8,8)]=new Voxel(BlockType.StoneSlab,0,BlockOrientation.SlabBottom);
        Invalidate(new(-1,0,0)); Invalidate(ivec3.Zero); Step(32); Step();
        VoxelObject emitter=world.Spawn([(ivec3.Zero,BlockType.Furnace)],new dvec3(20,8,8),dquat.Identity);
        emitter.Grid[ivec3.Zero]=Furnace();
        foreach (ComputeLighting field in new[] { light, reference }) field.Invalidate(emitter,ivec3.Zero,true);
        Step(32); emitter.PendingPosition+=new dvec3(2,0,0); Step();
        emitter.PendingPosition+=new dvec3(16,0,0); Step(); Step();
        emitter.PendingPosition-=new dvec3(32,0,0); Step(); Step();

        // With no point sources, geometry changes wake only affected roof columns and
        // their neighbors. Compare that path after outdoor tiles have gone to sleep.
        foreach (var (cell,voxel) in world.Map.Grid.Occupied().Where(c=>c.Value.Lit).ToArray())
        {
            world.Map.Grid[cell]=voxel with { Lit=false };
            Invalidate(cell>>4);
        }
        emitter.Grid[ivec3.Zero]=new Voxel(BlockType.Furnace,0);
        foreach (ComputeLighting field in new[] { light, reference })
        {
            field.Invalidate(emitter,ivec3.Zero,true);
            field.Configure(new vec3(.2f),true,true);
        }
        Step(128); Step();
        world.Map.Grid[new ivec3(8,8,24)]=new Voxel(BlockType.Stone,0);
        Invalidate(new(0,0,1)); Step(); Step(32);
        world.Map.Grid.RemoveChunk(new(0,0,1)); Invalidate(new(0,0,1)); Step();
        ulong previousBytes=light.GridBytes;
        for (int i=40;i<72;i++) world.Map.LoadedColumns.Add(new(i,0));
        world.Map.ColumnVersion++; Step();
        ok &= Expect("light and activity buffers grow while preserving existing cells",light.GridBytes>previousBytes);
        previousBytes=light.GridBytes;
        world.Map.LoadedColumns.Add(new(100,0)); world.Map.ColumnVersion++; Step();
        ok &= Expect("new slots also preserve flags within existing buffer capacity",light.GridBytes==previousBytes);
        Console.WriteLine($"   incremental metadata: {light.Edits} edits, {light.Relayouts} layouts; point shadows traced in {light.PointShadowDispatches} frames");
        return ok && equivalent;
    }
}
