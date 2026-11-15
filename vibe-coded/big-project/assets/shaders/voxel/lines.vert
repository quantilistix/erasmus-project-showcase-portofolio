#version 450
#extension GL_GOOGLE_include_directive : require

// World-space lines, such as the outline of the targeted block.

#include "frame.glsl"

layout(location = 0) in vec3 position;
layout(location = 1) in vec4 lineColor;

layout(location = 0) out vec4 fragmentColor;

void main()
{
    gl_Position = viewProjection * vec4(position, 1.0);
    fragmentColor = lineColor;
}
