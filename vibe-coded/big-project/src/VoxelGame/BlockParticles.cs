using System.Runtime.InteropServices;

using GlmSharp;

using MYT.Kit.Vulkan;

namespace VoxelGame;


/// <summary>The point and outward normal where damage occurs, in the voxel body's frame (m).</summary>
internal readonly record struct BlockDamageSite(dvec3 Point, dvec3 Normal);

[StructLayout(LayoutKind.Sequential)]
internal struct ParticleVertex(vec3 position, vec3 uvLayer, float opacity, float dustShade)
{
    public vec3 Position = position;
    public vec3 UvLayer = uvLayer;
    public float Opacity = opacity;
    public float DustShade = dustShade;
}

/// <summary>One GPU input record per particle; the GPU expands the billboard.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ParticleInstance(vec3 position, float size, vec3 uvLayer, float uvSize, float dustShade)
{
    public vec3 Position = position;
    public float Size = size;
    public vec3 UvLayer = uvLayer;
    public float UvSize = uvSize;
    public float DustShade = dustShade;
}

/// <summary>Camera-facing block chips, animated explosion sprites and dust.</summary>
internal sealed class BlockParticles
{
    public const int MaxParticles = 32768;
    public const int TexturePixels = 16;
    public const int DustFrames = 5;
    public const int ExplosionFrames = 7;
    private const int FlashFrames = 2;
    public const int ChipPixels = 2;
    public const double ChipSize = VoxelBody.CellSize * ChipPixels / TexturePixels;
    // Full 16x16 effect sprites use the same world-space pixel size as block chips.
    public const double EffectSize = ChipSize * TexturePixels / ChipPixels;
    internal const double AirDensity = 1.225; // kg/m³
    // Effective cloud densities include the entrained warm air, rather than solid grains.
    private const double PuffDensity = 0.45;
    /// <summary>Total chips shed while any intact block takes all of its breaking damage.</summary>
    public const int ParticlesPerBlock = 32;

    internal record struct Particle
    {
        public dvec3 Position;
        public dvec3 Velocity;
        public double Remaining;
        public double Lifetime;
        public Voxel Voxel;
        public int Face;
        public ivec2 Pixel;
        public int Effect; // 0 = texture chip, 1 = dust, 2 = blast puff
        public double Size;
        public double Mass;
        public float DustShade; // Zero preserves the original texture color for chips and blast puffs.
    }

    private readonly List<Particle> _particles = [];
    private readonly ParticleInstance[] _instances = new ParticleInstance[MaxParticles];
    private ParticleVertex[]? _vertices;
    private readonly ParticleRaycaster _raycaster = new();

