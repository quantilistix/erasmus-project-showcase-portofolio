#version 450

// The interface of MYT.Kit.Gui (GuiDrawList): quads in pixels from the top-left corner of
// the screen, textured from its atlas and tinted by sRGB vertex colors, which are turned
// linear here for an sRGB framebuffer to encode back.

layout(push_constant) uniform Screen
{
    vec2 size;
} screen;

layout(location = 0) in vec2 position;
layout(location = 1) in vec2 uv;
layout(location = 2) in vec4 color;

layout(location = 0) out vec2 fragmentUv;
layout(location = 1) out vec4 fragmentColor;

vec3 linear(vec3 srgb)
{
    return mix(srgb / 12.92, pow((srgb + 0.055) / 1.055, vec3(2.4)), greaterThan(srgb, vec3(0.04045)));
}

void main()
{
    gl_Position = vec4(position / screen.size * 2.0 - 1.0, 0.0, 1.0);
    fragmentUv = uv;
    fragmentColor = vec4(linear(color.rgb), color.a);
}
