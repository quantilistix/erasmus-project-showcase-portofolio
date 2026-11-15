// One 6-DOF AVBD block step per rigid body of the current color (AVBD §4 "Rigid Bodies"):
// position and rotation are solved together, H Δ = f, and applied with AVBD Eq. 21. Bodies of
// one color share no contact unless coloring ran out of colors, so results are written in place.
//
// Two small bodies share a workgroup, with 16 threads apiece. A dense body's workgroup
// keeps 256 threads. Each cluster reduces its own force and Hessian in shared memory.

layout(local_size_x = RIGID_GROUP) in;

#include "solver_common.glsl"
#include "contact_common.glsl"

const uint GROUP = RIGID_GROUP;
const uint CLUSTER = RIGID_CLUSTER;

// The force (6), then the Hessian's upper triangle row by row (21).
const uint TERMS = 27u;
const uint HESSIAN = 6u;

shared float partials[TERMS * GROUP];

void jacobianRow(vec3 direction, vec3 arm, float sign, out float row[6])
{
    vec3 angular = cross(arm, direction);
    row[0] = sign * direction[0];
    row[3] = sign * angular[0];
    row[1] = sign * direction[1];
    row[4] = sign * angular[1];
    row[2] = sign * direction[2];
    row[5] = sign * angular[2];
}

// Upper triangle only; the Hessian is symmetric.
void addOuter(inout float terms[TERMS], float row[6], float scale)
{
    terms[6] += scale * row[0] * row[0];
    terms[7] += scale * row[0] * row[1];
    terms[8] += scale * row[0] * row[2];
    terms[9] += scale * row[0] * row[3];
    terms[10] += scale * row[0] * row[4];
    terms[11] += scale * row[0] * row[5];
    terms[12] += scale * row[1] * row[1];
    terms[13] += scale * row[1] * row[2];
    terms[14] += scale * row[1] * row[3];
    terms[15] += scale * row[1] * row[4];
    terms[16] += scale * row[1] * row[5];
    terms[17] += scale * row[2] * row[2];
    terms[18] += scale * row[2] * row[3];
    terms[19] += scale * row[2] * row[4];
    terms[20] += scale * row[2] * row[5];
    terms[21] += scale * row[3] * row[3];
    terms[22] += scale * row[3] * row[4];
    terms[23] += scale * row[3] * row[5];
    terms[24] += scale * row[4] * row[4];
    terms[25] += scale * row[4] * row[5];
    terms[26] += scale * row[5] * row[5];
}

// With equal tangent stiffnesses, t t^T + b b^T = I - n n^T. The point
// Hessian is (kn-kt) Jn Jn^T + kt B^T B, B = [I, -crossMatrix(arm)].
// B^T B has closed-form blocks I, -crossMatrix(arm), |arm|^2 I-arm arm^T.
// This preserves translation/rotation coupling without assembling two tangent outer products.
void addIsotropicPointHessian(inout float terms[TERMS], vec3 arm, float stiffness)
{
    terms[6] += stiffness;
    terms[12] += stiffness;
    terms[17] += stiffness;
    vec3 scaled = stiffness * arm;
    terms[10] += scaled.z;
    terms[11] -= scaled.y;
    terms[14] -= scaled.z;
    terms[16] += scaled.x;
    terms[18] += scaled.y;
    terms[19] -= scaled.x;
    terms[21] += scaled.y * arm.y + scaled.z * arm.z;
    terms[22] -= scaled.x * arm.y;
    terms[23] -= scaled.x * arm.z;
    terms[24] += scaled.x * arm.x + scaled.z * arm.z;
    terms[25] -= scaled.y * arm.z;
    terms[26] += scaled.x * arm.x + scaled.y * arm.y;
}

