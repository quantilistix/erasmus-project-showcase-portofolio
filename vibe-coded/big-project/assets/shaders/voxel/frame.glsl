// Per-frame data shared by the voxel, line and interface shaders. Must match Renderer.cs.

layout(set = 0, binding = 0) uniform FrameData
{
    mat4 viewProjection;
    vec4 cameraPosition;   // xyz = eye, w = viewport aspect ratio
    vec4 sunDirection;     // xyz = unit vector toward the sun
    vec4 skyColor;         // rgb = fog and sky colour
    vec4 settings;         // x = fog start, y = fog end, z = 1 when showing stress, w = first crack layer
    mat4 handViewProjection; // the held item's view, from the eye at the origin, with a field of view of its own
    vec4 lighting; // AO, sun, specular, ambient brightness
    ivec4 lightScene; // x = shadow probe, y = underwater eye, z = water material ID, w = cached light volume is ready
    ivec4 lightCounts; // block lighting enabled, sun shadows enabled, opaque leaf layer, selected leaf layer
    mat4 sunViewProjection;
    mat4 inverseViewProjection;
    vec4 skyZenith;
    vec4 ambientSky;
    vec4 sunColor;
    vec4 atmosphere; // twilight intensity, star visibility, orange/yellow timing, water texture layer
    vec4 starRotation; // xyz = orbit axis, w = angle from world into the star field
    vec4 moonDirection; // xyz = independently orbiting moon
    vec4 explosionLights[4]; // xyz = position, w = flash intensity
};

layout(push_constant) uniform DrawData
{
    mat4 model;            // body frame to world; for the interface: x = 2 / screen width, y = 2 / screen height in [0][0], [0][1]
    ivec4 chunk;           // xyz = chunk origin in body cells, w = voxel data slot
};

// Transient local ambient boost; evaluated immediately, without waiting for light diffusion.
vec3 explosionAmbient(vec3 position)
{
    vec3 result = vec3(0);
    for (int i = 0; i < 4; i++)
    {
        vec3 offset = position - explosionLights[i].xyz;
        result += vec3(1.0, .65, .3) * (120.0 * explosionLights[i].w / (1.0 + 2.0 * dot(offset, offset)));
    }
    return result;
}
