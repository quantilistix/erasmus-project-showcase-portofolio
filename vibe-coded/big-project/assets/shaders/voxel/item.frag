#version 450
#extension GL_GOOGLE_include_directive : require

// Items lit as blocks are, by the sun and the sky, then fogged. Sampled at full detail: an
// item's pixels are its shape, and its mipmaps would fade the edges of a thin one away.

#include "frame.glsl"
#include "sky.glsl"
#include "lighting.glsl"

layout(set = 0, binding = 1) uniform sampler2DArray textures;

layout(location = 0) in vec3 uvLayer;
layout(location = 1) in vec3 worldPosition;
layout(location = 2) in vec3 normal;

layout(location = 0) out vec4 color;

void main()
{
    vec3 uv = uvLayer;
    int layerIndex = int(round(uv.z));
    uv.z = float(layerIndex == lightCounts.z ? lightCounts.w : layerIndex);
    vec4 albedo = textureLod(textures, uv, 0.0);
    bool water = layerIndex == int(atmosphere.w);
    if (albedo.a <= 0.0 || (!water && albedo.a < 0.5))
        discard;

    vec3 lit = water ? albedo.rgb * sampleLight(worldPosition, vec3(0.0)).rgb
                     : illuminate(albedo.rgb, worldPosition, normalize(normal), 1.0);

    float fog = smoothstep(settings.x, settings.y, distance(worldPosition, cameraPosition.xyz));
    color = vec4(mix(lit, skyGradient(normalize(worldPosition-cameraPosition.xyz)), fog), water ? albedo.a : 1.0);
}
