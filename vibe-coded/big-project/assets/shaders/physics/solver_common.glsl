// Solver state (descriptor set 0) shared by every physics pass.
// Struct layouts must match AvbdSolver.cs.
//
// Precision: absolute positions and orientations are doubles, so large worlds keep their
// detail. Everything relative (displacements, contact geometry, forces, the local solves) is
// float, which consumer GPUs run up to 64 times faster.

const uint NO_BLOCK = 0xFFFFFFFFu;

const uint EDIT_RIGID_POSE = 0u;
const uint EDIT_RIGID_LINEAR_VELOCITY = 1u;
const uint EDIT_RIGID_ANGULAR_VELOCITY = 2u;
const uint EDIT_RIGID_IMPULSE = 3u;
const uint EDIT_NODES_VELOCITY = 4u;
const uint EDIT_NODES_TRANSLATE = 5u;
const uint EDIT_NODES_ADD_VELOCITY = 6u;

struct Node
{
    dvec4 position;          // xyz = current iterate x, w = mass (infinite = pinned)
    dvec4 inertial;          // xyz = inertial target y
    dvec4 previous;          // xyz = x^t, position at the start of the step
    dvec4 velocity;
    dvec4 previousVelocity;  // velocity one step earlier, for adaptive initialization
};

struct Rigid
{
    dvec4 position;              // xyz = center of mass, w = mass
    dvec4 orientation;           // quaternion (x, y, z, w)
    dvec4 linearVelocity;
    dvec4 angularVelocity;       // world frame
    dvec4 inertialPosition;
    dvec4 inertialOrientation;
    dvec4 previousPosition;
    dvec4 previousOrientation;
    dvec4 previousLinearVelocity;
    dvec4 inertia0;              // columns of the body-frame inertia tensor about the center of mass; w = gravity scale
    dvec4 inertia1;              // xyz = inertia column, w = previous solver color hint
    dvec4 inertia2;
    dvec4 centerOfMass;          // xyz = center of mass in mesh space, w = 1 if the rotation is fixed
};

struct Spring
{
    uint node1;
    uint node2;
    float restLength;
    float stiffness;             // infinite = hard distance constraint
    float damping;               // Rayleigh coefficient k_d, seconds
    float durability;            // force at which the spring breaks
};

// A change requested from the CPU, applied to blocks [header.y, header.y + header.z).
struct Edit
{
    uvec4 header;                // x = type, y = first block, z = block count
    dvec4 a;
    dvec4 b;
};

layout(std140, set = 0, binding = 0) uniform SolverParams
{
    dvec4 gravityAndStep;        // xyz = gravity, w = time step h
    dvec4 avbd;                  // x = alpha, y = beta, z = gamma, w = starting stiffness
    dvec4 limits;                // x = max stiffness, y = max force, z = minimum bounce speed, w = max persistent depth
    dvec4 collision;             // x = contact margin, y = penetration tolerance, z = restitution mixing (PhysicsSettings.RestitutionMixing)
    uvec4 counts;                // x = nodes, y = rigid bodies, z = springs, w = iterations
    uvec4 collisionCounts;       // x = collision vertices, y = triangles, z = dynamic triangles, w = bodies
    uvec4 bodyCounts;            // x = dynamic bodies, y = edits, z = rigid colors, w = soft bodies
    uvec4 capacities;            // x = max contacts, y = cache slots (power of two), z = max block contact refs, w = blocks
};

layout(std430, set = 0, binding = 1) buffer Nodes { Node nodes[]; };
layout(std430, set = 0, binding = 2) buffer Rigids { Rigid rigids[]; };
layout(std430, set = 0, binding = 3) buffer Springs { Spring springs[]; };
layout(std430, set = 0, binding = 4) buffer SpringStates { vec4 springStates[]; };    // x = stiffness, y = lambda, z = broken
layout(std430, set = 0, binding = 5) buffer NodeSpringOffsets { uint nodeSpringOffsets[]; };
layout(std430, set = 0, binding = 6) buffer NodeSpringIndices { uint nodeSpringIndices[]; };
layout(std430, set = 0, binding = 7) buffer ColorNodes { uint colorNodes[]; };
layout(std430, set = 0, binding = 8) buffer RigidTransforms { mat4 rigidTransforms[]; };
layout(std430, set = 0, binding = 9) buffer Edits { Edit edits[]; };
layout(std430, set = 0, binding = 10) buffer RigidColors { uint rigidColors[]; };
layout(std430, set = 0, binding = 11) buffer NodeBodies { uint nodeBodies[]; };          // soft body of each node
layout(std430, set = 0, binding = 12) buffer GuessFractions { uint guessFractions[]; };  // per soft body, then per rigid body, 0.32 fixed point
layout(std430, set = 0, binding = 13) buffer RigidWork { uint rigidWork[]; };
layout(std430, set = 0, binding = 14) buffer RigidArguments { uvec4 rigidArguments[]; };

