// Shared by every pass of GpuStressLattice: the rigid-body-spring lattice of StressLattice.cs
// on a dense box of cells. Cells are nodes of the box, x fastest; air nodes are skipped and
// keep zero in every vector. Coarser levels halve the box (rounding up), node c of a level
// aggregating nodes 2c .. 2c + 1 of the finer one, down to a single node.
//
// Each node carries six freedoms or six loads, stored as a Six: u.xyz = displacement or
// force, t.xyz = rotation or moment.
//
// Precision. Stone is so stiff that a displacement rounded to float makes a force error the
// size of a block's weight, so the conjugate gradient runs in double: its solution x,
// direction p, product q = K·p and residual r, the operator in its rows, and the stresses. The
// multigrid cycle only preconditions, and runs in float, but the coarser levels' operators
// are summed in double: an aggregate's Galerkin blocks are sums of large terms that nearly
// cancel for its rigid motions, and summed in float the preconditioner stops being positive
// definite. Rounded to float once summed, they are fine. The interfaces' stiffnesses are
// stored in float; every row uses the same values, so the operator stays exactly symmetric.

#define TORSION_FACTOR 0.1406
#define TORSION_SHEAR_FACTOR 0.208
#define SHEAR_PEAK_FACTOR 1.5
#define ANCHOR_FACTOR 1000.0
#define BLOCK 36
#define BLOCKS_PER_NODE 4

// Which vector an exact row multiplies.
#define VECTOR_SOLUTION 0u
#define VECTOR_DIRECTION 1u

struct Six
{
    vec4 u;
    vec4 t;
};

struct DSix
{
    dvec3 u;
    dvec3 t;
};

struct WideSix
{
    dvec4 u;
    dvec4 t;
};

struct CellLoad
{
    uvec4 node;
    vec4 force;
    vec4 moment;
};

// One level of the hierarchy: its box, its index, and where its nodes start in the arrays
// of every level (nodes) and of the levels above the cells (blocks).
struct Level
{
    ivec3 size;
    int index;
    uint nodes;
    uint blocks;
};

struct LevelEntry
{
    ivec4 size;
    uvec4 offsets;
};

layout(push_constant) uniform Pass
{
    ivec4 size;        // xyz = this level's box size, w = this level
    ivec4 coarseSize;  // xyz = the next coarser level's box size, w = color or mode
    uvec4 offsets;     // x = this level's first node, y = the coarser level's, z = this level's first block node, w = the coarser level's
    vec4 parameters;   // xyz = gravity (loads), w = cell size; for counts see each pass
} pass;

// The conjugate gradient's scalars, kept on the GPU between passes.
layout(std430, binding = 0) buffer State
{
    float rz;
    float pq;
    float alpha;
    float beta;
    float rr;
    float bb;
    uint iterations;
    uint padding;
} state;

// Level 0: x = Young's modulus, y = shear modulus, z = mass, w = 0 air, 1 cell, 2 anchored cell.
layout(std430, binding = 1) readonly buffer Cells { vec4 cells[]; };
layout(std430, binding = 2) buffer Solution { WideSix x[]; };
layout(std430, binding = 3) buffer Direction { WideSix p[]; };
layout(std430, binding = 4) buffer Product { WideSix q[]; };
layout(std430, binding = 5) buffer Loads { Six b[]; };
// Every level's right-hand side, correction and residual for the multigrid cycle. Level 0 of
// the right-hand sides is the conjugate gradient's residual r rounded to float, of the
// corrections its preconditioned residual z.
layout(std430, binding = 6) buffer Right { Six right[]; };
layout(std430, binding = 7) buffer Correction { Six correction[]; };
layout(std430, binding = 8) buffer Residual { Six residual[]; };
// Level 0: the inverse of each cell's diagonal block.
layout(std430, binding = 9) buffer Inverses { float inverses[]; };
// Levels 1 and up: each node's diagonal block, then its coupling blocks towards +x, +y and +z.
// The coupling towards −x from node a is the transpose of the one towards +x from its
// neighbour there, and so on.
layout(std430, binding = 10) buffer Blocks { float blocks[]; };
// Levels 1 and up: xyz = the aggregate's reference point, w = its occupied children.
layout(std430, binding = 11) buffer Positions { vec4 positions[]; };
layout(std430, binding = 12) buffer Partials { vec4 partials[]; };
layout(std430, binding = 13) readonly buffer CellLoads { CellLoad cellLoads[]; };
// Level 0: x = tension, y = compression, z = shear, Pa.
layout(std430, binding = 14) buffer Stresses { vec4 stresses[]; };
// Levels 1 and up: the inverse of each node's diagonal block.
layout(std430, binding = 15) buffer CoarseInverses { float coarseInverses[]; };
// Level 0, four per node: the interface to the next node along x, y and z (kn, ks, kb, kt),
// then x = the stiffness of the node's anchor springs.
layout(std430, binding = 16) buffer Bonds { vec4 bonds[]; };
// Every level's box and offsets, for passes that walk several levels.
layout(std430, binding = 17) readonly buffer Levels { LevelEntry levels[]; };
// Level 0: the conjugate gradient's residual r = b − K·x.
layout(std430, binding = 18) buffer CgResidual { WideSix r[]; };
// The node of each cell, in the order the cells were given.
layout(std430, binding = 19) readonly buffer CellNodes { int cellNodes[]; };

