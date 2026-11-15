using GlmSharp;

using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace MYT.Kit.Vulkan;


/// <summary>
/// A deformable body made of point masses (nodes) joined by springs. Its surface triangles,
/// wound counter-clockwise seen from outside, are both what it collides with and what it renders.
/// <para>
/// Before the body is added to a <see cref="PhysicsSystem"/>, the node arrays are its initial
/// state. Once added, <see cref="Positions"/> and <see cref="Velocities"/> read the simulated
/// state as of the last step (while <see cref="SyncToCpu"/> is on), and the methods below push or
/// move the whole body at the start of the next step.
/// </para>
/// </summary>
public sealed class SoftBody : PhysicsBody
{
    // Half of the 26-neighbourhood, so every pair of adjacent lattice nodes gets exactly one spring.
    private static readonly ivec3[] LatticeNeighbourOffsets =
    [
        new(1, 0, 0), new(0, 1, 0), new(0, 0, 1),
        new(1, 1, 0), new(1, -1, 0), new(1, 0, 1), new(1, 0, -1), new(0, 1, 1), new(0, 1, -1),
        new(1, 1, 1), new(1, 1, -1), new(1, -1, 1), new(1, -1, -1)
    ];

    /// <summary>
    /// Builds a solid box: nodes on a regular grid, each joined to its axis, face-diagonal and
    /// body-diagonal neighbours so it resists shear as well as stretch. The outer faces of the
    /// grid form the surface.
    /// </summary>
    /// <param name="stiffness">Spring stiffness in N/m; <see cref="double.PositiveInfinity"/> makes the box rigid-ish and exact.</param>
    /// <param name="damping">Rayleigh damping coefficient in seconds; around 0.001 - 0.01 is typical.</param>
    public static SoftBody CreateLatticeBox
    (
        dvec3 center,
        dvec3 size,
        ivec3 resolution,
        double totalMass,
        double stiffness,
        double damping
    )
    {
        if (resolution.x < 2 || resolution.y < 2 || resolution.z < 2)
            throw new ArgumentOutOfRangeException(nameof(resolution), "A lattice needs at least 2 nodes per axis");

        int[] res = [resolution.x, resolution.y, resolution.z];
        int nodeCount = res[0] * res[1] * res[2];
        dvec3 spacing = size / new dvec3(res[0] - 1, res[1] - 1, res[2] - 1);
        dvec3 origin = center - size / 2.0;

        int Index(int x, int y, int z) => x + res[0] * (y + res[1] * z);

        dvec3[] positions = new dvec3[nodeCount];
        for (int z = 0; z < res[2]; z++)
        {
            for (int y = 0; y < res[1]; y++)
            {
                for (int x = 0; x < res[0]; x++)
                    positions[Index(x, y, z)] = origin + spacing * new dvec3(x, y, z);
            }
        }

        List<Spring> springs = [];
        for (int z = 0; z < res[2]; z++)
        {
            for (int y = 0; y < res[1]; y++)
            {
                for (int x = 0; x < res[0]; x++)
                {
                    foreach (ivec3 offset in LatticeNeighbourOffsets)
                    {
                        int nx = x + offset.x;
                        int ny = y + offset.y;
                        int nz = z + offset.z;
                        if (nx < 0 || ny < 0 || nz < 0 || nx >= res[0] || ny >= res[1] || nz >= res[2])
                            continue;

                        springs.Add(CreateSpring(positions, (uint)Index(x, y, z), (uint)Index(nx, ny, nz), stiffness, damping));
                    }
                }
            }
        }

        List<uvec3> triangles = [];
        for (int axis = 0; axis < 3; axis++)
        {
            // (u, v, axis) is a cyclic permutation of (x, y, z), so e_u × e_v = +e_axis.
            int u = (axis + 1) % 3;
            int v = (axis + 2) % 3;

            for (int side = 0; side < 2; side++)
            {
                int[] cell = new int[3];
                cell[axis] = side == 0 ? 0 : res[axis] - 1;

                uint Corner(int iu, int iv)
                {
                    cell[u] = iu;
                    cell[v] = iv;
                    return (uint)Index(cell[0], cell[1], cell[2]);
                }

                for (int iu = 0; iu < res[u] - 1; iu++)
                {
                    for (int iv = 0; iv < res[v] - 1; iv++)
                    {
                        uint p00 = Corner(iu, iv);
                        uint p10 = Corner(iu + 1, iv);
                        uint p11 = Corner(iu + 1, iv + 1);
                        uint p01 = Corner(iu, iv + 1);

                        if (side == 1)
                        {
                            triangles.Add(new uvec3(p00, p10, p11));
                            triangles.Add(new uvec3(p00, p11, p01));
                        }
                        else
                        {
                            triangles.Add(new uvec3(p00, p11, p10));
                            triangles.Add(new uvec3(p00, p01, p11));
                        }
                    }
                }
            }
        }

        double[] masses = new double[nodeCount];
        Array.Fill(masses, totalMass / nodeCount);

        return new SoftBody(positions, masses, [.. springs], [.. triangles]);
    }

