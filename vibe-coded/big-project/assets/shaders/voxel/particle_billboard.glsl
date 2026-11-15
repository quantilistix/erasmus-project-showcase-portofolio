// Screen directions in world space; undo Vulkan's projection Y flip.
vec3 billboardRight() { return normalize(inverseViewProjection[0].xyz); }
vec3 billboardUp() { return -normalize(inverseViewProjection[1].xyz); }