const ivec3 DIRECTIONS[6] = ivec3[6](ivec3(1, 0, 0), ivec3(-1, 0, 0), ivec3(0, 1, 0), ivec3(0, -1, 0), ivec3(0, 0, 1), ivec3(0, 0, -1));

int nodeCount(ivec3 size)
{
    return size.x * size.y * size.z;
}

int nodeIndex(ivec3 c, ivec3 size)
{
    return c.x + size.x * (c.y + size.y * c.z);
}

ivec3 nodeCoordinate(int i, ivec3 size)
{
    return ivec3(i % size.x, (i / size.x) % size.y, i / (size.x * size.y));
}

bool inside(ivec3 c, ivec3 size)
{
    return all(greaterThanEqual(c, ivec3(0))) && all(lessThan(c, size));
}

float cellSize()
{
    return pass.parameters.w;
}

Level thisLevel()
{
    return Level(pass.size.xyz, pass.size.w, pass.offsets.x, pass.offsets.z);
}

Level coarserLevel()
{
    return Level(pass.coarseSize.xyz, pass.size.w + 1, pass.offsets.y, pass.offsets.w);
}

Level levelAt(int index)
{
    LevelEntry entry = levels[index];
    return Level(entry.size.xyz, index, entry.offsets.x, entry.offsets.y);
}

Six six(vec3 u, vec3 t)
{
    return Six(vec4(u, 0.0), vec4(t, 0.0));
}

Six zeroSix()
{
    return six(vec3(0.0), vec3(0.0));
}

DSix widen(Six v)
{
    return DSix(dvec3(v.u.xyz), dvec3(v.t.xyz));
}

Six narrow(DSix v)
{
    return six(vec3(v.u), vec3(v.t));
}

// e × v for the unit vector e along an axis.
vec3 axisCross(int axis, vec3 v)
{
    if (axis == 0)
        return vec3(0.0, -v.z, v.y);
    if (axis == 1)
        return vec3(v.z, 0.0, -v.x);
    return vec3(-v.y, v.x, 0.0);
}

dvec3 wideAxisCross(int axis, dvec3 v)
{
    if (axis == 0)
        return dvec3(0.0LF, -v.z, v.y);
    if (axis == 1)
        return dvec3(v.z, 0.0LF, -v.x);
    return dvec3(-v.y, v.x, 0.0LF);
}

// along on the axis component, across on the others.
vec3 stiffen(int axis, vec3 v, float along, float across)
{
    vec3 result = across * v;
    result[axis] = along * v[axis];
    return result;
}

dvec3 wideStiffen(int axis, dvec3 v, double along, double across)
{
    dvec3 result = across * v;
    result[axis] = along * v[axis];
    return result;
}

// The interface between two cells, two half cells in series: kn, ks, kb, kt.
vec4 bond(vec4 a, vec4 b)
{
    float h = cellSize();
    float area = h * h;
    float kn = area / (0.5 * h / a.x + 0.5 * h / b.x);
    float ks = area / (0.5 * h / a.y + 0.5 * h / b.y);
    return vec4(kn, ks, kn * h * h / 12.0, ks * TORSION_FACTOR * h * h);
}

// The stored interface between level-0 node i and its occupied neighbour j in direction d.
vec4 bondBetween(int i, int j, int d)
{
    return bonds[((d & 1) == 0 ? i : j) * 4 + (d >> 1)];
}

float anchorStiffness(int i)
{
    return bonds[i * 4 + 3].x;
}

