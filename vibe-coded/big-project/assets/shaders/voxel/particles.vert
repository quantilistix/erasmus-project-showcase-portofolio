#version 450
#extension GL_GOOGLE_include_directive : require
#include "frame.glsl"
#include "particle_billboard.glsl"
// Devices without geometry shaders expand the same stream as instanced strips.
layout(location=0) in vec4 positionSize;
layout(location=1) in vec4 uvLayerSize;
layout(location=2) in float dustShade;
layout(location=0) out vec3 fragmentUv;
layout(location=1) out vec3 worldPosition;
layout(location=2) out float fragmentOpacity;
layout(location=3) flat out float fragmentDustShade;
layout(location=4) flat out vec3 fragmentNormal;
void main()
{
    vec2 corner=vec2(gl_VertexIndex&1,gl_VertexIndex>>1);
    vec3 right=billboardRight(),up=billboardUp();
    worldPosition=positionSize.xyz+((corner.x-.5)*right+(corner.y-.5)*up)*positionSize.w;
    fragmentUv=vec3(uvLayerSize.xy+corner*uvLayerSize.w,uvLayerSize.z);
    fragmentOpacity=1; fragmentDustShade=dustShade;
    fragmentNormal=normalize(cross(right,up));
    gl_Position=viewProjection*vec4(worldPosition,1);
}
