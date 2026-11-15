// Collision data (descriptor set 1) and contact constraint evaluation.
// Include after solver_common.glsl. Layouts must match GPUCollisionSystem.cs.

const uint KIND_FIXED = 0u;
const uint KIND_NODE = 1u;
const uint KIND_RIGID = 2u;

const uint CONTACT_VERTEX_TRIANGLE = 0u;
const uint CONTACT_EDGE_EDGE = 1u;

const uint CACHE_EMPTY = 0u;
const uint CACHE_LOCKED = 1u;
const uint CACHE_FILLED = 2u;
const uint CACHE_MAX_PROBES = 64u;

const uint BODY_RESET_LAST = 1u;
// The surface goes on past the body's open edges into other bodies (FixedBody.OpenEdgesContinue).
const uint BODY_OPEN_EDGES_CONTINUE = 2u;
// The body has a contact within the margin at the start of this step, so it is not in free
// flight and its inertial prediction is no estimate of its motion (contact_detect.comp).
const uint BODY_TOUCHING = 4u;

// Where a triangle's material sits in its ownership word (CollisionGeometry.MaterialShift).
const uint TRIANGLE_MATERIAL_SHIFT = 8u;

const uint COUNTER_CONTACTS = 0u;
const uint COUNTER_DROPPED_CONTACTS = 1u;
const uint COUNTER_DROPPED_REFS = 2u;
const uint COUNTER_PAIRS = 3u;
const uint COUNTER_PAIR_TOTAL = 4u;
const uint COUNTER_WARM_STARTS = 5u;

// orderedKey of +infinity and -infinity: the empty running bounds of a body.
const uint KEY_POSITIVE_INFINITY = 0xFF800000u;
const uint KEY_NEGATIVE_INFINITY = 0x007FFFFFu;

struct Triangle
{
    uvec4 vertices;   // xyz = collision vertices, w = collision body
    uvec4 edges;      // xyz = edges v0v1, v1v2, v2v0; w = ownership bits (0-2 vertices, 3-5 edges) | material << TRIANGLE_MATERIAL_SHIFT
};

struct Body
{
    uvec4 info;       // x = kind, y = rigid block or NO_BLOCK, z = first triangle, w = triangle count
    uvec4 extra;      // x = BVH root node or NO_BLOCK, y = first vertex, z = vertex count, w = flags
    vec4 boundsMin;   // swept world bounds of the last detection pass, margin included
    vec4 boundsMax;
    uvec4 sweepMin;   // xyz = running bounds of the current pass as orderedKey, reset after use
    uvec4 sweepMax;
    uvec4 features;   // x = feature lifetime handle, y = first edge; zw reserved
};

struct BvhNode
{
    vec4 boundsMin;   // w = bits of the right child (interior) or first primitive (leaf)
    vec4 boundsMax;   // w = bits of the primitive count, 0 for interior nodes (left child = next node)
};

// Side A against side B along n, which points from B to A. Each side is a fixed point, a
// point on a rigid body, or a weighted sum of up to three soft body nodes. Everything here is
// relative to the start of the step, so it is stored in float.
// Rows are ordered (t, b, n); t and b are derived from n with contactBasis.
struct Contact
{
    uvec4 header;     // x = kindA | kindB << 4 | type << 8, y = rigid block A, z = rigid block B, w = cache slot
    uvec4 nodesA;     // xyz = node blocks of side A, NO_BLOCK where unused
    uvec4 nodesB;
    uvec4 key;        // xy = feature pair identifying the contact across steps, zw = bodies of sides A and B (their own material ids)
    vec4 weightsA;    // xyz = node weights of side A, w = friction coefficient
    vec4 weightsB;    // xyz = node weights of side B, w = restitution
    vec4 normal;      // xyz = n, w = signed distance along n at x^t (C0 of the normal row)
    vec4 offsetA;     // xyz = contact point minus A's center of mass at x^t, w = normal velocity at x^t
    vec4 offsetB;
    vec4 lambda;      // xyz = AVBD dual variable per row
    vec4 penalty;     // x = normal row stiffness, y = friction rows stiffness
};