void addTranslationHessian(inout float terms[TERMS], vec3 n, vec3 t, vec3 b, float kn, float kt)
{
    // Keep positive outer products when friction is stiffer than the normal. Subtracting
    // a large normal rank-one term from kt I would lose precision in the soft direction.
    if (kt > kn)
    {
        terms[6] += kn * n.x * n.x + kt * (t.x * t.x + b.x * b.x);
        terms[7] += kn * n.x * n.y + kt * (t.x * t.y + b.x * b.y);
        terms[8] += kn * n.x * n.z + kt * (t.x * t.z + b.x * b.z);
        terms[12] += kn * n.y * n.y + kt * (t.y * t.y + b.y * b.y);
        terms[13] += kn * n.y * n.z + kt * (t.y * t.z + b.y * b.z);
        terms[17] += kn * n.z * n.z + kt * (t.z * t.z + b.z * b.z);
        return;
    }
    float difference = kn - kt;
    terms[6] += kt + difference * n.x * n.x;
    terms[7] += difference * n.x * n.y;
    terms[8] += difference * n.x * n.z;
    terms[12] += kt + difference * n.y * n.y;
    terms[13] += difference * n.y * n.z;
    terms[17] += kt + difference * n.z * n.z;
}

vec3 solveLDLT3(float a00, float a01, float a02, float a11, float a12, float a22, vec3 f)
{
    float d0 = a00;
    float l10 = a01 / d0;
    float l20 = a02 / d0;
    float d1 = a11 - l10 * l10 * d0;
    float l21 = (a12 - l20 * l10 * d0) / d1;
    float d2 = a22 - l20 * l20 * d0 - l21 * l21 * d1;
    float y1 = f.y - l10 * f.x;
    float y2 = f.z - l20 * f.x - l21 * y1;
    float x2 = y2 / d2;
    float x1 = y1 / d1 - l21 * x2;
    float x0 = f.x / d0 - l10 * x1 - l20 * x2;
    return vec3(x0, x1, x2);
}

// AVBD §3.5: the contact arm turns with the body, so each row's constraint curves in the
// rotation, with second derivative F·(sym(d rᵀ) - (d·r) I) for force F along direction d at arm r.
// That term can dwarf a light body's rotational inertia; dropping it lets the rotation overshoot,
// keeping it exactly can make the Hessian indefinite. Its diagonal of column norms is neither.
void addGeometricStiffness(inout float terms[TERMS], vec3 direction, vec3 arm, float force)
{
    mat3 g = 0.5 * (outerProduct(direction, arm) + outerProduct(arm, direction)) - dot(direction, arm) * mat3(1.0);

    // Diagonal entries (3, 3), (4, 4), (5, 5) of the upper triangle.
    terms[HESSIAN + 15u] += abs(force) * length(g[0]);
    terms[HESSIAN + 18u] += abs(force) * length(g[1]);
    terms[HESSIAN + 20u] += abs(force) * length(g[2]);
}