    /// <summary>
    /// Builds a soft body from a closed, outward-wound mesh: one node per distinct vertex plus a
    /// hidden node at the centroid. Springs run along every edge, across every pair of adjacent
    /// triangles (resisting folding) and from the centroid node to every vertex (resisting
    /// collapse). Suits convex or roughly star-shaped meshes; very concave ones sag.
    /// </summary>
    public static SoftBody CreateFromMesh(Mesh3D mesh, double totalMass, double stiffness, double damping)
    {
        (dvec3[] surface, uvec3[] triangles, _) = SpringUtility.Weld(mesh.Vertices, SpringUtility.ExtractSurfaceTriangles(mesh));

        dvec3 centroid = dvec3.Zero;
        foreach (dvec3 p in surface)
            centroid += p;
        centroid /= Math.Max(surface.Length, 1);

        dvec3[] positions = [.. surface, centroid];
        uint center = (uint)surface.Length;

        HashSet<(uint, uint)> pairs = [];
        List<Spring> springs = [];

        void Connect(uint a, uint b)
        {
            if (a == b || !pairs.Add(a < b ? (a, b) : (b, a)))
                return;

            springs.Add(CreateSpring(positions, a, b, stiffness, damping));
        }

        Dictionary<(uint, uint), uint> opposite = [];
        foreach (uvec3 t in triangles)
        {
            uint[] corners = [t.x, t.y, t.z];
            for (int k = 0; k < 3; k++)
            {
                uint a = corners[k];
                uint b = corners[(k + 1) % 3];
                uint c = corners[(k + 2) % 3];
                Connect(a, b);

                (uint, uint) edge = a < b ? (a, b) : (b, a);
                if (opposite.TryGetValue(edge, out uint other))
                    Connect(c, other);
                else
                    opposite[edge] = c;
            }
        }

        for (uint v = 0; v < center; v++)
            Connect(v, center);

        double[] masses = new double[positions.Length];
        Array.Fill(masses, totalMass / positions.Length);

        return new SoftBody(positions, masses, [.. springs], triangles);
    }

    private static Spring CreateSpring(dvec3[] positions, uint a, uint b, double stiffness, double damping)
    {
        return new Spring
        {
            Node1 = a,
            Node2 = b,
            RestLength = (positions[a] - positions[b]).Length,
            Stiffness = stiffness,
            Damping = damping,
            Durability = double.MaxValue
        };
    }

