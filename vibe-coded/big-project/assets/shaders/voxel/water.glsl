// Water volume integration over exact voxel intervals. Requires lighting.glsl.
bool waterMaterial(uint material)
{
    return ((material >> 16) & 255u) == uint(lightScene.z);
}

// RGB is premultiplied in the accumulator, then converted for SrcAlpha blending.
// Authored alpha is the absorption over one block (Beer-Lambert extinction).
vec4 marchWater(vec3 start, vec3 direction, float limit, vec3 tint, float opacity,
                out float travelled)
{
    float extinction = -log(max(1.0 - opacity, 0.001));
    ivec3 cell = ivec3(floor(start + direction * 0.0001));
    ivec3 stepCell = ivec3(sign(direction));
    vec3 delta = 1.0 / max(abs(direction), vec3(1e-20));
    vec3 boundary = vec3(cell) + step(vec3(0.0), direction);
    vec3 next = vec3(1e30);
    for (int axis = 0; axis < 3; axis++)
        if (stepCell[axis] != 0) next[axis] = max(0.0, (boundary[axis] - start[axis]) / direction[axis]);

    vec3 radiance = vec3(0.0);
    float transmission = 1.0;
    travelled = 0.0;
    ivec3 key = cell >> 4;
    uint slot = gridSlot(key);
    // Bounded cost; skip zero-length edge/corner crossings together, cache chunk lookups,
    // and terminate once the background contributes less than half a percent.
    for (int iteration = 0; iteration < 256; iteration++)
    {
        if (slot == 0u || !waterMaterial(gridMaterial(slot - 1u, gridIndex(cell)))) break;
        float end = min(limit, min(next.x, min(next.y, next.z)));
        float segment = max(0.0, end - travelled);
        float absorbed = 1.0 - exp(-extinction * segment);
        vec3 ambient = lightScene.w != 0 ? lightCell(cell, slot).value.rgb : vec3(0.0);
        radiance += transmission * absorbed * tint * ambient;
        transmission *= 1.0 - absorbed;
        travelled = end;
        if (transmission < 0.005 || end >= limit) break;
        for (int axis = 0; axis < 3; axis++)
            if (next[axis] <= end + 1e-5)
            {
                cell[axis] += stepCell[axis];
                next[axis] += delta[axis];
            }
        if (any(notEqual(cell >> 4, key)))
        {
            key = cell >> 4;
            slot = gridSlot(key);
        }
    }
    float alpha = 1.0 - transmission;
    return vec4(alpha > 0.0 ? radiance / alpha : vec3(0.0), alpha);
}

vec4 waterVolume(vec3 position, vec3 normal, vec3 tint, float opacity, float fallbackThickness)
{
    vec3 direction = normalize(position - cameraPosition.xyz);
    // From below, show the textured water-air boundary. The screen pass supplies
    // the volume absorption, so this surface contributes only its own texture.
    if (dot(direction, normal) >= 0.0)
    {
        if (lightScene.y == 0) discard;
        return vec4(tint * sampleLight(position, -normal).rgb, opacity);
    }
    float limit = min(settings.y, 256.0);
    float travelled;
    vec4 result = marchWater(position, direction, limit, tint, opacity, travelled);
    // A transformed water object is absent from terrain metadata. Retain its one-block
    // appearance and sample the world ambient field at its actual position.
    if (travelled == 0.0)
    {
        // The same Beer-Lambert absorption for coarse water outside loaded metadata:
        // one reduced voxel spans fallbackThickness original blocks.
        float alpha = fallbackThickness > 1.0 ? 1.0 - pow(max(1.0-opacity, .001), fallbackThickness) : opacity;
        return vec4(tint * sampleLight(position, vec3(0.0)).rgb, alpha);
    }
    return result;
}

vec4 waterVolume(vec3 position, vec3 normal, vec3 tint, float opacity)
{
    return waterVolume(position, normal, tint, opacity, 1.0);
}
