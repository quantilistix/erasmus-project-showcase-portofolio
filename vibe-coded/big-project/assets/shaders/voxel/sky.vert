#version 450
#extension GL_GOOGLE_include_directive : require
#include "frame.glsl"
layout(location = 0) out vec3 viewRay;
void main()
{
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2)*2.0-1.0;
    gl_Position = vec4(p, 0.0, 1.0);
    // Reversed infinite projection: the far-plane homogeneous point has w = 0,
    // so its xyz is a world-space direction without any camera translation.
    viewRay = (inverseViewProjection * vec4(p, 0.0, 1.0)).xyz;
}