// Row i of K·v on level 0 in double, for the solution or the direction (StressLattice.FineRow).
DSix exactRow(uint which, int i, ivec3 c, ivec3 size)
{
    double halfCell = 0.5LF * double(cellSize());
    WideSix own = which == VECTOR_SOLUTION ? x[i] : p[i];
    dvec3 ui = own.u.xyz;
    dvec3 ti = own.t.xyz;
    dvec3 force = dvec3(0.0LF);
    dvec3 moment = dvec3(0.0LF);

    for (int d = 0; d < 6; d++)
    {
        ivec3 n = c + DIRECTIONS[d];
        if (!inside(n, size))
            continue;

        int j = nodeIndex(n, size);
        if (cells[j].w == 0.0)
            continue;

        int axis = d >> 1;
        bool lower = (d & 1) == 0;
        dvec4 k = dvec4(bondBetween(i, j, d));
        WideSix other = which == VECTOR_SOLUTION ? x[j] : p[j];

        dvec3 gap = lower ? other.u.xyz - ui : ui - other.u.xyz;
        gap += halfCell * wideAxisCross(axis, ti + other.t.xyz);
        dvec3 twist = lower ? other.t.xyz - ti : ti - other.t.xyz;

        dvec3 f = wideStiffen(axis, gap, k.x, k.y);
        dvec3 m = wideStiffen(axis, twist, k.w, k.z);
        dvec3 arm = halfCell * wideAxisCross(axis, f);
        if (lower)
        {
            force -= f;
            moment -= arm + m;
        }
        else
        {
            force += f;
            moment += m - arm;
        }
    }

    double s = double(anchorStiffness(i));
    force += s * ui;
    moment += s * double(cellSize() * cellSize()) * ti;
    return DSix(force, moment);
}

// Row i of K·correction on level 0 in float, for the multigrid cycle.
Six fineRow(int i, ivec3 c, ivec3 size)
{
    float halfCell = 0.5 * cellSize();
    Six vi = correction[i];
    vec3 force = vec3(0.0);
    vec3 moment = vec3(0.0);

    for (int d = 0; d < 6; d++)
    {
        ivec3 n = c + DIRECTIONS[d];
        if (!inside(n, size))
            continue;

        int j = nodeIndex(n, size);
        if (cells[j].w == 0.0)
            continue;

        int axis = d >> 1;
        bool lower = (d & 1) == 0;
        vec4 k = bondBetween(i, j, d);
        Six vj = correction[j];

        vec3 gap = lower ? vj.u.xyz - vi.u.xyz : vi.u.xyz - vj.u.xyz;
        gap += halfCell * axisCross(axis, vi.t.xyz + vj.t.xyz);
        vec3 twist = lower ? vj.t.xyz - vi.t.xyz : vi.t.xyz - vj.t.xyz;

        vec3 f = stiffen(axis, gap, k.x, k.y);
        vec3 m = stiffen(axis, twist, k.w, k.z);
        vec3 arm = halfCell * axisCross(axis, f);
        if (lower)
        {
            force -= f;
            moment -= arm + m;
        }
        else
        {
            force += f;
            moment += m - arm;
        }
    }

    float s = anchorStiffness(i);
    force += s * vi.u.xyz;
    moment += s * cellSize() * cellSize() * vi.t.xyz;
    return six(force, moment);
}

// blocks[start..]·v, or its transpose's.
Six multiplyBlock(uint start, Six v, bool transposed)
{
    float a[6] = float[6](v.u.x, v.u.y, v.u.z, v.t.x, v.t.y, v.t.z);
    float r[6];
    for (int row = 0; row < 6; row++)
    {
        float sum = 0.0;
        for (int column = 0; column < 6; column++)
            sum += blocks[start + uint(transposed ? column * 6 + row : row * 6 + column)] * a[column];
        r[row] = sum;
    }

    return six(vec3(r[0], r[1], r[2]), vec3(r[3], r[4], r[5]));
}

// Where node i's blocks start on a coarse level.
uint blockStart(Level level, int i)
{
    return (level.blocks + uint(i)) * uint(BLOCKS_PER_NODE * BLOCK);
}

// Where the coupling block from node i to its neighbour j in direction d starts, and whether
// it is stored transposed, as j's coupling towards i.
uint couplingStart(Level level, int i, int j, int d, out bool transposed)
{
    transposed = (d & 1) == 1;
    return blockStart(level, transposed ? j : i) + uint((1 + (d >> 1)) * BLOCK);
}

Six multiplyInverse(uint start, Six v)
{
    float a[6] = float[6](v.u.x, v.u.y, v.u.z, v.t.x, v.t.y, v.t.z);
    float r[6];
    for (int row = 0; row < 6; row++)
    {
        float sum = 0.0;
        for (int column = 0; column < 6; column++)
            sum += inverses[start + uint(row * 6 + column)] * a[column];
        r[row] = sum;
    }

    return six(vec3(r[0], r[1], r[2]), vec3(r[3], r[4], r[5]));
}

Six multiplyCoarseInverse(uint start, Six v)
{
    float a[6] = float[6](v.u.x, v.u.y, v.u.z, v.t.x, v.t.y, v.t.z);
    float r[6];
    for (int row = 0; row < 6; row++)
    {
        float sum = 0.0;
        for (int column = 0; column < 6; column++)
            sum += coarseInverses[start + uint(row * 6 + column)] * a[column];
        r[row] = sum;
    }

    return six(vec3(r[0], r[1], r[2]), vec3(r[3], r[4], r[5]));
}

