#version 450
#extension GL_GOOGLE_include_directive : require
#include "frame.glsl"
#include "particle_billboard.glsl"
layout(points) in;
layout(triangle_strip,max_vertices=4) out;
layout(location=0) in vec4 pointUv[];
layout(location=1) in float pointSize[];
layout(location=2) in float pointShade[];
layout(location=0) out vec3 fragmentUv;
layout(location=1) out vec3 worldPosition;
layout(location=2) out float fragmentOpacity;
layout(location=3) flat out float fragmentDustShade;
layout(location=4) flat out vec3 fragmentNormal;
void main()
{
    vec3 right=billboardRight(),up=billboardUp();
    for(int k=0;k<4;k++)
    {
        vec2 corner=vec2(k&1,k>>1);
        worldPosition=gl_in[0].gl_Position.xyz+((corner.x-.5)*right+(corner.y-.5)*up)*pointSize[0];
        fragmentUv=vec3(pointUv[0].xy+corner*pointUv[0].w,pointUv[0].z);
        fragmentOpacity=1; fragmentDustShade=pointShade[0];
        fragmentNormal=normalize(cross(right,up));
        gl_Position=viewProjection*vec4(worldPosition,1);
        EmitVertex();
    }
    EndPrimitive();
}
