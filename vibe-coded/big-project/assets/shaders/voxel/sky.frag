#version 450
#extension GL_GOOGLE_include_directive : require
#include "frame.glsl"
#include "sky.glsl"
layout(location = 0) in vec3 viewRay;
layout(location = 0) out vec4 color;

float hash(vec3 p)
{
    return fract(sin(dot(p, vec3(127.1,311.7,74.7)))*43758.5453);
}

void main()
{
    vec3 ray = normalize(viewRay);
    vec3 sky = skyGradient(ray);
    // Rotate the viewing ray into the celestial sphere's reference frame. This is
    // the inverse of the sun's orbit; the moon has its own angular speed.
    vec3 axis = starRotation.xyz;
    float c = cos(starRotation.w), s = sin(starRotation.w);
    vec3 starRay = ray*c + cross(axis,ray)*s + axis*dot(axis,ray)*(1.0-c);
    // Direction-space cells avoid a longitude seam or poles.
    vec3 starPosition = starRay*220.0;
    vec3 cell = floor(starPosition);
    float seed = hash(cell);
    vec3 center = 0.2 + 0.6*vec3(hash(cell+17.0),hash(cell+43.0),hash(cell+91.0));
    float starDistance = length(fract(starPosition)-center);
    float width = max(length(fwidth(starPosition))*0.4, 0.025);
    float stars = (1.0-smoothstep(0.06,0.06+width,starDistance))*step(0.975,seed);
    sky += mix(vec3(0.55,0.7,1.0),vec3(1.0,0.87,0.65),seed)*stars*atmosphere.y;

    float celestialVisibility = smoothstep(-0.025, 0.04, ray.z);
    vec3 sun = sunDirection.xyz;
    float sunDistance = length(ray-sun);
    float edge = max(fwidth(sunDistance),0.0001);
    float sunDisc = 1.0-smoothstep(0.033-edge,0.033+edge,sunDistance);
    float sunHalo = exp(-sunDistance*sunDistance/0.008)*0.25;
    sky += sunColor.rgb*(sunDisc*2.0+sunHalo)*celestialVisibility;

    vec3 moon = moonDirection.xyz;
    float moonDistance = length(ray-moon);
    edge = max(fwidth(moonDistance),0.0001);
    float moonDisc = 1.0-smoothstep(0.028-edge,0.028+edge,moonDistance);
    // Soft craters give the moon a visible surface without a texture asset.
    vec3 right = normalize(cross(moon,vec3(0,0,1)));
    vec3 up = cross(right,moon);
    vec2 uv = vec2(dot(ray,right),dot(ray,up))/0.028;
    float craters = 0.0;
    craters += exp(-dot(uv-vec2(-0.35,0.2),uv-vec2(-0.35,0.2))*25.0);
    craters += exp(-dot(uv-vec2(0.25,-0.35),uv-vec2(0.25,-0.35))*40.0);
    craters += exp(-dot(uv-vec2(0.4,0.4),uv-vec2(0.4,0.4))*60.0);
    vec3 moonSurface = vec3(0.65,0.73,0.9)*(0.85-craters*0.22);
    sky = mix(sky,moonSurface,moonDisc*celestialVisibility);
    sky += vec3(0.1,0.14,0.23)*exp(-moonDistance*moonDistance/0.004)*atmosphere.y*celestialVisibility;
    color = vec4(sky,1.0);
}
