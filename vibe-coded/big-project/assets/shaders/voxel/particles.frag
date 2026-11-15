#version 450
#extension GL_GOOGLE_include_directive : require

#include "frame.glsl"
#include "sky.glsl"
#include "lighting.glsl"

layout(set = 0, binding = 1) uniform sampler2DArray textures;

layout(location = 0) in vec3 fragmentUv;
layout(location = 1) in vec3 worldPosition;
layout(location = 2) in float fragmentOpacity;
layout(location = 3) flat in float fragmentDustShade;
layout(location = 4) flat in vec3 fragmentNormal;
layout(location = 0) out vec4 color;

void main()
{
    bool explosion = fragmentUv.z < 0.0;
    float layer = explosion ? -fragmentUv.z - 1.0 : fragmentUv.z;
    // Point-sampled sprite frames preserve their pixel art and alpha silhouettes.
    vec4 chip = textureLod(textures, vec3(fragmentUv.xy, layer), 0.0);
    if (chip.a < 0.5)
        discard;
    // Each dust cloud keeps its spawn-time gray shade through animation and lighting.
    vec3 albedo = fragmentDustShade > 0.0
        ? vec3(dot(chip.rgb, vec3(0.2126, 0.7152, 0.0722)) * fragmentDustShade)
        : chip.rgb;
    vec3 lit = illuminateParticle(albedo, worldPosition, normalize(fragmentNormal));
    float fog = smoothstep(settings.x, settings.y, distance(worldPosition, cameraPosition.xyz));
    color = vec4(mix(lit, skyGradient(normalize(worldPosition-cameraPosition.xyz)), fog), 1.0);
}
