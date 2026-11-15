// Shared by the background and distant world fog so their horizons meet smoothly.
vec3 skyGradient(vec3 ray)
{
    float height = clamp(ray.z, 0.0, 1.0);
    vec3 sky = mix(skyColor.rgb, skyZenith.rgb, pow(height, 0.55));
    vec3 sun = sunDirection.xyz;
    // Once the sun is high, angular distance from it defines the daylight sky.
    // This keeps the brightest blue near the sun, even above the horizon.
    float sunProximity = clamp(0.5+0.5*dot(ray,sun), 0.0, 1.0);
    // Broaden only after the sun clears the twilight band.
    float noon = smoothstep(0.65, 0.84, sun.z);
    float sunward = pow(sunProximity, mix(0.65, 0.35, noon));
    // A broad, softer cyan region around the high sun fades into a clear blue.
    // Keep the whole daytime hemisphere bright; deep blue comes from twilight.
    float cyan = sunward * smoothstep(-0.1, mix(0.75, 0.55, noon), ray.z);
    vec3 dayHue = mix(vec3(0.09, 0.26, 0.85), vec3(0.16, 0.64, 1.0), cyan);
    vec3 daySky = dayHue * mix(0.72, 1.0, sunward);
    sky = mix(sky, daySky, smoothstep(0.02, 0.6, sun.z));
    float towardSun = dot(ray.xy, sun.xy) / max(length(ray.xy)*length(sun.xy), 0.001);
    // A wide patch centered on the sun, including below the horizon. As the sun
    // sinks into the void, only the upper tip of the patch remains above ground.
    float azimuth = acos(clamp(towardSun, -1.0, 1.0));
    float altitude = asin(clamp(ray.z, -1.0, 1.0));
    float sunAltitude = asin(clamp(sun.z, -1.0, 1.0));
    float across = 1.0-smoothstep(0.0, 1.95, azimuth);
    float vertical = 1.0-smoothstep(0.0, 0.8, abs(altitude-sunAltitude));
    float glow = across * vertical * atmosphere.x;
    vec3 sunsetColor = mix(vec3(1.0, 0.72, 0.12), vec3(1.0, 0.24, 0.025), atmosphere.z);
    sky = mix(sky, sunsetColor, glow*0.95);
    return sky;
}
