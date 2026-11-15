// Identical cube-face axes for ray generation and shadow lookup.
const uint PointShadowSize=16u;
const uint PointShadowSamples=6u*PointShadowSize*PointShadowSize;
vec3 pointShadowRay(uint face,vec2 uv)
{
    return face==0u ? vec3(1,uv.y,uv.x) : face==1u ? vec3(-1,uv.y,-uv.x)
         : face==2u ? vec3(uv.x,1,uv.y) : face==3u ? vec3(uv.x,-1,-uv.y)
         : face==4u ? vec3(uv.x,uv.y,1) : vec3(-uv.x,uv.y,-1);
}
void pointShadowFace(vec3 ray,out uint face,out vec2 uv)
{
    vec3 a=abs(ray);
    if(a.x>=a.y && a.x>=a.z)
    { face=ray.x>=0 ? 0u : 1u; uv=vec2(ray.x>=0 ? ray.z : -ray.z,ray.y)/a.x; }
    else if(a.y>=a.z)
    { face=ray.y>=0 ? 2u : 3u; uv=vec2(ray.x,ray.y>=0 ? ray.z : -ray.z)/a.y; }
    else
    { face=ray.z>=0 ? 4u : 5u; uv=vec2(ray.z>=0 ? ray.x : -ray.x,ray.y)/a.z; }
}
