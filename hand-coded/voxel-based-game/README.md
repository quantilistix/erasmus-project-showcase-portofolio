This is a voxel based game writen in C++ and OpenGL. It currently doesn't feature game mechanics such as: player gravity, player collision, breaking/placing blocks, items etc. However, it does feature the map and terrain generation.

# -------------------------------------------------------------------- #

# The map #

The map is a voxel grid broken into a 2D grid of 16x16x256 block chunks. This way, the player may explore and the terrain in front of him generates while the terrain in his back gets deleted. This implementation does not feature chunk recycling.

Each chunk is a 16x16x256 block array. Each block is an int32 that represents which block type it is. The mapping is currently:\
0 -> air\
1 -> grass\
2 -> dirt\
3 -> stone\
4 -> bedrock\
5 -> cobblestone\
6 -> acacia log\
7 -> acacia planks\
8 -> tnt\
9 -> oak leaves\
10 -> water\
11 -> sand

The chunk creation process:
The engine runs on the main thread managing and delegating work to other threads. The current implementation uses a thread for each chunk. The algorithm per thread is a state machine:
1. That generates the terrain (voxel data) using perlin noise.
2. That generates the defragmentated geometry (vertices and faces without memory gaps) by discarding invisible faces (culled mesher).
3. That creates the GPU buffers and uploads the geometry to the buffers for rendering.

At the end of a chunk's lifetime, the same thread frees the chunk in the reverse order of creation, using another state machine.

What I would now is I would make a pointer array with all the chunks and whenever the player moves, recycle the chunks. The old chunks become the new ones. This way I don't need to allocate and deallocate as much memory. Also, I'd make smarter use of threads. I know it's a kinda generic and simple explanation, but the whole details would fill this page.

# Block types #

All block properties are held in separate files with a custom file type (.dat). These properties include, most importantly, the textures applied on each cube face. The textures are loaded into one big atlas (an image with all the textures in it aligned in a 2D grid) with padding for mipmapping, more about that later. When generating the geometry for the terrain, block by block, the value held in the voxel data for each block is used to determine to what block type to look at. At the right block type, we look at the corresponding face texture, since we are generating each face at a time, to determine the texture. For example: if it's the side face of a grass block, we look the the side texture of the grass block type which is "grass_block_side.png". The UV texture coordinates of the vertices corresponding to that face are set to point to the "grass_block_side.png" texture in the atlas. While it mostly works as you can see even the same block may have different textures on the sides, it has one issue with mipmapping. Mipmapping is the process of making multiple variants of the same image but with different sizes. When you are far away from a texture, the moire effect takes place and you start seeing artifacts when you should be seeing the average value. By applying a lower resolution texture to far away objects, you essentialy take the average of multiple pixels into one bigger pixel and the moire effect dissapears. Now with the atlas, there's a problem. The other sorounding textures get into the average, mixing the textures, so a grass block may turn out orange or red far away. This is why I added massive padding around the textures, a cheap fix.

What I would do now instead is make a 3D texture where the third dimension is used to determine which texture, then set the UVW texture coordinates to the right image. Since OpenGL does not support 3D mipmaping without mixing the textures as well by shrinking the 3rd dimension, I would have to generate them manually, but it would be much easier.

# Usage of AI #

Only for debugging memory corruption bugs. You know how annoying they can get.