// A contact that pushed during a substep, for the CPU (PhysicsSystem.ContactReports).
struct ContactReport
{
    dvec4 point;      // xyz = world contact point at the start of the substep, on a moving side; w = substep duration
    uvec4 bodies;     // x = body of side A, y = body of side B (material ids)
    vec4 normal;      // xyz = n, from B to A; w = normal impulse on A along n, N·s
    vec4 friction;    // xyz = friction impulse on A, N·s; w = approach speed at the start of the substep, m/s
    vec4 reserved;    // pads the array stride to the dvec4 alignment
};

struct CacheEntry
{
    uvec4 header;     // x = state, yz = current dense feature pair, w = contact index
    uvec4 identity;   // lifetime handle A, local feature A, lifetime handle B, local feature B
    vec4 lambda;
    vec4 penalty;
    vec4 normal;
};

layout(std430, set = 1, binding = 0) buffer VertexLocal { dvec4 vertexLocal[]; };    // xyz: fixed: world, rigid: relative to center of mass; w unused
layout(std430, set = 1, binding = 1) buffer VertexInfo { uvec4 vertexInfo[]; };      // x = body, y = kind, z = block, w = the body's own material id
layout(std430, set = 1, binding = 2) buffer VertexStart { dvec4 vertexStart[]; };    // positions at x^t
layout(std430, set = 1, binding = 3) buffer VertexEnd { dvec4 vertexEnd[]; };        // predicted or current positions
layout(std430, set = 1, binding = 4) buffer VertexLast { dvec4 vertexLast[]; };      // positions at the start of the previous step
layout(std430, set = 1, binding = 5) buffer Triangles { Triangle triangles[]; };
layout(std430, set = 1, binding = 6) buffer Edges { uvec4 edges[]; };                  // xy = vertices, zw = adjacent triangles or NO_BLOCK
layout(std430, set = 1, binding = 7) buffer Bodies { Body bodies[]; };
// bvh_refit.comp reads nodes other invocations of the same dispatch wrote, so it needs them coherent.
#ifndef BVH_NODES_QUALIFIER
#define BVH_NODES_QUALIFIER
#endif

layout(std430, set = 1, binding = 8) BVH_NODES_QUALIFIER buffer BvhNodes { BvhNode bvhNodes[]; };
layout(std430, set = 1, binding = 9) buffer BvhPrimitives { uint bvhPrimitives[]; };
layout(std430, set = 1, binding = 10) buffer Contacts { Contact contacts[]; };
layout(std430, set = 1, binding = 11) buffer Counters { uint counters[]; };
layout(std430, set = 1, binding = 12) buffer BlockContactOffsets { uint blockContactOffsets[]; };
layout(std430, set = 1, binding = 13) buffer BlockContactCursor { uint blockContactCursor[]; };
layout(std430, set = 1, binding = 14) buffer BlockContactRefs { uint blockContactRefs[]; };
layout(std430, set = 1, binding = 15) coherent buffer CacheCurrent { CacheEntry cacheCurrent[]; };
layout(std430, set = 1, binding = 16) buffer CachePrevious { CacheEntry cachePrevious[]; };
layout(std430, set = 1, binding = 17) buffer Materials { dvec4 materials[]; };       // x = friction, y = restitution; the bodies' own, then their surface materials
layout(std430, set = 1, binding = 18) buffer IndirectArgs { uvec4 indirectArgs[]; }; // [0] = per contact, [1] = per candidate pair, [2] = report readback
layout(std430, set = 1, binding = 19) buffer TriangleBounds { vec4 triangleBounds[]; }; // per triangle: swept world lower, upper (rounded outward)
layout(std430, set = 1, binding = 20) buffer Pairs { uvec2 pairs[]; };                 // candidate triangle pairs of the broad phase
layout(std430, set = 1, binding = 21) buffer BvhRefit { uvec4 bvhRefit[]; };           // per soft body BVH node: x = parent or NO_BLOCK, y = children refit this pass, z = body's first triangle
layout(std430, set = 1, binding = 22) buffer ContactReports { uvec4 reportCounts; ContactReport reports[]; }; // reportCounts: x = written, y = dropped
layout(std430, set = 1, binding = 23) buffer ReportReadback { uvec4 readbackReportCounts; ContactReport readbackReports[]; };
#ifndef BODY_BVH_QUALIFIER
#define BODY_BVH_QUALIFIER
#endif
layout(std430, set = 1, binding = 24) BODY_BVH_QUALIFIER buffer BodyBvhNodes { BvhNode bodyBvhNodes[]; };
layout(std430, set = 1, binding = 25) buffer BodyBvhPrimitives { uint bodyBvhPrimitives[]; };
layout(std430, set = 1, binding = 26) buffer BodyBvhRefit { uvec4 bodyBvhRefit[]; }; // parent, arrivals, subtree escape
const uint MAX_BODY_CANDIDATES = 64u;
layout(std430, set = 1, binding = 27) buffer BodyCandidates { uint bodyCandidates[]; };
layout(std430, set = 1, binding = 28) buffer BodyCandidateCounts { uint bodyCandidateCounts[]; }; // > capacity means use the BVH directly

