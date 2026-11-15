#version 450
layout(location=0) in vec4 tint;
layout(location=1) in vec2 uv;
layout(location=0) out vec4 color;
void main() {
 if(uv.x<1.5 && dot(uv,uv)>1.0) discard;
 color=tint;
}
