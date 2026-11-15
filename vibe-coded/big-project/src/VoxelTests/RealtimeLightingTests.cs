using System.Diagnostics;
using GlmSharp;
using VoxelGame;

namespace VoxelTests;

internal static class RealtimeLightingTests
{
    public static readonly (string Name,Func<bool> Run)[] Scenarios = [("lighting-realtime", Run),("lighting-live-updates",LiveUpdates)];
    private static bool Check(string label,bool ok)
    {
        Console.WriteLine($"   {(ok ? "ok" : "BAD")} {label}"); return ok;
    }
    private static void Drain(RealtimeLightField field)
    {
        int rounds = 0;
        while (field.Pending && rounds++ < 2000) field.Step(double.PositiveInfinity,65536);
        if (field.Pending) throw new Exception("Lighting did not settle");
    }
    private static bool LiveUpdates()
    {
        vec3 sky = new(.3f,.6f,.9f);
        RealtimeLightField field = new() { GpuSunShadows = true };
        field.Configure(sky,true,true);
        byte[] lower = new byte[4096], upper = new byte[4096];
        for (int z = 0; z < 16; z++) for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++)
            if (z == 0 || x == 0 || x == 15 || y == 0 || y == 15) lower[x+16*y+256*z] = 255;
        for (int i = 0; i < 256; i++) upper[i] = 255;
        LightChunk a = new(ivec3.Zero,lower,[]), b = new(new(0,0,1),(byte[])upper.Clone(),[]);
        field.PrepareBatch([a,b]);
        field.Set(a,new vec3[4096]); field.Set(b,new vec3[4096]); field.EndBatch();
        bool ok = Check("streamed roof prevents temporary sky light before any propagation",field.Sample(new(8,8,8)) == vec3.Zero);
        ok &= Check("outdoor daylight is initialized before propagation",(field.Sample(new(8,8,17))-sky).Length < .00001);
        vec3 newSky = new(.8f,.4f,.2f); field.Configure(newSky,true,true);
        ok &= Check("brightness changes reach all outdoor cells together",(field.Sample(new(8,8,17))-newSky).Length < .00001 && (field.Sample(new(3,3,30))-newSky).Length < .00001);
        ok &= Check("uncached outdoor air has consistent daylight",(field.Sample(new(8,8,80))-newSky).Length < .00001);
        Array.Clear(upper); b = new(new(0,0,1),(byte[])upper.Clone(),[]); field.Set(b,new vec3[4096]);
        ok &= Check("mining exposes daylight immediately despite pending initial work",(field.Sample(new(8,8,8))-newSky).Length < .00001);
        upper[8+16*8] = 255; field.Set(new(new(0,0,1),(byte[])upper.Clone(),[]),new vec3[4096]);
        ok &= Check("placing a roof clears temporary exterior seeds immediately",field.Sample(new(8,8,8)) == vec3.Zero);

        RealtimeLightField dark = new() { GpuSunShadows = true };
        dark.Configure(vec3.Zero,false,false);
        byte[] ceiling = new byte[4096]; Array.Fill(ceiling,(byte)255,256*15,256);
        dark.Set(new(ivec3.Zero,ceiling,[]),new vec3[4096]);
        dark.Step(double.PositiveInfinity,32);
        ok &= Check("unlit empty volume does not consume the propagation frontier",!dark.Pending);