// A uint whose order matches the float's, so atomicMin and atomicMax can merge float bounds.
uint orderedKey(float value)
{
    uint bits = floatBitsToUint(value);
    return (bits & 0x80000000u) != 0u ? ~bits : bits | 0x80000000u;
}

vec3 orderedValue(uvec3 key)
{
    return uintBitsToFloat(mix(~key, key & 0x7FFFFFFFu, notEqual(key & 0x80000000u, uvec3(0u))));
}

bool overlaps(vec3 lowerA, vec3 upperA, vec3 lowerB, vec3 upperB)
{
    return all(lessThanEqual(lowerA, upperB)) && all(lessThanEqual(lowerB, upperA));
}

// Enumerate a dynamic pair from the side with more triangles, or the lower index on a tie.
bool enumeratesAgainst(uint p, uint q)
{
    if (q >= bodyCounts.x)
        return true;
    uint countP = bodies[p].info.w;
    uint countQ = bodies[q].info.w;
    return countP != countQ ? countP > countQ : p < q;
}

uint contactKindA(Contact c)
{
    return c.header.x & 0xFu;
}

uint contactKindB(Contact c)
{
    return (c.header.x >> 4) & 0xFu;
}

uint contactTotal()
{
    return min(counters[COUNTER_CONTACTS], capacities.x);
}

uint pairTotal()
{
    return indirectArgs[1].w;
}

uint blockContactBegin(uint block)
{
    return min(blockContactOffsets[block], capacities.z);
}

uint blockContactEnd(uint block)
{
    return min(blockContactOffsets[block + 1u], capacities.z);
}

// Deterministic tangent frame, so warm-started friction keeps meaning the same directions.
void contactBasis(vec3 n, out vec3 t, out vec3 b)
{
    t = normalize(cross(n, abs(n.x) < 0.9 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0)));
    b = cross(n, t);
}

vec3 nodeDisplacement(uint node)
{
    return vec3(nodes[node].position.xyz - nodes[node].previous.xyz);
}

// Where a lever arm taken at x^t points now, after the body's rotation since then.
vec3 rigidCurrentArm(uint r, vec3 offset)
{
    return quatRotate(quatMul(vec4(rigids[r].orientation), quatConjugate(vec4(rigids[r].previousOrientation))), offset);
}

// Displacement since x^t of a point fixed to rigid body r. The rotation is applied exactly rather
// than linearized: a linear Δθ × r lets a light body appear to move a contact point arbitrarily
// far by spinning, which the solver exploits under hard impacts.
vec3 rigidPointDisplacement(uint r, vec3 offset)
{
    return vec3(rigids[r].position.xyz - rigids[r].previousPosition.xyz) + rigidCurrentArm(r, offset) - offset;
}

