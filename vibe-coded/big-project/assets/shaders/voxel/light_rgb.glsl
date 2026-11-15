// RGB9E5: three positive floating components with a shared exponent (4 bytes/cell).
uint packLight(vec3 rgb)
{
    rgb=clamp(rgb,vec3(0),vec3(65000));
    float largest=max(rgb.x,max(rgb.y,rgb.z));
    int exponent=max(-15,int((floatBitsToUint(largest)>>23)&255u)-126);
    float scale=uintBitsToFloat(uint(136-exponent)<<23);
    uvec3 mantissa=uvec3(rgb*scale+.5);
    if(max(mantissa.x,max(mantissa.y,mantissa.z))>=512u)
    { exponent++; mantissa=uvec3(rgb*(scale*.5)+.5); }
    return mantissa.x|(mantissa.y<<9)|(mantissa.z<<18)|(uint(exponent+15)<<27);
}
vec3 unpackLight(uint rgb)
{
    return vec3(rgb&511u,(rgb>>9)&511u,(rgb>>18)&511u)*uintBitsToFloat(((rgb>>27)+103u)<<23);
}
