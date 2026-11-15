using GlmSharp;

using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace MYT.Kit.Vulkan;


/// <summary>
/// A body that moves and rotates but never deforms, with uniform density over a closed mesh.
/// <para>
/// Before the body is added to a <see cref="PhysicsSystem"/>, its properties are its initial
/// state. Once added, they read the simulated state as of the last step, and setting
/// <see cref="Position"/>, <see cref="Orientation"/> or the velocities teleports or pushes the
/// body at the start of the next step. <see cref="Position"/> places the mesh origin; the
/// velocities are those of the center of mass.
/// </para>
/// </summary>
public sealed class RigidBody : PhysicsBody
{
    // A scheduling hint, never a constraint on the next contact graph. Snapshot refinement
    // and conflict pruning validate it before any concurrent primal writes.
    internal uint SolverColorHint { get; set; }

    /// <summary>
    /// Integrates volume, first and second moments over signed tetrahedra spanned by the origin
    /// and each triangle (divergence theorem). Returns the center of mass and the inertia tensor
    /// about it for unit mass. Winding direction cancels out, but the mesh must be closed or the
    /// volume is meaningless.
    /// </summary>
    private static (dvec3 CenterOfMass, dmat3 UnitInertia) ComputeMassProperties(dvec3[] vertices, uvec3[] triangles)
    {
        double volume = 0;
        dvec3 firstMoment = dvec3.Zero;
        dmat3 secondMoment = dmat3.Zero;

        foreach (uvec3 t in triangles)
        {
            dvec3 a = vertices[t.x];
            dvec3 b = vertices[t.y];
            dvec3 c = vertices[t.z];
            dvec3 sum = a + b + c;
            double det = dvec3.Dot(a, dvec3.Cross(b, c));

            volume += det / 6.0;
            firstMoment += det / 24.0 * sum;
            secondMoment += det / 120.0 * (Outer(a, a) + Outer(b, b) + Outer(c, c) + Outer(sum, sum));
        }

        if (Math.Abs(volume) < 1e-12)
            throw new ArgumentException("Rigid body mesh must be closed to compute its mass properties");

        dvec3 centerOfMass = firstMoment / volume;
        dmat3 covariance = (secondMoment - volume * Outer(centerOfMass, centerOfMass)) / volume;
        double trace = covariance.m00 + covariance.m11 + covariance.m22;
        return (centerOfMass, trace * dmat3.Identity - covariance);
    }

    private static dmat3 Outer(dvec3 a, dvec3 b)
    {
        return new dmat3
        (
            a.x * b.x, a.y * b.x, a.z * b.x,
            a.x * b.y, a.y * b.y, a.z * b.y,
            a.x * b.z, a.y * b.z, a.z * b.z
        );
    }

    private static void ValidateMass(double mass)
    {
        if (!(mass > 0.0) || double.IsInfinity(mass))
            throw new ArgumentOutOfRangeException(nameof(mass), "Mass must be positive and finite");
    }

    /// <param name="geometry">A closed, outward-wound triangle mesh, used for collision and mass properties.</param>
    /// <param name="mass">Mass in kg.</param>
    public RigidBody(Mesh3D geometry, double mass = 1.0)
    {
        ValidateMass(mass);

        Geometry = geometry;
        Triangles = SpringUtility.ExtractSurfaceTriangles(geometry);
        (CenterOfMass, _unitInertia) = ComputeMassProperties(geometry.Vertices, Triangles);
        _mass = mass;
    }

    /// <summary>
    /// A body whose mass is not spread uniformly through its mesh, such as one built from
    /// parts of different materials. The mass properties are taken as given.
    /// </summary>
    /// <param name="geometry">An outward-wound triangle mesh, used only for collision.</param>
    /// <param name="mass">Mass in kg.</param>
    /// <param name="centerOfMass">Center of mass in mesh space.</param>
    /// <param name="inertia">Inertia tensor about the center of mass in mesh axes, kg·m²; symmetric positive definite.</param>
    public RigidBody(Mesh3D geometry, double mass, dvec3 centerOfMass, dmat3 inertia)
    {
        ValidateMass(mass);
        if (!(inertia.m00 > 0.0 && inertia.m11 > 0.0 && inertia.m22 > 0.0))
            throw new ArgumentOutOfRangeException(nameof(inertia), "The inertia tensor must be positive definite");

        Geometry = geometry;
        Triangles = SpringUtility.ExtractSurfaceTriangles(geometry);
        CenterOfMass = centerOfMass;
        _unitInertia = (1.0 / mass) * inertia;
        _mass = mass;
    }

    private dmat3 _unitInertia;
    private double _mass;
    private bool _fixedRotation;
    private double _gravityScale = 1.0;
    private dvec3 _position;
    private dquat _orientation = dquat.Identity;
    private dvec3 _linearVelocity;
    private dvec3 _angularVelocity;
    private Buffer _transformBuffer;
    private DeviceMemory _transformMemory;

    public Mesh3D Geometry { get; private set; }

    /// <summary>Replaces the collision mesh, preserving mass, pose and velocities. Rebuilds the simulation on its next update.</summary>
    public void SetGeometry(Mesh3D geometry)
    {
        uvec3[] triangles = SpringUtility.ExtractSurfaceTriangles(geometry);
        var properties = ComputeMassProperties(geometry.Vertices, triangles);
        Geometry = geometry;
        Triangles = triangles;
        CenterOfMass = properties.CenterOfMass;
        _unitInertia = properties.UnitInertia;
        System?.MarkDirty(this);
        System?.QueueRigidPose(this);
    }

