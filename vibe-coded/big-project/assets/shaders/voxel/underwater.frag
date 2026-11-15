#version 450
#extension GL_GOOGLE_include_directive : require

#include "frame.glsl"
#include "lighting.glsl"
#include "water.glsl"

layout(set = 0, binding = 1) uniform sampler2DArray textures;
layout(location = 0) in vec3 viewRay;
layout(location = 0) out vec4 color;

void main()
{
    // The coarsest mip is the water's average tint and one-block absorption.
    // Surface UVs must not paint their pattern onto submerged walls and floors.
    vec4 water = textureLod(textures, vec3(0.5, 0.5, atmosphere.w),
                            float(textureQueryLevels(textures) - 1));
    float travelled;
    color = marchWater(cameraPosition.xyz, normalize(viewRay), min(settings.y, 256.0),
                       water.rgb, water.a, travelled);
}