        // A point shadow map must never expose a mixture of old and new faces.
        RealtimeLightField pointField = new(); pointField.Configure(vec3.Zero,true,false);
        byte[] wall = new byte[4096];
        for (int z = 0; z < 16; z++) for (int y = 0; y < 16; y++) wall[10+16*y+256*z] = 255;
        pointField.Set(new(ivec3.Zero,wall,[new(8,8,8)]),new vec3[4096]);
        var point = pointField.PointLights.Single(); float[] initial = (float[])point.Depth.Clone();
        pointField.Step(double.PositiveInfinity,16);
        ok &= Check("partial point-map jobs do not publish dark wedges",point.Next > 0 && point.Depth.AsSpan().SequenceEqual(initial));
        Drain(pointField);
        ok &= Check("complete point-map jobs publish blocked directions",point.Depth[8+16*8] < 2);
        ok &= Check("flat walls have constant cube-face depth, avoiding dark angular bands",point.Depth.Take(256).All(d => Math.Abs(d-1.5f) < .00001f));
        return ok;
    }
    private static bool Run()
    {
        bool ok = Check("texture RGB is linearized and multiplied by mean alpha",
            (LightingScene.AverageReflectance(new byte[] {255,0,0,128,255,0,0,0})-new vec3(128/510f,0,0)).Length < .00001f);
        RealtimeLightField field = new(); field.Configure(vec3.Zero,true,false);
        byte[] masks = new byte[4096];
        vec3[] Colors(byte[] m,vec3 color) => [.. m.Select(mask => mask == 0 ? vec3.Zero : color)];
        ivec3 source = new(8,8,8), near = source+new ivec3(1,0,0), far = source+new ivec3(4,0,0);
        masks[LightGrid.IndexInChunk(source)] = 255;
        field.Set(new(ivec3.Zero,(byte[])masks.Clone(),[source]),Colors(masks,new vec3(.5f)));
        field.Step(double.PositiveInfinity,1);
        ok &= Check("one frame has a bounded cell count and leaves propagation pending",field.Pending);
        Drain(field);
        vec3 lit = field.Sample(far);
        ok &= Check("RGB furnace light reaches nearby air with warm color and falloff",lit.x > lit.y && lit.y > lit.z && lit.x > 0 && field.Sample(near).x > lit.x);
        for (int z = 0; z < 16; z++) for (int y = 0; y < 16; y++) masks[LightGrid.IndexInChunk(new(10,y,z))] = 255;
        field.Set(new(ivec3.Zero,(byte[])masks.Clone(),[source]),Colors(masks,new vec3(.5f))); Drain(field);
        var point = field.PointLights.Single();
        ok &= Check("point-light cubemap records a wall while the opposite face stays open",
            point.Depth[8+16*8] > 1 && point.Depth[8+16*8] < 2 && float.IsPositiveInfinity(point.Depth[256+8+16*8]));
        float beforeRemoval = field.Sample(near).x;
        field.Set(new(ivec3.Zero,(byte[])masks.Clone(),[]),Colors(masks,new vec3(.5f)));
        int visited = field.Step(double.PositiveInfinity,32);
        ok &= Check("extinguishing a source immediately starts removing light",visited <= 32 && field.Sample(near).x < beforeRemoval);
        Drain(field);
        ok &= Check("source removal converges to black",field.Sample(far) == vec3.Zero);

        RealtimeLightField multiple = new(); multiple.Configure(vec3.Zero,true,false);
        multiple.Set(new(ivec3.Zero,(byte[])masks.Clone(),[new(2,2,2),new(12,12,12)]),Colors(masks,new vec3(.5f)));
        multiple.Step(double.PositiveInfinity,16);
        ok &= Check("multiple point shadow maps make progress in the same frame",multiple.PointLights.All(p => p.Next > 0));

        // Fully sealed chamber; opening a wall must produce light without a full rebuild.
        Array.Fill(masks,(byte)255);
        for (int z = 5; z <= 10; z++) for (int y = 5; y <= 10; y++) for (int x = 5; x <= 10; x++)
            masks[LightGrid.IndexInChunk(new(x,y,z))] = 0;
        field.Configure(new vec3(.2f),false,true);
        field.Set(new(ivec3.Zero,(byte[])masks.Clone(),[]),Colors(masks,new vec3(.5f))); Drain(field);
        ok &= Check("sealed chamber receives no ambient or direct sun",field.Sample(new(8,8,8)) == vec3.Zero && field.Sun(new(8,8,8)) == 0);
        for (int z = 11; z < 16; z++) masks[LightGrid.IndexInChunk(new(8,8,z))] = 0;
        field.Set(new(ivec3.Zero,(byte[])masks.Clone(),[]),Colors(masks,new vec3(.5f)));
        field.Step(double.PositiveInfinity,128);
        ok &= Check("mining a shaft lights newly exposed air in the next update",field.Sample(new(8,8,12)).x > 0);
        Drain(field);
        ok &= Check("light propagates through the new opening",field.Sample(new(8,8,8)).x > 0);
        for (int z = 11; z < 16; z++) masks[LightGrid.IndexInChunk(new(8,8,z))] = 255;
        field.Set(new(ivec3.Zero,(byte[])masks.Clone(),[]),Colors(masks,new vec3(.5f))); Drain(field);
        ok &= Check("closing the opening removes stale ambient",field.Sample(new(8,8,8)) == vec3.Zero);

        // Red terrain returns red irradiance from the white sun, even with sky/emission disabled.
        RealtimeLightField reflection = new(); reflection.Configure(vec3.Zero,false,true);
        byte[] floor = new byte[4096];
        for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) floor[x+16*y] = 255;
        reflection.Set(new(ivec3.Zero,floor,[]),Colors(floor,new vec3(1,0,0))); Drain(reflection);
        vec3 bounce = reflection.Sample(new(8,8,1));
        ok &= Check("sunlit red surfaces return red light to the grid",bounce.x > 0 && bounce.y == 0 && bounce.z == 0);
        ok &= Check("direct sun remains available alongside ambient RGB",reflection.Sun(new(8,8,1)) == 1);
        vec3[] transmitting = Colors(floor,new vec3(.25f,0,0));
        reflection.Set(new(ivec3.Zero,new byte[4096],[]),transmitting); Drain(reflection);
        ok &= Check("transmitting terrain can still return colored light",reflection.Sample(new(8,8,1)).x > 0 && reflection.Sample(new(8,8,1)).y == 0);
        reflection.Configure(vec3.Zero,false,false); Drain(reflection);
        ok &= Check("disabling the sun also removes reflected sunlight",reflection.Sample(new(8,8,1)) == vec3.Zero);

        reflection.Remove(ivec3.Zero); Drain(reflection);
        ok &= Check("streaming out terrain releases its light chunks and sources",reflection.CellCount == 0 && !reflection.PointLights.Any());

        Stopwatch watch = Stopwatch.StartNew();
        int cells = reflection.Step(2,128);
        Console.WriteLine($"   settled frame: {watch.Elapsed.TotalMilliseconds:F3} ms, {cells} cells");
        return ok;
    }
}