    /// <param name="positions">Initial node positions.</param>
    /// <param name="masses">Node masses in kg; <see cref="double.PositiveInfinity"/> pins a node in place.</param>
    /// <param name="springs">Springs between nodes of this body.</param>
    /// <param name="surfaceTriangles">Outward-wound triangles over the nodes, for collision and rendering.</param>
    public SoftBody(dvec3[] positions, double[] masses, Spring[] springs, uvec3[] surfaceTriangles)
    {
        if (masses.Length != positions.Length)
            throw new ArgumentException("Every node needs a mass", nameof(masses));

        foreach (double mass in masses)
        {
            if (!(mass > 0.0))
                throw new ArgumentOutOfRangeException(nameof(masses), "Node masses must be positive");
        }

        foreach (Spring spring in springs)
        {
            if (spring.Node1 >= positions.Length || spring.Node2 >= positions.Length || spring.Node1 == spring.Node2)
                throw new ArgumentException("A spring must join two different nodes of this body", nameof(springs));
        }

        foreach (uvec3 t in surfaceTriangles)
        {
            if (t.x >= positions.Length || t.y >= positions.Length || t.z >= positions.Length)
                throw new ArgumentException("A surface triangle refers to a node that does not exist", nameof(surfaceTriangles));
        }

        _positions = (dvec3[])positions.Clone();
        _velocities = new dvec3[positions.Length];
        _masses = (double[])masses.Clone();
        _springs = (Spring[])springs.Clone();
        _surfaceTriangles = (uvec3[])surfaceTriangles.Clone();
    }

    private readonly dvec3[] _positions;
    private readonly dvec3[] _velocities;
    private readonly double[] _masses;
    private readonly Spring[] _springs;
    private readonly uvec3[] _surfaceTriangles;

    private Buffer _vertexBuffer;
    private DeviceMemory _vertexMemory;
    private Buffer _indexBuffer;
    private DeviceMemory _indexMemory;
    private Buffer _vertexTriangleOffsetsBuffer;
    private DeviceMemory _vertexTriangleOffsetsMemory;
    private Buffer _vertexTriangleIndicesBuffer;
    private DeviceMemory _vertexTriangleIndicesMemory;

    public int NodeCount => _positions.Length;
    public ReadOnlySpan<dvec3> Positions => _positions;
    public ReadOnlySpan<dvec3> Velocities => _velocities;
    public ReadOnlySpan<double> Masses => _masses;
    public ReadOnlySpan<Spring> Springs => _springs;
    public ReadOnlySpan<uvec3> SurfaceTriangles => _surfaceTriangles;

    private protected override int SurfaceTriangleCount => _surfaceTriangles.Length;

    /// <summary>
    /// Whether <see cref="Positions"/> and <see cref="Velocities"/> are copied back from the GPU
    /// after every step. Turn it off for large bodies only ever drawn from <see cref="VertexBuffer"/>.
    /// </summary>
    public bool SyncToCpu { get; set; } = true;

    /// <summary>Mass-weighted average position of the free nodes, from <see cref="Positions"/>.</summary>
    public dvec3 Center
    {
        get
        {
            dvec3 sum = dvec3.Zero;
            double mass = 0.0;
            for (int i = 0; i < _positions.Length; i++)
            {
                if (double.IsInfinity(_masses[i]))
                    continue;

                sum += _masses[i] * _positions[i];
                mass += _masses[i];
            }

            return mass > 0.0 ? sum / mass : dvec3.Zero;
        }
    }

    /// <summary>
    /// One vertex per node, float3 position then float3 normal (24 bytes), written by the
    /// simulation after every step. Interior nodes get vertices too; the index buffer skips them.
    /// Valid while the body is in a <see cref="PhysicsSystem"/>.
    /// </summary>
    public Buffer VertexBuffer => _vertexBuffer;

    /// <summary>The surface triangles as uint32 indices into <see cref="VertexBuffer"/>.</summary>
    public Buffer IndexBuffer => _indexBuffer;

    public uint IndexCount => (uint)(_surfaceTriangles.Length * 3);

    /// <summary>Index among the solver's soft bodies, or -1 while the body has no GPU state.</summary>
    internal int SolverIndex { get; set; } = -1;

    internal Buffer VertexTriangleOffsetsBuffer => _vertexTriangleOffsetsBuffer;
    internal Buffer VertexTriangleIndicesBuffer => _vertexTriangleIndicesBuffer;

    /// <summary>Pins a node where it is: it keeps its position and still pushes other bodies.</summary>
    public void Pin(int node)
    {
        _masses[node] = double.PositiveInfinity;
        System?.MarkDirty(this);
    }

