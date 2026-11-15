#version 450
#extension GL_GOOGLE_include_directive : require

// Items: dropped ones in the world, and the one held, drawn over the world from the eye.

#include "frame.glsl"

layout(location = 0) in vec3 position;
layout(location = 1) in vec3 itemNormal;
layout(location = 2) in vec3 itemUvLayer;

layout(location = 0) out vec3 uvLayer;
layout(location = 1) out vec3 worldPosition;
layout(location = 2) out vec3 normal;

void main()
{
    // chunk.x is 1 for the held item, whose model puts it relative to the eye, in world axes.
    bool held = chunk.x == 1;
    vec4 placed = model * vec4(position, 1.0);
    gl_Position = (held ? handViewProjection : viewProjection) * placed;
    worldPosition = held ? cameraPosition.xyz + placed.xyz : placed.xyz;
    normal = mat3(model) * itemNormal;
    uvLayer = itemUvLayer;
}