bool occupiedAt(Level level, int i)
{
    return level.index == 0 ? cells[i].w > 0.0 : positions[level.blocks + uint(i)].w > 0.0;
}

// A node's reference point: a cell's center on level 0, its aggregate's point above.
vec3 positionAt(Level level, int i)
{
    if (level.index == 0)
        return (vec3(nodeCoordinate(i, level.size)) + 0.5) * cellSize();
    return positions[level.blocks + uint(i)].xyz;
}

// Row i of K·correction on a coarse level, from its blocks.
Six coarseRow(Level level, int i, ivec3 c)
{
    Six result = multiplyBlock(blockStart(level, i), correction[level.nodes + uint(i)], false);

    for (int d = 0; d < 6; d++)
    {
        ivec3 n = c + DIRECTIONS[d];
        if (!inside(n, level.size))
            continue;

        int j = nodeIndex(n, level.size);
        if (!occupiedAt(level, j))
            continue;

        bool transposed;
        uint start = couplingStart(level, i, j, d, transposed);
        Six coupled = multiplyBlock(start, correction[level.nodes + uint(j)], transposed);
        result.u += coupled.u;
        result.t += coupled.t;
    }

    return result;
}

// One block Gauss-Seidel step at node i, if it has the color being swept:
// correction += D⁻¹·(right − K·correction).
void smoothNode(Level level, int i, int color)
{
    ivec3 c = nodeCoordinate(i, level.size);
    if (((c.x + c.y + c.z) & 1) != color || !occupiedAt(level, i))
        return;

    uint node = level.nodes + uint(i);
    Six step;
    if (level.index == 0)
    {
        Six row = fineRow(i, c, level.size);
        step = multiplyInverse(uint(i) * uint(BLOCK), Six(right[i].u - row.u, right[i].t - row.t));
    }
    else
    {
        Six row = coarseRow(level, i, c);
        step = multiplyCoarseInverse((level.blocks + uint(i)) * uint(BLOCK), Six(right[node].u - row.u, right[node].t - row.t));
    }

    correction[node].u += step.u;
    correction[node].t += step.t;
}

// residual = right − K·correction at node i.
void residualNode(Level level, int i)
{
    if (!occupiedAt(level, i))
        return;

    uint node = level.nodes + uint(i);
    ivec3 c = nodeCoordinate(i, level.size);
    if (level.index == 0)
    {
        Six row = fineRow(i, c, level.size);
        residual[node] = Six(right[node].u - row.u, right[node].t - row.t);
        return;
    }

    Six row = coarseRow(level, i, c);
    residual[node] = Six(right[node].u - row.u, right[node].t - row.t);
}

// The right-hand side of coarse node a, the finer level's residual gathered from its children
// (forces summed, moments about its reference point), and a zero correction to start from.
void restrictNode(Level fine, Level coarse, int a)
{
    uint node = coarse.nodes + uint(a);
    correction[node] = zeroSix();
    if (!occupiedAt(coarse, a))
    {
        right[node] = zeroSix();
        return;
    }

    ivec3 ca = nodeCoordinate(a, coarse.size);
    vec3 pa = positionAt(coarse, a);
    vec3 force = vec3(0.0);
    vec3 moment = vec3(0.0);
    for (int k = 0; k < 8; k++)
    {
        ivec3 child = 2 * ca + ivec3(k & 1, (k >> 1) & 1, k >> 2);
        if (!inside(child, fine.size))
            continue;

        int i = nodeIndex(child, fine.size);
        if (!occupiedAt(fine, i))
            continue;

        Six r = residual[fine.nodes + uint(i)];
        vec3 offset = positionAt(fine, i) - pa;
        force += r.u.xyz;
        moment += r.t.xyz + cross(offset, r.u.xyz);
    }

    right[node] = six(force, moment);
}

// Adds to fine node i its aggregate's correction, moved rigidly to where it is: U + Θ × r, Θ.
void prolongNode(Level fine, Level coarse, int i)
{
    if (!occupiedAt(fine, i))
        return;

    int a = nodeIndex(nodeCoordinate(i, fine.size) >> 1, coarse.size);
    Six from = correction[coarse.nodes + uint(a)];
    vec3 offset = positionAt(fine, i) - positionAt(coarse, a);

    uint node = fine.nodes + uint(i);
    correction[node].u.xyz += from.u.xyz + cross(from.t.xyz, offset);
    correction[node].t.xyz += from.t.xyz;
}

// Weighted dot product with moments divided by the cell size, so both halves are in newtons.
float scaledDot(Six a, Six b)
{
    float h = cellSize();
    return dot(a.u.xyz, b.u.xyz) + dot(a.t.xyz, b.t.xyz) / (h * h);
}
