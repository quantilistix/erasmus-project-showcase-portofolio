// Shared sparse topology. Every active 16^3 brick has a persistent RGB float per cell.
#ifndef LIGHT_GRID_BINDING
#define LIGHT_GRID_BINDING 0
#endif
layout(std430,set=0,binding=LIGHT_GRID_BINDING) readonly buffer Metadata { uint grid[]; };
uint gridHash(ivec3 p) { return uint(p.x)*73856093u ^ uint(p.y)*19349663u ^ uint(p.z)*83492791u; }
uint gridSlot(ivec3 key)
{
    uint mask=grid[0], slot=gridHash(key)&mask;
    for(uint probe=0u;probe<=mask;probe++,slot=(slot+1u)&mask)
    {
        uint e=16u+slot*4u, value=grid[e+3u];
        if(value==0u) return 0u;
        if(all(equal(ivec3(grid[e],grid[e+1u],grid[e+2u]),key))) return value;
    }
    return 0u;
}
uint gridRecord(uint slot) { return grid[1]+slot*16u; }
uint gridMaterial(uint slot,uint cell)
{
    uint r=gridRecord(slot), offset=grid[r+3u];
    if(offset==0u) return grid[r+4u];
    uint header=grid[offset], count=header&65535u, bits=header>>16;
    if(bits==32u) return grid[offset+1u+cell];
    uint perWord=32u/bits;
    uint index=(grid[offset+1u+count+cell/perWord]>>((cell%perWord)*bits))&((1u<<bits)-1u);
    return grid[offset+1u+index];
}
uint gridIndex(ivec3 p) { p &= 15; return uint(p.x+16*p.y+256*p.z); }
uint worldMaterial(ivec3 p)
{
    uint slot=gridSlot(p>>4); return slot==0u ? 0u : gridMaterial(slot-1u,gridIndex(p));
}
uint objectMaterial(ivec3 p)
{
    uint mask=grid[6], start=grid[7], slot=gridHash(p)&mask;
    for(uint probe=0u;probe<=mask;probe++,slot=(slot+1u)&mask)
    {
        uint e=start+slot*4u, value=grid[e+3u];
        if(value==0u) return 0u;
        if(all(equal(ivec3(grid[e],grid[e+1u],grid[e+2u]),p))) return value;
    }
    return 0u;
}
float gridExposure(uint slot,uint cell)
{
    uint r=gridRecord(slot), roof=grid[r+12u];
    return roof==0u || int(grid[r+2u])*16+int(cell>>8)>int(grid[roof+(cell&255u)]) ? 1.0 : 0.0;
}
float worldExposure(ivec3 p)
{
    ivec2 key=p.xy>>4; uint mask=grid[8], start=grid[9], slot=gridHash(ivec3(key,0))&mask;
    for(uint probe=0u;probe<=mask;probe++,slot=(slot+1u)&mask)
    {
        uint e=start+slot*4u, offset=grid[e+2u];
        if(offset==0u) return 1.0;
        if(all(equal(ivec2(grid[e],grid[e+1u]),key))) return p.z>int(grid[offset+uint((p.x&15)+16*(p.y&15))]) ? 1.0 : 0.0;
    }
    return 1.0;
}
vec3 gridReflectance(uint material)
{
    uint offset=grid[3]+((material>>16)&255u)*4u;
    return vec3(uintBitsToFloat(grid[offset]),uintBitsToFloat(grid[offset+1u]),uintBitsToFloat(grid[offset+2u]));
}
const ivec3 gridDirections[6]=ivec3[](ivec3(1,0,0),ivec3(-1,0,0),ivec3(0,1,0),ivec3(0,-1,0),ivec3(0,0,1),ivec3(0,0,-1));
// Occupancy is the original half-cell shape mask. Adjacent open faces must overlap.
bool gridOpen(uint a,uint b,int direction)
{
    if (((a|b)&255u)==0u) return true;
    if ((a&255u)==255u || (b&255u)==255u) return false;
    uint masks[6]=uint[](170u,85u,204u,51u,240u,15u);
    uint shifts[6]=uint[](1u,1u,2u,2u,4u,4u);
    uint left=(~a)&masks[direction], right=(~b)&masks[direction^1];
    return (direction&1)==0 ? (left & (right<<shifts[direction]))!=0u : (left & (right>>shifts[direction]))!=0u;
}
