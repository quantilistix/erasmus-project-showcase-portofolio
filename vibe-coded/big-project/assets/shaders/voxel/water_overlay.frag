#version 450
#extension GL_GOOGLE_include_directive : require
#include "frame.glsl"
#include "lighting.glsl"

layout(set = 0, binding = 1) uniform sampler2DArray textures;
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 color;

void main()
{
    vec4 water = textureLod(textures, vec3(uv, atmosphere.w), 0.0);
    ivec3 eyeCell = ivec3(floor(cameraPosition.xyz));
    vec3 ambient = lightScene.w != 0
        ? lightCell(eyeCell, gridSlot(eyeCell >> 4)).value.rgb
        : vec3(0.0);
    color = vec4(water.rgb * ambient, water.a);
}