    /// <summary>Sets the velocity of every free node.</summary>
    public void SetVelocity(dvec3 velocity)
    {
        if (System != null && SolverIndex >= 0)
        {
            System.QueueSoftEdit(this, PhysicsEdit.NodesVelocity, velocity);
            return;
        }

        for (int i = 0; i < _velocities.Length; i++)
            _velocities[i] = double.IsInfinity(_masses[i]) ? dvec3.Zero : velocity;
    }

    /// <summary>Applies an impulse in N·s spread over the free nodes by mass, which changes every node's velocity equally.</summary>
    public void ApplyImpulse(dvec3 impulse)
    {
        double mass = 0.0;
        foreach (double m in _masses)
        {
            if (!double.IsInfinity(m))
                mass += m;
        }

        if (mass <= 0.0)
            return;

        dvec3 change = impulse / mass;
        if (System != null && SolverIndex >= 0)
        {
            System.QueueSoftEdit(this, PhysicsEdit.NodesAddVelocity, change);
            return;
        }

        for (int i = 0; i < _velocities.Length; i++)
        {
            if (!double.IsInfinity(_masses[i]))
                _velocities[i] += change;
        }
    }

    /// <summary>Moves every free node by an offset without giving it velocity.</summary>
    public void Translate(dvec3 offset)
    {
        if (System != null && SolverIndex >= 0)
        {
            System.QueueSoftEdit(this, PhysicsEdit.NodesTranslate, offset);
            return;
        }

        for (int i = 0; i < _positions.Length; i++)
        {
            if (!double.IsInfinity(_masses[i]))
                _positions[i] += offset;
        }
    }

    internal void SetSimulatedNode(int node, dvec3 position, dvec3 velocity)
    {
        _positions[node] = position;
        _velocities[node] = velocity;
    }

    internal void CreateGpuResources(GpuBufferAllocator allocator)
    {
        allocator.CreateDeviceLocal<uvec3>
        (
            _surfaceTriangles,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.IndexBufferBit,
            out _indexBuffer, out _indexMemory
        );

        float[] vertices = new float[_positions.Length * 6];
        for (int i = 0; i < _positions.Length; i++)
        {
            vertices[i * 6] = (float)_positions[i].x;
            vertices[i * 6 + 1] = (float)_positions[i].y;
            vertices[i * 6 + 2] = (float)_positions[i].z;
            vertices[i * 6 + 5] = 1f;
        }

        allocator.CreateDeviceLocal<float>
        (
            vertices,
            BufferUsageFlags.StorageBufferBit | BufferUsageFlags.VertexBufferBit,
            out _vertexBuffer, out _vertexMemory
        );

        (uint[] offsets, uint[] indices) = SpringUtility.VertexTriangleAdjacency(_positions.Length, _surfaceTriangles);
        allocator.CreateDeviceLocal<uint>
        (
            offsets, BufferUsageFlags.StorageBufferBit,
            out _vertexTriangleOffsetsBuffer, out _vertexTriangleOffsetsMemory
        );
        allocator.CreateDeviceLocal<uint>
        (
            indices, BufferUsageFlags.StorageBufferBit,
            out _vertexTriangleIndicesBuffer, out _vertexTriangleIndicesMemory
        );
    }

    internal void DestroyGpuResources(GpuBufferAllocator allocator)
    {
        allocator.Destroy(_indexBuffer, _indexMemory);
        allocator.Destroy(_vertexBuffer, _vertexMemory);
        allocator.Destroy(_vertexTriangleOffsetsBuffer, _vertexTriangleOffsetsMemory);
        allocator.Destroy(_vertexTriangleIndicesBuffer, _vertexTriangleIndicesMemory);

        _indexBuffer = default;
        _indexMemory = default;
        _vertexBuffer = default;
        _vertexMemory = default;
        _vertexTriangleOffsetsBuffer = default;
        _vertexTriangleOffsetsMemory = default;
        _vertexTriangleIndicesBuffer = default;
        _vertexTriangleIndicesMemory = default;
    }
}
