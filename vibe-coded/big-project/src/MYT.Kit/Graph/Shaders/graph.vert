#version 450
layout(location=0) in vec3 position;
layout(location=1) in vec4 color;
layout(location=2) in vec2 uv;
layout(location=0) out vec4 tint;
layout(location=1) out vec2 texcoord;
layout(push_constant) uniform Screen { vec2 size; } screen;
void main() {
 gl_Position=vec4(position.xy/screen.size*2.0-1.0,position.z,1.0);
 tint=color; texcoord=uv;
}
