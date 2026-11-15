#version 450
#extension GL_GOOGLE_include_directive : require

// Chunk faces: four vertices per face, each two uints packed by VoxelMesher.BuildRenderMesh.

#include "frame.glsl"

layout(location = 0) in uvec2 packedVertex;

struct VoxelDraw { mat4 model; ivec4 chunk; vec4 clipMin; vec4 clipMax; };
layout(std430, set = 0, binding = 3) readonly buffer VoxelDraws { VoxelDraw draws[]; };

layout(location = 0) out vec3 uvLayer;
layout(location = 1) out vec3 worldPosition;
layout(location = 2) flat out vec3 normal;
layout(location = 3) out float occlusion;
layout(location = 4) flat out uint cell;
layout(location = 5) out vec3 cellPosition;
layout(location = 6) flat out int voxelSlot;
layout(location = 7) flat out float voxelScale;

const vec3 Normals[6] = vec3[](vec3(1, 0, 0), vec3(-1, 0, 0), vec3(0, 1, 0), vec3(0, -1, 0), vec3(0, 0, 1), vec3(0, 0, -1));
const vec2 Corners[4] = vec2[](vec2(0, 1), vec2(1, 1), vec2(1, 0), vec2(0, 0));
const float Occlusion[4] = float[](0.42, 0.62, 0.8, 1.0);

layout(location=8) flat out vec4 fragmentClipMin;
layout(location=9) flat out vec4 fragmentClipMax;

void main()
{
    VoxelDraw draw = draws[gl_InstanceIndex];
    fragmentClipMin=draw.clipMin; fragmentClipMax=draw.clipMax;
    voxelSlot = max(draw.chunk.w, 0);
    voxelScale = draw.chunk.w < 0 ? length(mat3(draw.model) * vec3(1,0,0)) : 1.0;
    uint a = packedVertex.x;
    uint b = packedVertex.y;
    vec3 point = vec3(a & 31u, (a >> 5) & 31u, (a >> 10) & 31u);
    point += 0.5 * vec3((a >> 25) & 1u, (a >> 26) & 1u, (a >> 27) & 1u);
    bool shaped = (a & (1u << 29)) != 0u;
    if (shaped)
    {
        for (int axis = 0; axis < 3; axis++)
            if ((a & (1u << (20 + axis))) != 0u)
                point[axis] += ((a & (1u << (25 + axis))) != 0u) ? 0.3125 : 0.1875;
    }
    vec3 texturePoint = point;
    vec3 local = point + vec3(draw.chunk.xyz);
    uint face = (a >> 15) & 7u;

    vec4 world = draw.model * vec4(local, 1.0);
    vec3 offset = world.xyz - cameraPosition.xyz;
    gl_Position = draw.chunk.w < 0 && max(abs(offset.x), abs(offset.y)) >= 524288.0
        ? viewProjection * vec4(offset.xy, 0.0, 0.0) : viewProjection * world;
    uvLayer = vec3(Corners[(a >> 20) & 3u], float(b & 255u));
    bool merged = !shaped && (a & (1u << 22)) != 0u;
    if (merged)
    {
        // Same orientation as each original unit face, repeated once per block.
        vec2 uv = face == 0u ? vec2(point.y, 1.0 - point.z)
                : face == 1u ? vec2(-point.y, 1.0 - point.z)
                : face == 2u ? vec2(-point.x, 1.0 - point.z)
                : face == 3u ? vec2(point.x, 1.0 - point.z)
                : face == 4u ? vec2(point.x, 1.0 - point.y)
                             : vec2(-point.x, 1.0 - point.y);
        uvLayer.xy = uv;
    }
    if ((a & (1u << 28)) != 0u)
    {
        uint index = (b >> 8) & 4095u;
        vec3 p = texturePoint - vec3(index & 15u, (index >> 4) & 15u, (index >> 8) & 15u);
        uvLayer.xy = face == 0u ? vec2(p.y, 1.0 - p.z)
                   : face == 1u ? vec2(1.0 - p.y, 1.0 - p.z)
                   : face == 2u ? vec2(1.0 - p.x, 1.0 - p.z)
                   : face == 3u ? vec2(p.x, 1.0 - p.z)
                   : face == 4u ? vec2(p.x, 1.0 - p.y)
                                : vec2(1.0 - p.x, 1.0 - p.y);
    }
    uint rotation = (a >> 23) & 3u;
    vec2 uv = uvLayer.xy;
    uvLayer.xy = rotation == 1u ? vec2(uv.y, 1.0 - uv.x)
               : rotation == 2u ? vec2(1.0 - uv.x, 1.0 - uv.y)
               : rotation == 3u ? vec2(1.0 - uv.y, uv.x) : uv;
    if ((b & (1u << 20)) != 0u)
    {
        uint turn = (b >> 21) & 3u;
        int thinAxis = (turn & 1u) == 0u ? 1 : 0;
        if (int(face >> 1) != thinAxis)
        {
            uint index = (b >> 8) & 4095u;
            vec3 p = point - vec3(index & 15u, (index >> 4) & 15u, (index >> 8) & 15u);
            p.xy = turn == 1u ? vec2(1.0 - p.y, p.x)
                 : turn == 2u ? vec2(1.0 - p.x, 1.0 - p.y)
                 : turn == 3u ? vec2(p.y, 1.0 - p.x) : p.xy;
            // The edge texture stores its three opaque columns at the left.
            uvLayer.xy = vec2(p.y, 1.0 - (face >= 4u ? p.x : p.z));
        }
    }

    cellPosition = point - Normals[face] * 0.5;
    worldPosition = world.xyz;
    normal = mat3(draw.model) * Normals[face];
    occlusion = Occlusion[(a >> 18) & 3u];
    cell = (uint(voxelSlot) * 4096u + ((b >> 8) & 4095u)) | (merged ? 0x80000000u : 0u);
}