    /// <summary>Mass in kg. Changing it on an added body rebuilds the simulation on the next update.</summary>
    public double Mass
    {
        get => _mass;
        set
        {
            ValidateMass(value);
            _mass = value;
            System?.MarkDirty(this);
        }
    }

    /// <summary>
    /// A body that only translates, such as a character's upright collision box: contacts and
    /// impulses push it but never turn it, and its angular velocity stays zero. Changing it on
    /// an added body rebuilds the simulation on the next update.
    /// </summary>
    public bool FixedRotation
    {
        get => _fixedRotation;
        set
        {
            _fixedRotation = value;
            if (value)
                _angularVelocity = dvec3.Zero;
            System?.MarkDirty(this);
        }
    }

    /// <summary>
    /// How hard gravity pulls the body, as a multiple of <see cref="PhysicsSettings.Gravity"/>:
    /// a character can fall more briskly than the objects around it, and rests on what holds
    /// it up with its weight scaled alike. Changing it on an added body rebuilds the
    /// simulation on the next update.
    /// </summary>
    public double GravityScale
    {
        get => _gravityScale;
        set
        {
            if (!double.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), "The gravity scale must be finite");

            _gravityScale = value;
            System?.MarkDirty(this);
        }
    }

    /// <summary>Center of mass in mesh space.</summary>
    public dvec3 CenterOfMass { get; private set; }

    /// <summary>Inertia tensor about the center of mass, in mesh axes.</summary>
    public dmat3 LocalInertia => _mass * _unitInertia;

    /// <summary>World position of the mesh origin.</summary>
    public dvec3 Position
    {
        get => _position;
        set
        {
            _position = value;
            System?.QueueRigidPose(this);
        }
    }

    public dquat Orientation
    {
        get => _orientation;
        set
        {
            _orientation = value.Normalized;
            System?.QueueRigidPose(this);
        }
    }

    /// <summary>Velocity of the center of mass, m/s.</summary>
    public dvec3 LinearVelocity
    {
        get => _linearVelocity;
        set
        {
            _linearVelocity = value;
            System?.QueueRigidEdit(this, PhysicsEdit.RigidLinearVelocity, new dvec4(value, 0), dvec4.Zero);
        }
    }

    /// <summary>Angular velocity in world axes, rad/s. Always zero with <see cref="FixedRotation"/>.</summary>
    public dvec3 AngularVelocity
    {
        get => _angularVelocity;
        set
        {
            _angularVelocity = _fixedRotation ? dvec3.Zero : value;
            System?.QueueRigidEdit(this, PhysicsEdit.RigidAngularVelocity, new dvec4(value, 0), dvec4.Zero);
        }
    }

    public dvec3 WorldCenterOfMass => _position + _orientation.ToMat3 * CenterOfMass;

    /// <summary>
    /// The model matrix (one float mat4, column-major, mesh space to world) the simulation writes
    /// after every step with <see cref="PhysicsSettings.UpdateRigidTransforms"/> enabled,
    /// ready to bind as a uniform or storage buffer. Valid while the body is in a <see cref="PhysicsSystem"/>.
    /// </summary>
    public Buffer TransformBuffer => _transformBuffer;

    internal uvec3[] Triangles { get; private set; }

    private protected override int SurfaceTriangleCount => Triangles.Length;

    /// <summary>Index among the solver's rigid bodies, or -1 while the body has no GPU state.</summary>
    internal int SolverIndex { get; set; } = -1;

    /// <summary>Applies an impulse in N·s through the center of mass.</summary>
    public void ApplyImpulse(dvec3 impulse)
    {
        ApplyImpulse(impulse, WorldCenterOfMass);
    }

    /// <summary>Applies an impulse in N·s at a world point, which also spins the body.</summary>
    public void ApplyImpulse(dvec3 impulse, dvec3 worldPoint)
    {
        if (System != null && SolverIndex >= 0)
        {
            System.QueueRigidEdit(this, PhysicsEdit.RigidImpulse, new dvec4(impulse, 0), new dvec4(worldPoint, 0));
            return;
        }

        _linearVelocity += impulse / _mass;
        if (_fixedRotation)
            return;

        dmat3 rotation = _orientation.ToMat3;
        dmat3 inverseWorldInertia = rotation * LocalInertia.Inverse * rotation.Transposed;
        _angularVelocity += inverseWorldInertia * dvec3.Cross(worldPoint - WorldCenterOfMass, impulse);
    }

    internal void SetSimulatedState(dvec3 worldCenterOfMass, dquat orientation, dvec3 linearVelocity, dvec3 angularVelocity)
    {
        _orientation = orientation;
        _position = worldCenterOfMass - orientation.ToMat3 * CenterOfMass;
        _linearVelocity = linearVelocity;
        _angularVelocity = angularVelocity;
    }

    internal unsafe void CreateGpuResources(GpuBufferAllocator allocator)
    {
        dmat3 r = _orientation.ToMat3;
        mat4[] initialTransform =
        [
            new mat4
            (
                (float)r.m00, (float)r.m01, (float)r.m02, 0f,
                (float)r.m10, (float)r.m11, (float)r.m12, 0f,
                (float)r.m20, (float)r.m21, (float)r.m22, 0f,
                (float)_position.x, (float)_position.y, (float)_position.z, 1f
            )
        ];

        allocator.CreateDeviceLocal<mat4>
        (
            initialTransform,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.UniformBufferBit,
            out _transformBuffer, out _transformMemory
        );
    }

    internal void DestroyGpuResources(GpuBufferAllocator allocator)
    {
        allocator.Destroy(_transformBuffer, _transformMemory);
        _transformBuffer = default;
        _transformMemory = default;
    }
}