// Fixed-size LDLT, in the same arithmetic order as the loop-based solve. Scalar factors
// avoid dynamically indexed private arrays (and their GPU scratch-memory traffic).
void solveLDLT6(float a[36], float b[6], out float x[6])
{
    float d0 = a[0];
    float l10 = a[6];
    l10 /= d0;
    float l20 = a[12];
    l20 /= d0;
    float l30 = a[18];
    l30 /= d0;
    float l40 = a[24];
    l40 /= d0;
    float l50 = a[30];
    l50 /= d0;

    float d1 = a[7];
    d1 -= l10 * l10 * d0;
    float l21 = a[13];
    l21 -= l20 * l10 * d0;
    l21 /= d1;
    float l31 = a[19];
    l31 -= l30 * l10 * d0;
    l31 /= d1;
    float l41 = a[25];
    l41 -= l40 * l10 * d0;
    l41 /= d1;
    float l51 = a[31];
    l51 -= l50 * l10 * d0;
    l51 /= d1;

    float d2 = a[14];
    d2 -= l20 * l20 * d0;
    d2 -= l21 * l21 * d1;
    float l32 = a[20];
    l32 -= l30 * l20 * d0;
    l32 -= l31 * l21 * d1;
    l32 /= d2;
    float l42 = a[26];
    l42 -= l40 * l20 * d0;
    l42 -= l41 * l21 * d1;
    l42 /= d2;
    float l52 = a[32];
    l52 -= l50 * l20 * d0;
    l52 -= l51 * l21 * d1;
    l52 /= d2;

    float d3 = a[21];
    d3 -= l30 * l30 * d0;
    d3 -= l31 * l31 * d1;
    d3 -= l32 * l32 * d2;
    float l43 = a[27];
    l43 -= l40 * l30 * d0;
    l43 -= l41 * l31 * d1;
    l43 -= l42 * l32 * d2;
    l43 /= d3;
    float l53 = a[33];
    l53 -= l50 * l30 * d0;
    l53 -= l51 * l31 * d1;
    l53 -= l52 * l32 * d2;
    l53 /= d3;

    float d4 = a[28];
    d4 -= l40 * l40 * d0;
    d4 -= l41 * l41 * d1;
    d4 -= l42 * l42 * d2;
    d4 -= l43 * l43 * d3;
    float l54 = a[34];
    l54 -= l50 * l40 * d0;
    l54 -= l51 * l41 * d1;
    l54 -= l52 * l42 * d2;
    l54 -= l53 * l43 * d3;
    l54 /= d4;

    float d5 = a[35];
    d5 -= l50 * l50 * d0;
    d5 -= l51 * l51 * d1;
    d5 -= l52 * l52 * d2;
    d5 -= l53 * l53 * d3;
    d5 -= l54 * l54 * d4;

    float y0 = b[0];
    float y1 = b[1];
    y1 -= l10 * y0;
    float y2 = b[2];
    y2 -= l20 * y0;
    y2 -= l21 * y1;
    float y3 = b[3];
    y3 -= l30 * y0;
    y3 -= l31 * y1;
    y3 -= l32 * y2;
    float y4 = b[4];
    y4 -= l40 * y0;
    y4 -= l41 * y1;
    y4 -= l42 * y2;
    y4 -= l43 * y3;
    float y5 = b[5];
    y5 -= l50 * y0;
    y5 -= l51 * y1;
    y5 -= l52 * y2;
    y5 -= l53 * y3;
    y5 -= l54 * y4;
    x[5] = y5 / d5;
    x[4] = y4 / d4;
    x[4] -= l54 * x[5];
    x[3] = y3 / d3;
    x[3] -= l43 * x[4];
    x[3] -= l53 * x[5];
    x[2] = y2 / d2;
    x[2] -= l32 * x[3];
    x[2] -= l42 * x[4];
    x[2] -= l52 * x[5];
    x[1] = y1 / d1;
    x[1] -= l21 * x[2];
    x[1] -= l31 * x[3];
    x[1] -= l41 * x[4];
    x[1] -= l51 * x[5];
    x[0] = y0 / d0;
    x[0] -= l10 * x[1];
    x[0] -= l20 * x[2];
    x[0] -= l30 * x[3];
    x[0] -= l40 * x[4];
    x[0] -= l50 * x[5];
}

