#version 450
#extension GL_GOOGLE_include_directive : require
#include "frame.glsl"
layout(set = 0,binding = 1) uniform sampler2DArray textures;
layout(location = 0) in vec3 uvLayer;
layout(location=1) flat in vec4 fragmentClipMin;
layout(location=2) flat in vec4 fragmentClipMax;
layout(location=3) in vec3 clipPosition;

void main()
{
    if(fragmentClipMin.w!=0.0)
    {
        vec3 p=clipPosition;
        if(any(lessThan(p,fragmentClipMin.xyz)) || any(greaterThanEqual(p,fragmentClipMax.xyz))) discard;
    }
    int layer = int(round(uvLayer.z));
    // Water transmits sunlight; its authored opacity must not create a solid shadow.
    if (layer == int(atmosphere.w)) discard;
    if (layer == lightCounts.z) layer = lightCounts.w;
    if (texture(textures,vec3(fract(uvLayer.xy),float(layer))).a < .5) discard;
}
