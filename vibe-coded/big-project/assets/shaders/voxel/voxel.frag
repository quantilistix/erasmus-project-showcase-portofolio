#version 450
#extension GL_GOOGLE_include_directive : require

// Block faces: the block's texture, cracks by damage, optionally tinted by how close the block
// is to failing, lit by the sun and the sky, ambient occlusion, then fog.

#include "frame.glsl"
#include "sky.glsl"
#include "lighting.glsl"
#include "water.glsl"

layout(set = 0, binding = 1) uniform sampler2DArray textures;
layout(constant_id = 0) const bool waterPass = false;

// Per cell of every chunk in a slot: bits 0-7 damage (0-255), bits 8-15 stress over strength × 64.
layout(std430, set = 0, binding = 2) readonly buffer VoxelData { uint voxelData[]; };

layout(location = 0) in vec3 uvLayer;
layout(location = 1) in vec3 worldPosition;
layout(location = 2) flat in vec3 normal;
layout(location = 3) in float occlusion;
layout(location = 4) flat in uint cell;
layout(location = 5) in vec3 cellPosition;
layout(location = 6) flat in int voxelSlot;
layout(location = 7) flat in float voxelScale;

layout(location = 0) out vec4 color;

// Heat map of stress over strength: blue when idle, green, yellow, red at the limit, white past it.
vec3 stressColor(float ratio)
{
    const vec3 ramp[5] = vec3[](vec3(0.15, 0.3, 0.9), vec3(0.1, 0.8, 0.3), vec3(1.0, 0.85, 0.1), vec3(1.0, 0.12, 0.05), vec3(1.0, 0.9, 1.0));
    float x = clamp(ratio, 0.0, 1.25) * 3.0;
    int i = min(int(x), 3);
    return mix(ramp[i], ramp[i + 1], x - float(i));
}

layout(location=8) flat in vec4 fragmentClipMin;
layout(location=9) flat in vec4 fragmentClipMax;

void main()
{
    if(fragmentClipMin.w!=0.0)
    {
        vec3 p=worldPosition-normalize(normal)*.001;
        if(any(lessThan(p,fragmentClipMin.xyz)) || any(greaterThanEqual(p,fragmentClipMax.xyz))) discard;
    }
    bool merged = (cell & 0x80000000u) != 0u;
    // Keep clamp-to-edge sampling for each tile (also shared with item models).
    // Derivatives come from the continuous coordinates, before wrapping.
    vec2 uv = merged ? fract(uvLayer.xy) : uvLayer.xy;
    // Damage varies within a rectangle. Evaluate derivatives before divergent damage
    // branches or alpha discard, including for the crack overlay.
    vec2 uvDx = dFdx(uvLayer.xy);
    vec2 uvDy = dFdy(uvLayer.xy);
    // Texture layers are discrete. Perspective interpolation can produce e.g. 11.999999
    // for layer 12; testing that float for equality alternated opaque/cutout leaves.
    int layerIndex = int(round(uvLayer.z));
    bool water = layerIndex == int(atmosphere.w);
    if (water != waterPass)
        discard;
    float layer = float(layerIndex == lightCounts.z ? lightCounts.w : layerIndex);
    vec4 albedo = textureGrad(textures, vec3(uv, layer), uvDx, uvDy);
    if (albedo.a <= 0.0 || (!water && albedo.a < 0.5))
        discard;

    if(lightScene.x!=0)
    {
        vec3 n=normalize(normal);
        float visible=1.0;
        if(lightScene.x==1) visible=sunShadow(worldPosition,n,1.0);
        else if(grid[4]!=0u)
        {
            uint offset=grid[5];
            vec3 source=vec3(ivec3(grid[offset],grid[offset+1u],grid[offset+2u]))+.5;
            visible=pointVisibility(0u,worldPosition-source,n);
        }
        color=vec4(vec3(visible),1.0);
        return;
    }

    uint index = cell & 0x7fffffffu;
    if (merged)
    {
        uvec3 block = uvec3(clamp(floor(cellPosition), vec3(0.0), vec3(15.0)));
        index = uint(voxelSlot) * 4096u + block.x + 16u * block.y + 256u * block.z;
    }
    uint data = voxelSlot == 0 ? 0u : voxelData[index];
    uint damage = data & 255u;
    if (damage > 0u)
    {
        float stage = min(floor(float(damage) * 6.0 / 256.0), 5.0);
        vec4 crack = textureGrad(textures, vec3(uv, settings.w + stage), uvDx, uvDy);
        albedo.rgb = mix(albedo.rgb, crack.rgb, crack.a);
    }

    if (settings.z > 0.5)
    {
        float ratio = float((data >> 8) & 255u) / 64.0;
        float shade = dot(albedo.rgb, vec3(0.3, 0.55, 0.15));
        albedo.rgb = stressColor(ratio) * (0.55 + 0.45 * shade);
    }

    vec4 waterColor = vec4(0.0);
    vec3 lit;
    if (water)
    {
        waterColor = waterVolume(worldPosition, normalize(normal), albedo.rgb, albedo.a, voxelScale);
        lit = waterColor.rgb;
    }
    else
        lit = illuminate(albedo.rgb, worldPosition, normalize(normal), mix(1.0, occlusion, lighting.x));

    float fog = smoothstep(settings.x, settings.y, distance(worldPosition, cameraPosition.xyz));
    color = vec4(mix(lit, skyGradient(normalize(worldPosition-cameraPosition.xyz)), fog), water ? waterColor.a : 1.0);
}