layout(push_constant) uniform DispatchRange
{
    uint rangeOffset;
    uint rangeCount;
    uint iteration;
    uint flags;
};

bool isPinned(Node node)
{
    return isinf(node.position.w);
}

dvec4 quatMul(dvec4 a, dvec4 b)
{
    return dvec4(a.w * b.xyz + b.w * a.xyz + cross(a.xyz, b.xyz), a.w * b.w - dot(a.xyz, b.xyz));
}

dvec4 quatConjugate(dvec4 q)
{
    return dvec4(-q.xyz, q.w);
}

// Float overloads, for rotations applied to small relative vectors.
vec4 quatMul(vec4 a, vec4 b)
{
    return vec4(a.w * b.xyz + b.w * a.xyz + cross(a.xyz, b.xyz), a.w * b.w - dot(a.xyz, b.xyz));
}

vec4 quatConjugate(vec4 q)
{
    return vec4(-q.xyz, q.w);
}

vec3 quatRotate(vec4 q, vec3 v)
{
    vec3 t = 2.0 * cross(q.xyz, v);
    return v + q.w * t + cross(q.xyz, t);
}

mat3 quatToMat3(vec4 q)
{
    float x = q.x;
    float y = q.y;
    float z = q.z;
    float w = q.w;

    return mat3
    (
        1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y + w * z), 2.0 * (x * z - w * y),
        2.0 * (x * y - w * z), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z + w * x),
        2.0 * (x * z + w * y), 2.0 * (y * z - w * x), 1.0 - 2.0 * (x * x + y * y)
    );
}

dmat3 quatToMat3(dvec4 q)
{
    double x = q.x;
    double y = q.y;
    double z = q.z;
    double w = q.w;

    return dmat3
    (
        1.0 - 2.0 * (y * y + z * z), 2.0 * (x * y + w * z), 2.0 * (x * z - w * y),
        2.0 * (x * y - w * z), 1.0 - 2.0 * (x * x + z * z), 2.0 * (y * z + w * x),
        2.0 * (x * z + w * y), 2.0 * (y * z - w * x), 1.0 - 2.0 * (x * x + y * y)
    );
}

// Rotation vector taking b to a for small differences: 2·(a·b⁻¹)_v, via the shorter arc
// (AVBD Eq. 20).
dvec3 quatDifference(dvec4 a, dvec4 b)
{
    dvec4 d = quatMul(a, quatConjugate(b));
    return 2.0 * (d.w < 0.0 ? -d.xyz : d.xyz);
}

// Applies a world-frame rotation vector to q (AVBD Eq. 21).
dvec4 quatIntegrate(dvec4 q, dvec3 rotation)
{
    return normalize(q + 0.5 * quatMul(dvec4(rotation, 0.0), q));
}

mat3 rigidWorldInertia(Rigid body)
{
    mat3 rotation = quatToMat3(vec4(body.orientation));
    mat3 localInertia = mat3(vec3(body.inertia0.xyz), vec3(body.inertia1.xyz), vec3(body.inertia2.xyz));
    return rotation * localInertia * transpose(rotation);
}

// A body that only translates (RigidBody.FixedRotation): nothing turns it.
bool rigidRotationFixed(Rigid body)
{
    return body.centerOfMass.w != 0.0;
}

// Zero for a body whose rotation is fixed, as if its inertia were infinite.
mat3 rigidInverseWorldInertia(Rigid body)
{
    return rigidRotationFixed(body) ? mat3(0.0) : inverse(rigidWorldInertia(body));
}

// VBD Eq. 17: include gravity in the initial guess only as far as the body was actually
// accelerating with it last step, so resting bodies are not started inside what holds them up.
dvec3 adaptiveInitialGuess(dvec3 x, dvec3 v, dvec3 previousVelocity, double h, dvec3 g)
{
    dvec3 guess = x + h * v;
    double gLength = length(g);
    if (gLength < 1e-12)
        return guess;

    double along = dot((v - previousVelocity) / h, g / gLength);
    return guess + h * h * clamp(along / gLength, 0.0, 1.0) * g;
}