// The Newton step H Δ = f of body r, from the contact force and Hessian its cluster summed
// at partials[k · GROUP + thread]: translation in 0-2, rotation in 3-5 (zero for a body whose
// rotation is fixed).
void newtonStep(uint r, uint thread, bool rotationFixed, out float delta[6])
{
    delta = float[6](0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
    Rigid body = rigids[r];

    // Everything below is relative to the current pose, so it is float.
    float h = float(gravityAndStep.w);
    float invH2 = 1.0 / (h * h);
    float mass = float(body.position.w);
    vec3 linearOffset = vec3(body.position.xyz - body.inertialPosition.xyz);
    vec3 linearForce = -mass * invH2 * linearOffset;

    if (rotationFixed)
    {
        vec3 force = vec3(partials[thread], partials[GROUP + thread], partials[2u * GROUP + thread]) + linearForce;
        vec3 step = solveLDLT3
        (
            partials[6u * GROUP + thread] + mass * invH2,
            partials[7u * GROUP + thread], partials[8u * GROUP + thread],
            partials[12u * GROUP + thread] + mass * invH2,
            partials[13u * GROUP + thread],
            partials[17u * GROUP + thread] + mass * invH2, force
        );
        delta[0] = step.x;
        delta[1] = step.y;
        delta[2] = step.z;
        return;
    }

    mat3 worldInertia = rigidWorldInertia(body);

    // x ⊖ y (AVBD Eq. 20)
    vec3 angularOffset = vec3(quatDifference(body.orientation, body.inertialOrientation));
    vec3 angularForce = -invH2 * (worldInertia * angularOffset);

    float hessian[36];
    float force[6];
    force[0] = partials[0u * GROUP + thread];
    force[1] = partials[1u * GROUP + thread];
    force[2] = partials[2u * GROUP + thread];
    force[3] = partials[3u * GROUP + thread];
    force[4] = partials[4u * GROUP + thread];
    force[5] = partials[5u * GROUP + thread];
    hessian[0] = partials[6u * GROUP + thread];
    hessian[1] = hessian[6] = partials[7u * GROUP + thread];
    hessian[2] = hessian[12] = partials[8u * GROUP + thread];
    hessian[3] = hessian[18] = partials[9u * GROUP + thread];
    hessian[4] = hessian[24] = partials[10u * GROUP + thread];
    hessian[5] = hessian[30] = partials[11u * GROUP + thread];
    hessian[7] = partials[12u * GROUP + thread];
    hessian[8] = hessian[13] = partials[13u * GROUP + thread];
    hessian[9] = hessian[19] = partials[14u * GROUP + thread];
    hessian[10] = hessian[25] = partials[15u * GROUP + thread];
    hessian[11] = hessian[31] = partials[16u * GROUP + thread];
    hessian[14] = partials[17u * GROUP + thread];
    hessian[15] = hessian[20] = partials[18u * GROUP + thread];
    hessian[16] = hessian[26] = partials[19u * GROUP + thread];
    hessian[17] = hessian[32] = partials[20u * GROUP + thread];
    hessian[21] = partials[21u * GROUP + thread];
    hessian[22] = hessian[27] = partials[22u * GROUP + thread];
    hessian[23] = hessian[33] = partials[23u * GROUP + thread];
    hessian[28] = partials[24u * GROUP + thread];
    hessian[29] = hessian[34] = partials[25u * GROUP + thread];
    hessian[35] = partials[26u * GROUP + thread];

    hessian[0] += mass * invH2;
    force[0] += linearForce[0];
    force[3] += angularForce[0];
    hessian[21] += worldInertia[0][0] * invH2;
    hessian[22] += worldInertia[1][0] * invH2;
    hessian[23] += worldInertia[2][0] * invH2;
    hessian[7] += mass * invH2;
    force[1] += linearForce[1];
    force[4] += angularForce[1];
    hessian[27] += worldInertia[0][1] * invH2;
    hessian[28] += worldInertia[1][1] * invH2;
    hessian[29] += worldInertia[2][1] * invH2;
    hessian[14] += mass * invH2;
    force[2] += linearForce[2];
    force[5] += angularForce[2];
    hessian[33] += worldInertia[0][2] * invH2;
    hessian[34] += worldInertia[1][2] * invH2;
    hessian[35] += worldInertia[2][2] * invH2;

    solveLDLT6(hessian, force, delta);
}

// The fraction of a Newton step (linearStep, angularStep) of rigid block `block` that contact c
// lets it take. A contact clear of the surface by more than the margin that is not pushing is
// left out of the step's Hessian (contactForces), which therefore cannot stop the step from
// carrying the body far past it: a box landing on one corner swings its other corners
// centimetres into the floor, and the augmented Lagrangian then alternates between pushing them
// out and, with the force released, letting them fall back. The step is cut where the contact
// would close, conservative advancement within the iteration as guess_limit.comp does for the
// initial guess; the next iteration sees it touching.
float stepBound(Contact c, uint block, vec3 linearStep, vec3 angularStep)
{
    vec3 rows = contactConstraint(c);
    if (rows.z <= float(collision.x) || c.penalty.x * rows.z + c.lambda.z < 0.0)
        return 1.0;

    float sign;
    vec3 arm;
    rigidContactArm(c, block, sign, arm);
    float closing = -sign * (dot(c.normal.xyz, linearStep) + dot(cross(arm, c.normal.xyz), angularStep));
    return closing > rows.z ? rows.z / closing : 1.0;
}

void solveBody(uint index)
{
    uint thread = gl_LocalInvocationID.x;
    uint lane = thread % CLUSTER;
    bool hasBody = index < rigidArguments[flags].w;
    uint r = hasBody ? rigidWork[flags * counts.y + index] : 0u;
    uint block = counts.x + r;
    bool rotationFixed = hasBody && rigidRotationFixed(rigids[r]);

    float terms[TERMS];
    terms = float[TERMS](0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0);

    uint end = hasBody ? blockContactEnd(block) : 0u;
    uint begin = hasBody ? blockContactBegin(block) : 0u;
    for (uint ref = begin + lane; ref < end; ref += CLUSTER)
    {
        Contact c = contacts[blockContactRefs[ref]];
        vec3 rows = contactConstraint(c);

        vec3 rowForce;
        float normalStiffness;
        float frictionStiffness;
        contactForces(c, rows, rowForce, normalStiffness, frictionStiffness);

        vec3 n = c.normal.xyz;
        vec3 t;
        vec3 b;
        contactBasis(n, t, b);
        float sign = c.header.y == block ? 1.0 : -1.0;
        vec3 force = -sign * (n * rowForce.z + t * rowForce.x + b * rowForce.y);
        terms[0] += force.x;
        terms[1] += force.y;
        terms[2] += force.z;

        if (rotationFixed)
        {
            addTranslationHessian(terms, n, t, b, normalStiffness, frictionStiffness);
            continue;
        }

        vec3 arm;
        rigidContactArm(c, block, sign, arm);
        vec3 torque = cross(arm, force);
        terms[3] += torque.x;
        terms[4] += torque.y;
        terms[5] += torque.z;

        float normalRow[6];
        jacobianRow(n, arm, sign, normalRow);
        if (frictionStiffness <= normalStiffness)
        {
            addOuter(terms, normalRow, normalStiffness - frictionStiffness);
            if (frictionStiffness > 0.0)
                addIsotropicPointHessian(terms, arm, frictionStiffness);
        }
        else
        {
            // Positive rank-one assembly avoids cancellation in a weak normal direction.
            addOuter(terms, normalRow, normalStiffness);
            float tangentRow[6];
            float bitangentRow[6];
            jacobianRow(t, arm, sign, tangentRow);
            jacobianRow(b, arm, sign, bitangentRow);
            addOuter(terms, tangentRow, frictionStiffness);
            addOuter(terms, bitangentRow, frictionStiffness);
        }
        addGeometricStiffness(terms, n, arm, rowForce.z);

        if (frictionStiffness > 0.0)
        {
            addGeometricStiffness(terms, t, arm, rowForce.x);
            addGeometricStiffness(terms, b, arm, rowForce.y);
        }
    }

    partials[0u * GROUP + thread] = terms[0];
    partials[1u * GROUP + thread] = terms[1];
    partials[2u * GROUP + thread] = terms[2];
    partials[3u * GROUP + thread] = terms[3];
    partials[4u * GROUP + thread] = terms[4];
    partials[5u * GROUP + thread] = terms[5];
    partials[6u * GROUP + thread] = terms[6];
    partials[7u * GROUP + thread] = terms[7];
    partials[8u * GROUP + thread] = terms[8];
    partials[9u * GROUP + thread] = terms[9];
    partials[10u * GROUP + thread] = terms[10];
    partials[11u * GROUP + thread] = terms[11];
    partials[12u * GROUP + thread] = terms[12];
    partials[13u * GROUP + thread] = terms[13];
    partials[14u * GROUP + thread] = terms[14];
    partials[15u * GROUP + thread] = terms[15];
    partials[16u * GROUP + thread] = terms[16];
    partials[17u * GROUP + thread] = terms[17];
    partials[18u * GROUP + thread] = terms[18];
    partials[19u * GROUP + thread] = terms[19];
    partials[20u * GROUP + thread] = terms[20];
    partials[21u * GROUP + thread] = terms[21];
    partials[22u * GROUP + thread] = terms[22];
    partials[23u * GROUP + thread] = terms[23];
    partials[24u * GROUP + thread] = terms[24];
    partials[25u * GROUP + thread] = terms[25];
    partials[26u * GROUP + thread] = terms[26];

    barrier();

    for (uint stride = CLUSTER / 2u; stride > 0u; stride >>= 1u)
    {
        if (lane < stride)
        {
            partials[0u * GROUP + thread] += partials[0u * GROUP + thread + stride];
            partials[1u * GROUP + thread] += partials[1u * GROUP + thread + stride];
            partials[2u * GROUP + thread] += partials[2u * GROUP + thread + stride];
            partials[3u * GROUP + thread] += partials[3u * GROUP + thread + stride];
            partials[4u * GROUP + thread] += partials[4u * GROUP + thread + stride];
            partials[5u * GROUP + thread] += partials[5u * GROUP + thread + stride];
            partials[6u * GROUP + thread] += partials[6u * GROUP + thread + stride];
            partials[7u * GROUP + thread] += partials[7u * GROUP + thread + stride];
            partials[8u * GROUP + thread] += partials[8u * GROUP + thread + stride];
            partials[9u * GROUP + thread] += partials[9u * GROUP + thread + stride];
            partials[10u * GROUP + thread] += partials[10u * GROUP + thread + stride];
            partials[11u * GROUP + thread] += partials[11u * GROUP + thread + stride];
            partials[12u * GROUP + thread] += partials[12u * GROUP + thread + stride];
            partials[13u * GROUP + thread] += partials[13u * GROUP + thread + stride];
            partials[14u * GROUP + thread] += partials[14u * GROUP + thread + stride];
            partials[15u * GROUP + thread] += partials[15u * GROUP + thread + stride];
            partials[16u * GROUP + thread] += partials[16u * GROUP + thread + stride];
            partials[17u * GROUP + thread] += partials[17u * GROUP + thread + stride];
            partials[18u * GROUP + thread] += partials[18u * GROUP + thread + stride];
            partials[19u * GROUP + thread] += partials[19u * GROUP + thread + stride];
            partials[20u * GROUP + thread] += partials[20u * GROUP + thread + stride];
            partials[21u * GROUP + thread] += partials[21u * GROUP + thread + stride];
            partials[22u * GROUP + thread] += partials[22u * GROUP + thread + stride];
            partials[23u * GROUP + thread] += partials[23u * GROUP + thread + stride];
            partials[24u * GROUP + thread] += partials[24u * GROUP + thread + stride];
            partials[25u * GROUP + thread] += partials[25u * GROUP + thread + stride];
            partials[26u * GROUP + thread] += partials[26u * GROUP + thread + stride];
        }

        barrier();
    }

    // The Newton step, on the cluster's first thread, which then shares it.
    float delta[6] = float[6](0.0, 0.0, 0.0, 0.0, 0.0, 0.0);
    if (hasBody && lane == 0u)
    {
        newtonStep(r, thread, rotationFixed, delta);
        partials[0u * GROUP + thread] = delta[0];
        partials[1u * GROUP + thread] = delta[1];
        partials[2u * GROUP + thread] = delta[2];
        partials[3u * GROUP + thread] = delta[3];
        partials[4u * GROUP + thread] = delta[4];
        partials[5u * GROUP + thread] = delta[5];
    }

    barrier();

    uint first = thread - lane;
    vec3 linearStep = vec3(partials[0u * GROUP + first], partials[1u * GROUP + first], partials[2u * GROUP + first]);
    vec3 angularStep = vec3(partials[3u * GROUP + first], partials[4u * GROUP + first], partials[5u * GROUP + first]);
    float bound = 1.0;
    for (uint ref = begin + lane; ref < end; ref += CLUSTER)
        bound = min(bound, stepBound(contacts[blockContactRefs[ref]], block, linearStep, angularStep));

    partials[6u * GROUP + thread] = bound;
    barrier();

    for (uint stride = CLUSTER / 2u; stride > 0u; stride >>= 1u)
    {
        if (lane < stride)
            partials[6u * GROUP + thread] = min(partials[6u * GROUP + thread], partials[6u * GROUP + thread + stride]);

        barrier();
    }

    if (!hasBody || lane != 0u)
        return;

    float scale = partials[6u * GROUP + thread];
    rigids[r].position.xyz += dvec3(scale * linearStep);
    if (!rotationFixed)
        rigids[r].orientation = quatIntegrate(rigids[r].orientation, dvec3(scale * angularStep));
}

void main()
{
#ifdef RIGID_SERIAL
    // A single workgroup visits overflow bodies in index order. All contact reads
    // finish before the next body's write; no in-place neighbours run concurrently.
    for (uint index = 0u; index < rigidArguments[flags].w; index++)
    {
        solveBody(index);
        memoryBarrierBuffer();
        barrier();
    }
#else
    uint index = gl_WorkGroupID.x * (GROUP / CLUSTER) + gl_LocalInvocationID.x / CLUSTER;
    solveBody(index);
#endif
}
