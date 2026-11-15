using System.Diagnostics;
using System.Runtime.InteropServices;
using GlmSharp;
using VoxelGame;

namespace VoxelTests;

internal static class ParticleStreamTests
{
    public static readonly (string Name, Func<bool> Run)[] Scenarios = [("particle-stream", Run)];

    private static bool Run()
    {
        BlockParticles particles = new();
        for (int i = 0; i < 45; i++) particles.EmitExplosion(new dvec3(i % 8, 0, 5));
        Camera camera = new(new(3, -30, 8), new dvec3(0, 1, -.1).Normalized, 1.2, 1.5);
        int[,] layers = new int[Blocks.Count, 7];
        var instances = particles.BuildInstances(camera, layers, 100, 100, 200);
        var reference = particles.BuildVertices(camera, layers, 100, 100, 200);
        bool ok = instances.Length == particles.Count && reference.Length == instances.Length * 6
            && Marshal.SizeOf<ParticleInstance>() == 36;
        for (int i = 0; i < instances.Length; i++)
        {
            var p = instances[i];
            var a = reference[i * 6]; var c = reference[i * 6 + 2];
            ok &= ((a.Position + c.Position) / 2 - p.Position).Length < 1e-5;
            ok &= a.UvLayer == p.UvLayer && a.DustShade == p.DustShade;
            ok &= Math.Abs((reference[i * 6 + 1].Position - a.Position).Length - p.Size) < 1e-5;
        }
        // Camera movement/culling must leave the simulation order unchanged.
        ParticleInstance[] before = instances.ToArray();
        particles.BuildInstances(camera with { Position = new(0, 1000, 0) }, layers, 10, 100, 200);
        ok &= particles.BuildInstances(camera, layers, 100, 100, 200).SequenceEqual(before);
        for (int i = 0; i < 20; i++) particles.BuildInstances(camera, layers, 100, 100, 200);
        Stopwatch watch = Stopwatch.StartNew();
        long allocation = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) particles.BuildInstances(camera, layers, 100, 100, 200);
        allocation = GC.GetAllocatedBytesForCurrentThread() - allocation;
        Console.WriteLine($"   {particles.Count:N0} particles: {watch.Elapsed.TotalMilliseconds / 100:F3} ms/stream, {allocation} bytes allocated over 100 builds; upload {instances.Length * 36:N0} vs {instances.Length * 6 * 32:N0} bytes");
        return ok && allocation == 0;
    }
}