    private bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) _particles.Clear();
        }
    }

    public int Count => _particles.Count;

    internal Particle[] Capture() => [.. _particles];
    internal (dvec3 Position, int Frames)[] CaptureFlashes() => [.. _flashes];

    internal void Restore(Particle[] particles, (dvec3 Position, int Frames)[] flashes)
    {
        _particles.Clear();
        if (Enabled) _particles.AddRange(particles);
        _flashes.Clear();
        _flashes.AddRange(flashes);
    }

    public void Emit(VoxelBody body, ivec3 cell, BlockType type, int count, bool broken, BlockDamageSite? site = null)
    {
        if (!Enabled) return;
        count = Math.Clamp(count, 0, MaxParticles);
        if (count == 0)
            return;

        int face = 0;
        if (site is BlockDamageSite given && given.Normal.LengthSqr > 0.0)
        {
            double best = double.NegativeInfinity;
            for (int i = 0; i < 6; i++)
            {
                double dot = dvec3.Dot(given.Normal, new dvec3(Grid3D<Voxel>.FaceDirections[i]));
                if (dot > best)
                {
                    best = dot;
                    face = i;
                }
            }
        }
        else
        {
            // Stress can fail a cell away from a contact. Prefer a face that can shed visible chips.
            int start = Random.Shared.Next(6);
            for (int i = 0; i < 6; i++)
            {
                face = (start + i) % 6;
                if (!body.Grid.IsOccupied(cell + Grid3D<Voxel>.FaceDirections[face]))
                    break;
            }
        }

        dvec3 localNormal = new(Grid3D<Voxel>.FaceDirections[face]);
        dvec3 localPoint = site?.Point ?? VoxelBody.CellCenter(cell) + 0.5 * VoxelBody.CellSize * localNormal;
        // Clamp contact points on edges into this cell before scattering across its face.
        dvec3 low = new dvec3(cell) * VoxelBody.CellSize;
        localPoint = dvec3.Clamp(localPoint, low, low + VoxelBody.CellSize);
        dvec3 normal = body.Orientation * localNormal;
        dvec3 tangent = dvec3.Cross(normal, Math.Abs(normal.z) < 0.9 ? dvec3.UnitZ : dvec3.UnitY).Normalized;
        dvec3 up = dvec3.Cross(normal, tangent);
        dvec3 point = body.ToWorld(localPoint);
        dvec3 center = body.Rigid?.WorldCenterOfMass ?? body.ToWorld(VoxelBody.CellCenter(cell));
        dvec3 inherited = body.LinearVelocity + dvec3.Cross(body.AngularVelocity, point - center);
        int overflow = _particles.Count + count - MaxParticles;
        if (overflow > 0)
            _particles.RemoveRange(0, overflow);

        for (int i = 0; i < count; i++)
        {
            double spread = broken ? 0.22 : 0.10;
            _particles.Add(new Particle
            {
                Position = point + (0.04 + Random.Shared.NextDouble() * 0.04) * normal
                    + spread * (Signed() * tangent + Signed() * up),
                Velocity = inherited + (1.0 + Random.Shared.NextDouble() * 2.5) * normal
                    + (broken ? 2.8 : 1.8) * (Signed() * tangent + Signed() * up),
                Remaining = 1.0 + Random.Shared.NextDouble(),
                Mass = Blocks.Get(type).Density * Math.Pow(ChipSize, 3),
                Voxel = body.Grid[cell] with { Type = type },
                Face = face,
                Pixel = new ivec2(Random.Shared.Next(TexturePixels - ChipPixels + 1), Random.Shared.Next(TexturePixels - ChipPixels + 1))
            });
        }
    }

    private readonly List<(dvec3 Position, int Frames)> _flashes = [];

    /// <summary>Four simultaneous warm flashes, lasting two rendered frames with a quadratic fade regardless of timestep.</summary>
    public void ReadFlashes(Span<vec4> lights)
    {
        lights.Clear();
        for (int i = _flashes.Count - 1; i >= 0; i--)
        {
            var flash = _flashes[i];
            float strength = flash.Frames / (float)FlashFrames;
            lights[i] = new vec4((vec3)flash.Position, strength * strength);
            if (flash.Frames == 1) _flashes.RemoveAt(i);
            else _flashes[i] = (flash.Position, flash.Frames - 1);
        }
    }

    public void ApplyPressure(BlastPressure pressure, double dt)
    {
        for (int i = 0; i < _particles.Count; i++)
        {
            Particle particle = _particles[i];
            BlastPressure.Load load = pressure.Sample(particle.Position);
            if (load.Pressure < BlastPressure.Threshold) continue;
            double edge = particle.Effect == 0 ? ChipSize : particle.Size;
            // Dust/puffs are porous clouds, not solid one-square-metre plates.
            double coupling = particle.Effect switch { 0 => 1, 1 => .02, _ => .005 };
            particle.Velocity += load.Momentum(edge * edge * coupling, dt) / particle.Mass;
            _particles[i] = particle;
        }
    }

    internal void AppendPressurePositions(List<dvec3> positions)
    {
        foreach (Particle particle in _particles) positions.Add(particle.Position);
    }

    internal void ApplyPressure(ReadOnlySpan<BlastPressure.Load> loads, double dt)
    {
        if (loads.Length != _particles.Count) throw new ArgumentException("One pressure sample per particle is required", nameof(loads));
        for (int i = 0; i < _particles.Count; i++)
        {
            BlastPressure.Load load = loads[i];
            if (load.Pressure < BlastPressure.Threshold) continue;
            Particle particle = _particles[i];
            double edge = particle.Effect == 0 ? ChipSize : particle.Size;
            double coupling = particle.Effect switch { 0 => 1, 1 => .02, _ => .005 };
            particle.Velocity += load.Momentum(edge * edge * coupling, dt) / particle.Mass;
            _particles[i] = particle;
        }
    }

    public void EmitExplosion(dvec3 center, World? world = null)
    {
        if (_flashes.Count == 4) _flashes.RemoveAt(0);
        _flashes.Add((center, FlashFrames));
        if (!Enabled) return;
        if (world != null) _raycaster.Prepare(world);
        const int dust = 720, puffs = 16;
        int overflow = _particles.Count + dust + puffs - MaxParticles;
        if (overflow > 0) _particles.RemoveRange(0, overflow);
        for (int i = 0; i < dust + puffs; i++)
        {
            double z = Signed(), angle = Random.Shared.NextDouble() * Math.Tau;
            double ring = Math.Sqrt(1 - z * z);
            dvec3 direction = new(ring * Math.Cos(angle), ring * Math.Sin(angle), z);
            bool puff = i >= dust;
            double lifetime = puff ? 0.25 + Random.Shared.NextDouble() * 0.2 : 1 + Random.Shared.NextDouble() * 4;
            double distance = puff ? Random.Shared.NextDouble() * 1.1 : .35 + Random.Shared.NextDouble() * .45;
            if (world != null && _raycaster.Raycast(center, direction, distance) is VoxelHit wall)
            {
                if (wall.Normal == ivec3.Zero) continue;
                // Never emit through a thin wall or place a particle inside its material.
                distance = Math.Max(0, wall.Distance - .008);
            }
            _particles.Add(new Particle
            {
                // Spread dust through the source region so it samples the outward front,
                // rather than sitting almost entirely inside its isotropic center cell.
                Position = center + direction * distance,
                Velocity = dvec3.Zero,
                Remaining = lifetime,
                Lifetime = lifetime,
                Effect = puff ? 2 : 1,
                Size = EffectSize,
                Mass = (puff ? PuffDensity : DustMass(Random.Shared.NextDouble())) * Math.Pow(EffectSize, 3),
                DustShade = puff ? 0 : 0.15f + Random.Shared.NextSingle() * 0.35f
            });
        }
    }

    /// <summary>Continuous cloud mass distribution: mostly heavy clouds, a sparse light tail.</summary>
    internal static double DustMass(double quantile) => .1 + 1.05 * Math.Sqrt(quantile);

    private static double Signed() => Random.Shared.NextDouble() * 2.0 - 1.0;

    /// <summary>Integrates linear air resistance and displaced-air buoyancy in still air.</summary>
    internal static dvec3 AirMotion(ref dvec3 velocity, double mass, double volume, double area, dvec3 gravity, double dt)
    {
        // F_buoyancy = -rho_air * volume * gravity; F_drag = -k * velocity.
        // A reference flow speed folds into k for a cheap linear drag approximation.
        double rate = 3.0 * AirDensity * area / mass;
        dvec3 acceleration = gravity * (1 - AirDensity * volume / mass);
        dvec3 terminal = acceleration / rate;
        // Solve analytically so light particles cannot reverse or explode at large timesteps.
        double decay = Math.Exp(-rate * dt);
        double response = rate * dt < 1e-5 ? dt * (1 - rate * dt / 2) : (1 - decay) / rate;
        dvec3 travel = terminal * dt + (velocity - terminal) * response;
        velocity = terminal + (velocity - terminal) * decay;
        return travel;
    }

    public void Update(World world, double dt)
    {
        if (!(dt > 0.0) || _particles.Count == 0)
            return;

        _raycaster.Prepare(world);

        int retained = 0;
        for (int i = 0; i < _particles.Count; i++)
        {
            Particle particle = _particles[i];
            particle.Remaining -= dt;
            if (particle.Remaining <= 0.0)
                continue;

            double edge = particle.Effect == 0 ? ChipSize : particle.Size;
            dvec3 travel = AirMotion(ref particle.Velocity, particle.Mass, Math.Pow(edge, 3),
                edge * edge, world.Physics.Settings.Gravity, dt);
            VoxelHit? collision = _raycaster.Raycast(particle.Position, travel, travel.Length);
            if (collision is VoxelHit hit)
            {
                // Geometry may have moved into the particle since the last update. An
                // inside-solid hit has no entering face; treating it as air lets it leak out.
                if (hit.Normal == ivec3.Zero) continue;
                dvec3 normal = hit.Body.Orientation * new dvec3(hit.Normal);
                particle.Position += travel.Normalized * hit.Distance + normal * 0.008;
                double inward = dvec3.Dot(particle.Velocity, normal);
                if (inward < 0.0)
                    particle.Velocity = 0.6 * (particle.Velocity - 1.25 * inward * normal);
            }
            else
            {
                particle.Position += travel;
            }
            _particles[retained++] = particle;
        }
        // Compact once: removing expired particles one by one becomes quadratic at this capacity.
        _particles.RemoveRange(retained, _particles.Count - retained);
    }

    /// <summary>One compact record per visible particle, in one opaque cutout batch.</summary>
    public ReadOnlySpan<ParticleInstance> BuildInstances(Camera camera, int[,] layers, double viewDistance, int dustFirstLayer = 0, int explosionFirstLayer = 0)
    {
        Frustum frustum = new(camera);
        double distanceSquared = viewDistance * viewDistance;
        int count = 0;
        // Cutouts write depth, so they need no back-to-front sorting. Keep simulation
        // storage in place and upload each visible particle only once.
        foreach (Particle particle in _particles)
        {
            double edge = particle.Effect == 0 ? ChipSize : particle.Size;
            if ((particle.Position - camera.Position).LengthSqr > distanceSquared || !frustum.CanSee(particle.Position, edge))
                continue;
            vec2 uv = particle.Effect == 0 ? new vec2(particle.Pixel) / TexturePixels : vec2.Zero;
            float size = particle.Effect == 0 ? ChipPixels / (float)TexturePixels : 1;
            float layer = particle.Effect == 0 ? particle.Voxel.TextureLayer(layers, particle.Face) : 0;
            if (particle.Effect != 0)
            {
                int frames = particle.Effect == 1 ? DustFrames : ExplosionFrames;
                int frame = Math.Clamp((int)((1 - particle.Remaining / particle.Lifetime) * frames), 0, frames - 1);
                layer = particle.Effect == 1 ? dustFirstLayer + frame : -(explosionFirstLayer + frame + 1);
            }
            _instances[count++] = new((vec3)particle.Position, (float)edge, new vec3(uv, layer), size, particle.DustShade);
        }
        return _instances.AsSpan(0, count);
    }

    /// <summary>CPU reference expansion for geometry/physics checks; unused by rendering.</summary>
    public ReadOnlySpan<ParticleVertex> BuildVertices(Camera camera, int[,] layers, double viewDistance, int dustFirstLayer = 0, int explosionFirstLayer = 0)
    {
        var instances = BuildInstances(camera, layers, viewDistance, dustFirstLayer, explosionFirstLayer);
        _vertices ??= new ParticleVertex[MaxParticles * 6];
        vec3 cameraRight = (vec3)dvec3.Cross(camera.Forward, dvec3.UnitZ).Normalized;
        vec3 cameraUp = (vec3)dvec3.Cross((dvec3)cameraRight, camera.Forward);
        int count = 0;
        foreach (var p in instances)
        {
            vec3 right = cameraRight * (p.Size / 2), up = cameraUp * (p.Size / 2);
            vec3 uv = p.UvLayer;
            ParticleVertex a = new(p.Position - right - up, uv, 1, p.DustShade);
            ParticleVertex b = new(p.Position + right - up, uv + new vec3(p.UvSize, 0, 0), 1, p.DustShade);
            ParticleVertex c = new(p.Position + right + up, uv + new vec3(p.UvSize, p.UvSize, 0), 1, p.DustShade);
            ParticleVertex d = new(p.Position - right + up, uv + new vec3(0, p.UvSize, 0), 1, p.DustShade);
            _vertices[count++] = a; _vertices[count++] = b; _vertices[count++] = c;
            _vertices[count++] = a; _vertices[count++] = c; _vertices[count++] = d;
        }
        return _vertices.AsSpan(0, count);
    }
}
