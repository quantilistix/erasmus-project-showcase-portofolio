#version 450

// The atlas is an sRGB texture, so it samples linear, like the tint.

layout(set = 0, binding = 0) uniform sampler2D atlas;

layout(location = 0) in vec2 fragmentUv;
layout(location = 1) in vec4 fragmentColor;

layout(location = 0) out vec4 color;

void main()
{
    color = texture(atlas, fragmentUv) * fragmentColor;
    if (color.a < 0.004)
        discard;
}
