// Every-frame compute light grid. This buffer is the latest complete Jacobi step.
#define LIGHT_GRID_BINDING 4
#include "light_grid.glsl"
#include "light_rgb.glsl"
layout(std430,set=0,binding=6) readonly buffer LightingField { uint lightRgb[]; };
layout(std430,set=0,binding=7) readonly buffer LightingPoints { float pointDepth[]; };
struct LightSample { vec4 value; float weight; };
LightSample blendLight(LightSample a,LightSample b,float f)
{
    return LightSample(mix(a.value,b.value,f),mix(a.weight,b.weight,f));
}
LightSample lightCell(ivec3 cell,uint slot)
{
    if(slot==0u) { float exposed=worldExposure(cell); return LightSample(vec4(ambientSky.rgb*lighting.w*1.85*exposed,exposed),1.0); }
    uint index=gridIndex(cell);
    uint occupancy=grid[gridRecord(slot-1u)+14u];
    // Wall cells store reflected irradiance, including light on their exterior side.
    // Blending that value into room air leaks light through one-block-thick corners.
    if(occupancy==2u || (occupancy!=1u && (gridMaterial(slot-1u,index)&255u)==255u))
        return LightSample(vec4(0),0.0);
    return LightSample(vec4(unpackLight(lightRgb[(slot-1u)*4096u+index]),gridExposure(slot-1u,index)),1.0);
}
// The corner's slot: the base cell's, unless the corner lies in another chunk.
LightSample lightCorner(ivec3 cell,ivec3 corner,uint baseSlot)
{
    ivec3 c=cell+corner;
    return lightCell(c,all(equal(c>>4,cell>>4)) ? baseSlot : gridSlot(c>>4));
}
vec4 sampleLight(vec3 position,vec3 n)
{
    if(lightScene.w==0) return vec4(explosionAmbient(position),0);
    // Samples stay on the air side of the face. Interpolation smooths voxel propagation gradients.
    vec3 p = position+n*0.51-0.5;
    ivec3 cell = ivec3(floor(p)); vec3 f = fract(p);
    // Most footprints lie within one chunk; only those across a chunk face look up another.
    uint slot = gridSlot(cell>>4);
    LightSample a = blendLight(lightCell(cell,slot),lightCorner(cell,ivec3(1,0,0),slot),f.x);
    LightSample b = blendLight(lightCorner(cell,ivec3(0,1,0),slot),lightCorner(cell,ivec3(1,1,0),slot),f.x);
    LightSample c = blendLight(lightCorner(cell,ivec3(0,0,1),slot),lightCorner(cell,ivec3(1,0,1),slot),f.x);
    LightSample d = blendLight(lightCorner(cell,ivec3(0,1,1),slot),lightCorner(cell,ivec3(1,1,1),slot),f.x);
    LightSample result=blendLight(blendLight(a,b,f.y),blendLight(c,d,f.y),f.z);
    return (result.weight>0.0 ? result.value/result.weight : vec4(0)) + vec4(explosionAmbient(position),0);
}
#include "point_shadow.glsl"
float pointVisibility(uint point,vec3 ray,vec3 n)
{
    uint face; vec2 uv; pointShadowFace(ray,face,uv);
    vec2 texel=(uv*.5+.5)*float(PointShadowSize)-.5;
    ivec2 base=ivec2(floor(texel)); vec2 f=fract(texel);
    float visible=0.0;
    for(int y=0;y<2;y++) for(int x=0;x<2;x++)
    {
        // Reproject taps that cross a cube edge onto the adjoining face.
        vec3 tapRay=pointShadowRay(face,2.0*(vec2(base+ivec2(x,y))+.5)/float(PointShadowSize)-1.0);
        uint tapFace; vec2 tapUv; pointShadowFace(tapRay,tapFace,tapUv);
        ivec2 pixel=clamp(ivec2((tapUv*.5+.5)*float(PointShadowSize)),ivec2(0),ivec2(int(PointShadowSize)-1));
        tapRay=pointShadowRay(tapFace,2.0*(vec2(pixel)+.5)/float(PointShadowSize)-1.0);
        // Depth belongs to the tap's ray, not the fragment's ray. Intersect the tap
        // with the receiver plane to avoid square self-shadows on oblique surfaces.
        float denominator=dot(n,tapRay);
        float receiver=abs(denominator)>1e-6 ? dot(n,ray)/denominator : max(abs(ray.x),max(abs(ray.y),abs(ray.z)));
        float depth=pointDepth[point*PointShadowSamples+tapFace*PointShadowSize*PointShadowSize+uint(pixel.x)+PointShadowSize*uint(pixel.y)];
        float weight=(x==0 ? 1.0-f.x : f.x)*(y==0 ? 1.0-f.y : f.y);
        visible+=weight*(receiver<=depth+max(.002,abs(receiver)*.00001) ? 1.0 : 0.0);
    }
    return visible;
}
vec3 pointLighting(vec3 position,vec3 n)
{
    vec3 result=vec3(0);
    // A zero normal requests omnidirectional lighting for particle billboards.
    bool isotropic=dot(n,n)==0.0;
    for(int i=0; i<4; i++)
    {
        vec3 ray=position-explosionLights[i].xyz;
        float distanceSquared=dot(ray,ray);
        float diffuse=isotropic ? 1.0 : max(dot(n,-ray*inversesqrt(max(distanceSquared,.0001))),0.0);
        result+=vec3(1.0,.65,.3)*(1200.0*explosionLights[i].w/(1.0+distanceSquared))*diffuse;
    }
    if(lightScene.w==0 || lightCounts.x==0) return result;
    for(uint i=0u;i<grid[4];i++)
    {
        uint offset=grid[5]+i*4u;
        if(grid[offset+3u]!=0u) continue;
        vec3 source=vec3(ivec3(grid[offset],grid[offset+1u],grid[offset+2u]))+.5;
        vec3 ray=position-source;
        float distanceSquared=dot(ray,ray);
        if(distanceSquared<.0001) continue;
        float diffuse=isotropic ? 1.0 : max(dot(n,-normalize(ray)),0.0);
        if(diffuse==0.0) continue;
        result+=vec3(1.0,.55,.2)*(18.0/(1.0+distanceSquared))*diffuse*pointVisibility(i,ray,n);
    }
    return result;
}
layout(set = 0,binding = 5) uniform sampler2D sunShadowMap;
float sunShadow(vec3 position,vec3 n,float fallback)
{
    if (lightCounts.y == 0) return fallback;
    vec3 p=(sunViewProjection*vec4(position,1.0)).xyz;
    vec2 uv=p.xy*.5+.5;
    if(any(lessThan(uv,vec2(0))) || any(greaterThan(uv,vec2(1))) || p.z<0.0 || p.z>1.0) return fallback;
    vec2 size=vec2(textureSize(sunShadowMap,0));
    vec2 pixel=uv*size-.5, base=floor(pixel), f=fract(pixel);
    // Transform the receiver plane into the sheared orthographic shadow space.
    float denominator=dot(n,sunDirection.xyz/sunDirection.z);
    vec2 gradient=abs(denominator)>1e-6
        ? -n.xy*sunViewProjection[2][2]/(vec2(sunViewProjection[0][0],sunViewProjection[1][1])*denominator) : vec2(0);
    float visible=0.0;
    for(int y=0;y<2;y++) for(int x=0;x<2;x++)
    {
        vec2 tap=(base+vec2(x,y)+.5)/size;
        float depth=texture(sunShadowMap,tap).r;
        float receiver=p.z+dot(gradient,2.0*(tap-uv));
        float weight=(x==0 ? 1.0-f.x : f.x)*(y==0 ? 1.0-f.y : f.y);
        visible+=weight*(receiver+.000001>=depth ? 1.0 : 0.0);
    }
    float edge = max(abs(p.x),abs(p.y));
    return mix(visible,fallback,smoothstep(.85,1.0,edge));
}
vec3 illuminate(vec3 albedo,vec3 position,vec3 n,float ao)
{
    vec4 field = sampleLight(position,n);
    vec3 view = normalize(cameraPosition.xyz-position);
    float diffuse = max(dot(n,sunDirection.xyz),0.0)*lighting.y;
    float sunVisibility = diffuse > 0.0 ? sunShadow(position,n,field.w) : 0.0;
    vec3 sunlight = sunColor.rgb;
    vec3 light = field.rgb + sunlight*diffuse*0.75*sunVisibility + pointLighting(position,n);
    vec3 specular = vec3(0);
    if (diffuse > 0.0)
        specular = sunlight*pow(max(dot(n,normalize(view+sunDirection.xyz)),0.0),32.0)*0.15*lighting.y*lighting.z*sunVisibility;
    return albedo*light*ao+specular;
}

// Sample irradiance at the fragment itself, without a block-face offset. Direct
// sunlight and point sources use the billboard normal, including camera rotation.
vec3 illuminateParticle(vec3 albedo,vec3 position,vec3 n)
{
    vec4 field=sampleLight(position,vec3(0));
    float diffuse=max(dot(n,sunDirection.xyz),0.0)*lighting.y;
    float visibility=diffuse>0.0 ? sunShadow(position,n,field.w) : 0.0;
    vec3 light=field.rgb+sunColor.rgb*diffuse*0.75*visibility+pointLighting(position,n);
    return albedo*light;
}