vec3 sideDisplacement(uint kind, uint rigidBlock, uvec4 sideNodes, vec4 weights, vec3 offset)
{
    if (kind == KIND_RIGID)
        return rigidPointDisplacement(rigidBlock - counts.x, offset);

    vec3 d = vec3(0.0);
    if (kind == KIND_NODE)
    {
        for (int k = 0; k < 3; k++)
        {
            if (sideNodes[k] != NO_BLOCK)
                d += weights[k] * nodeDisplacement(sideNodes[k]);
        }
    }

    return d;
}

// Constraint rows (t, b, n) linearized at the start of the step (AVBD §4): C = C0 + J·Δx.
// C0 is the signed distance for the normal row and zero for the friction rows, so friction
// resists any tangential slip during the step. Existing penetration is only partly corrected
// per step (AVBD Eq. 18) so it does not turn into a burst of momentum.
vec3 contactConstraint(Contact c)
{
    vec3 d = sideDisplacement(contactKindA(c), c.header.y, c.nodesA, c.weightsA, c.offsetA.xyz)
           - sideDisplacement(contactKindB(c), c.header.z, c.nodesB, c.weightsB, c.offsetB.xyz);

    vec3 t;
    vec3 b;
    contactBasis(c.normal.xyz, t, b);

    float c0 = c.normal.w;
    if (c0 < 0.0)
        c0 *= float(1.0 - avbd.x);

    return vec3(dot(t, d), dot(b, d), c0 + dot(c.normal.xyz, d));
}

// AVBD row forces F (positive along each row's gradient pulls the sides together) and the
// stiffness the block Hessian should use. The normal force is clamped to push only; friction
// is clamped to the Coulomb cone of the current normal force (AVBD §3.2-3.3).
void contactForces(Contact c, vec3 rows, out vec3 force, out float normalStiffness, out float frictionStiffness)
{
    float kn = c.penalty.x;
    float kt = c.penalty.y;

    float normal = kn * rows.z + c.lambda.z;
    if (normal < 0.0)
    {
        force.z = normal;
        normalStiffness = kn;
    }
    else if (rows.z > float(collision.x))
    {
        // Clamped and clear of the surface: stiffness rescaling (AVBD Eq. 14), zero for a
        // speculative contact that has never pushed, so bodies approaching it are not slowed.
        force.z = 0.0;
        normalStiffness = clamp(-c.lambda.z / rows.z, 0.0, kn);
    }
    else
    {
        // Clamped but touching: the force switches on just ahead, and a step blind to it
        // would overshoot through the surface and oscillate back.
        force.z = 0.0;
        normalStiffness = kn;
    }

    float limit = -c.weightsA.w * force.z;
    if (limit <= 0.0)
    {
        force.xy = vec2(0.0);
        frictionStiffness = 0.0;
        return;
    }

    vec2 friction = kt * rows.xy + c.lambda.xy;
    float frictionLength = length(friction);
    if (frictionLength > limit)
        friction *= limit / frictionLength;

    force.xy = friction;
    frictionStiffness = kt;
}

// For a node block of this contact, w such that every row's Jacobian is w · direction.
float nodeContactWeight(Contact c, uint node)
{
    float w = 0.0;
    for (int k = 0; k < 3; k++)
    {
        if (c.nodesA[k] == node)
            w += c.weightsA[k];
        if (c.nodesB[k] == node)
            w -= c.weightsB[k];
    }

    return w;
}

// For a rigid block of this contact, the sign and current lever arm such that every row's
// Jacobian is sign · [direction; arm × direction].
void rigidContactArm(Contact c, uint block, out float sign, out vec3 arm)
{
    uint r = block - counts.x;
    if (contactKindA(c) == KIND_RIGID && c.header.y == block)
    {
        sign = 1.0;
        arm = rigidCurrentArm(r, c.offsetA.xyz);
    }
    else
    {
        sign = -1.0;
        arm = rigidCurrentArm(r, c.offsetB.xyz);
    }
}
