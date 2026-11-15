// Shared world mirror and pressure tiles. One pressure field serves all sources.
struct HashEntry { ivec4 key; uvec4 value; };
struct Body { mat4 model; mat4 inverseModel; vec4 bounds; ivec4 identity; };
struct Tile { ivec4 coordinate; ivec4 nearXY; ivec4 nearZ; };
layout(std430,binding=0) buffer Voxels { uint voxels[]; };
layout(std430,binding=1) readonly buffer Hash { HashEntry entries[]; };
layout(std430,binding=2) readonly buffer Bodies { Body bodies[]; };
layout(std430,binding=3) readonly buffer Tiles { Tile tiles[]; };
layout(std430,binding=4) buffer State { vec4 state[]; };
layout(std430,binding=5) buffer Temporary { vec4 temporary[]; };
layout(std430,binding=6) readonly buffer Sources { uvec4 sources[]; };
layout(std430,binding=7) readonly buffer Materials { vec4 materials[]; };
layout(std430,binding=8) buffer Events { uvec4 events[]; };
layout(std430,binding=9) readonly buffer Requests { vec4 requests[]; };
layout(std430,binding=10) writeonly buffer Samples { vec4 samples[]; };
layout(std430,binding=11) readonly buffer Slots { ivec4 slots[]; };
layout(std430,binding=12) readonly buffer Work { uint work[]; };
layout(std430,binding=13) buffer Occupancy { uint occupied[]; };
layout(std430,binding=14) readonly buffer Candidates { uint bodyCandidates[]; };
layout(push_constant) uniform Parameters {
    uvec4 counts; // pressure tiles, hash mask, bodies, voxel chunks
    vec4 physics; // dt, wave speed, pulse duration, threshold
    uvec4 batch;  // sources, samples, event capacity, map body id
} p;
const ivec3 directions[6] = ivec3[](ivec3(-1,0,0),ivec3(1,0,0),ivec3(0,-1,0),ivec3(0,1,0),ivec3(0,0,-1),ivec3(0,0,1));
uint invocation() { return gl_GlobalInvocationID.x+gl_GlobalInvocationID.y*gl_NumWorkGroups.x*gl_WorkGroupSize.x; }
uint hashKey(ivec4 k) {
    uvec4 q=uvec4(k);
    uint h=q.x*73856093u ^ q.y*19349663u ^ q.z*83492791u ^ q.w*2654435761u;
    h^=h>>16; h*=2246822519u; return h^(h>>13);
}
int find(ivec4 key) {
    uint h=hashKey(key)&p.counts.y;
    for(uint i=0u;i<=p.counts.y;i++) {
        HashEntry e=entries[h];
        if(e.value.x==0u) return -1;
        if(all(equal(e.key,key))) return int(e.value.x)-1;
        h=(h+1u)&p.counts.y;
    }
    return -1;
}
ivec3 localCell(uint index) { return ivec3(index&15u,(index>>4)&15u,(index>>8)&15u); }
uint localIndex(ivec3 c) { return uint(c.x+16*(c.y+16*c.z)); }
int pressureIndex(ivec3 c) {
    int tile=find(ivec4(c>>4,-1));
    return tile<0 ? -1 : tile*4096+int(localIndex(c&15));
}
int adjacent(uint index,int face) {
    uint tile=index>>12;
    ivec3 c=localCell(index)+directions[face];
    int axis=face>>1;
    if(c[axis]>=0&&c[axis]<16) return int(tile*4096u+localIndex(c));
    int other=face<4 ? tiles[tile].nearXY[face] : tiles[tile].nearZ[face-4];
    return other<0 ? -1 : other*4096+int(localIndex(c&15));
}
uint voxelAt(int body,ivec3 cell) {
    int slot=find(ivec4(cell>>4,body));
    return slot<0 ? 0u : voxels[uint(slot)*4096u+localIndex(cell&15)];
}
bool solidVoxel(uint value) { return materials[(value&65535u)*2u+1u].y>0.5; }
float airPressure(int i) { return i<0||occupied[i]!=0u ? 0.0 : state[i].x; }
vec4 cellLoad(ivec3 cell) {
    int index=pressureIndex(cell);
    if(index<0) return vec4(0);
    float compression=max(state[index].x,0.0);
    vec3 momentum=state[index].yzw;
    if(occupied[index]!=0u) {
        compression=0.0; momentum=vec3(0);
        for(int f=0;f<6;f++) {
            float incoming=max(airPressure(adjacent(uint(index),f)),0.0);
            compression=max(compression,incoming);
            momentum-=vec3(directions[f])*incoming;
        }
    } else {
        // Face velocities are staggered; average onto the cell centre.
        for(int axis=0;axis<3;axis++) {
            int prev=adjacent(uint(index),axis*2);
            momentum[axis]=0.5*(momentum[axis]+(prev<0 ? 0.0 : state[prev][axis+1]));
        }
    }
    float magnitude=length(momentum);
    return vec4(compression,magnitude>1e-8 ? momentum*(compression/magnitude) : vec3(0));
}
vec4 sampleLoad(vec3 point) {
    vec3 position=point-0.5, fraction=fract(position);
    ivec3 low=ivec3(floor(position));
    vec4 result=vec4(0);
    for(int z=0;z<2;z++) for(int y=0;y<2;y++) for(int x=0;x<2;x++) {
        vec3 weights=mix(vec3(1)-fraction,fraction,vec3(x,y,z));
        float weight=weights.x*weights.y*weights.z;
        if(weight>0.0) result+=weight*cellLoad(low+ivec3(x,y,z));
    }
    return result;
}
