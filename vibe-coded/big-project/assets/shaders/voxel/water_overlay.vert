#version 450
#extension GL_GOOGLE_include_directive : require
#include "frame.glsl"

layout(location = 0) out vec2 uv;
void main()
{
    vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2) * 2.0 - 1.0;
    gl_Position = vec4(p, 0.0, 1.0);
    // Cover the viewport once, preserving the square texture's aspect and cropping
    // the shorter screen dimension symmetrically instead of repeating or stretching.
    vec2 scale = cameraPosition.w >= 1.0 ? vec2(1.0, 1.0 / cameraPosition.w)
                                       : vec2(cameraPosition.w, 1.0);
    uv = p * 0.5 * scale + 0.5;
}
