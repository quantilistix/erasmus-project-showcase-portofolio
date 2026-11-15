// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <iostream>
// #include <cmath>
// #include <unordered_map>
// #include <atomic>

// // Include OpenGL math library (GLM)
// #include "glm/glm.hpp"
// #include "glm/gtc/matrix_transform.hpp"
// #include "glm/gtc/type_ptr.hpp"

// Globals
#include "globals.hpp"
// Game engine
#include "engine.hpp"

// This function transforms the world data to geometry
void Engine::calChunkGeometrySize(int &&xxxx, int &&yyyy)
{
    int xx = xxxx;
    int yy = yyyy;
    // Initialize air gaps array
    bool airBlocks[26];
    // Initialize the array in which is stored if a vertex should exist
    bool ifVTX[8];

    // Initialize relative blocks variables
    // Chunk positive X neighbour
    int cpx = 0;
    // Chunk negative X neighbour
    int cnx = 0;
    // Chunk positive Y neighbour
    int cpy = 0;
    // Chunk positive Y neighbour
    int cny = 0;
    // Block positive X neighbour
    int px = 0;
    // Block negative X neighbour
    int nx = 0;
    // Block positive Y neighbour
    int pz = 0;
    // Block negative Y neighbour
    int nz = 0;
    // The chunk the program is at

    // Use a lambda to calculate the size of the geometry arrays (vertex count, face count)
    auto sizeIncrementLambda = [&]()
    {
        // Check if vertex should be spawned
        // Vertex group 000
        ifVTX[0] = airBlocks[4] || airBlocks[10] || airBlocks[12];
        // Vertex group 100
        ifVTX[1] = airBlocks[4] || airBlocks[10] || airBlocks[13];
        // Vertex group 010
        ifVTX[2] = airBlocks[4] || airBlocks[12] || airBlocks[15];
        // Vertex group 110
        ifVTX[3] = airBlocks[4] || airBlocks[13] || airBlocks[15];
        // Vertex group 001
        ifVTX[4] = airBlocks[10] || airBlocks[12] || airBlocks[21];
        // Vertex group 101
        ifVTX[5] = airBlocks[10] || airBlocks[13] || airBlocks[21];
        // Vertex group 011
        ifVTX[6] = airBlocks[12] || airBlocks[15] || airBlocks[21];
        // Vertex group 111
        ifVTX[7] = airBlocks[13] || airBlocks[15] || airBlocks[21];

        // Spawn vertex if it should spawn (increase vertex count)
        if (ifVTX[0])
        {
            worldChunkGeometry[{xx, yy}].vtx_count += 5;
        }
        if (ifVTX[1])
        {
            worldChunkGeometry[{xx, yy}].vtx_count += 4;
        }
        if (ifVTX[2])
        {
            worldChunkGeometry[{xx, yy}].vtx_count += 5;
        }
        if (ifVTX[3])
        {
            worldChunkGeometry[{xx, yy}].vtx_count += 4;
        }
        if (ifVTX[4])
        {
            worldChunkGeometry[{xx, yy}].vtx_count += 4;
        }
        if (ifVTX[5])
        {
            worldChunkGeometry[{xx, yy}].vtx_count += 5;
        }
        if (ifVTX[6])
        {
            worldChunkGeometry[{xx, yy}].vtx_count += 4;
        }
        if (ifVTX[7])
        {
            worldChunkGeometry[{xx, yy}].vtx_count += 5;
        }

        // Spawn face if it should spawn (increase face count)
        if (airBlocks[10])
        {
            worldChunkGeometry[{xx, yy}].fac_count += 2;
        }
        if (airBlocks[15])
        {
            worldChunkGeometry[{xx, yy}].fac_count += 2;
        }
        if (airBlocks[4])
        {
            worldChunkGeometry[{xx, yy}].fac_count += 2;
        }
        if (airBlocks[21])
        {
            worldChunkGeometry[{xx, yy}].fac_count += 2;
        }
        if (airBlocks[12])
        {
            worldChunkGeometry[{xx, yy}].fac_count += 2;
        }
        if (airBlocks[13])
        {
            worldChunkGeometry[{xx, yy}].fac_count += 2;
        }
    };

    // This code checks every block and it's neighbors for geometry optimizations
    // This code checks every block and it's neighbors for geometry optimizations (just calculating number of worldChunkGeometry[{xx, yy}].faces and worldChunkGeometry[{xx, yy}].vertices yet)
    for (int x = 0; x < chunk_xSize; x++)
    {
        for (int z = 0; z < chunk_ySize; z++)
        {
            for (int y = 1; y < chunk_zSize - 1; y++)
            {
                // Check if block is air (if it should bother calculating it)
                // Center
                if (worldChunkMap[{xx, yy}].ID[x][y][z] > 0)
                {
                    // Calculate relative coords
                    if (x + 1 > chunk_xSize - 1)
                    {
                        cpx = xx + 1;
                        px = 0;
                    }
                    else
                    {
                        cpx = xx;
                        px = x + 1;
                    }
                    if (x - 1 < 0)
                    {
                        cnx = xx - 1;
                        nx = chunk_xSize - 1;
                    }
                    else
                    {
                        cnx = xx;
                        nx = x - 1;
                    }
                    if (z + 1 > chunk_ySize - 1)
                    {
                        cpy = yy + 1;
                        pz = 0;
                    }
                    else
                    {
                        cpy = yy;
                        pz = z + 1;
                    }
                    if (z - 1 < 0)
                    {
                        cny = yy - 1;
                        nz = chunk_ySize - 1;
                    }
                    else
                    {
                        cny = yy;
                        nz = z - 1;
                    }

                    // Check neigbors
                    airBlocks[0] = worldChunkMap[{cnx, cny}].ID[nx][y - 1][nz] == 0;
                    airBlocks[1] = worldChunkMap[{xx, cny}].ID[x][y - 1][nz] == 0;
                    airBlocks[2] = worldChunkMap[{cpx, cny}].ID[px][y - 1][nz] == 0;
                    airBlocks[3] = worldChunkMap[{cnx, cny}].ID[nx][y][nz] == 0;
                    airBlocks[4] = worldChunkMap[{xx, cny}].ID[x][y][nz] == 0;
                    airBlocks[5] = worldChunkMap[{cpx, cny}].ID[px][y][nz] == 0;
                    airBlocks[6] = worldChunkMap[{cnx, cny}].ID[nx][y + 1][nz] == 0;
                    airBlocks[7] = worldChunkMap[{xx, cny}].ID[x][y + 1][nz] == 0;
                    airBlocks[8] = worldChunkMap[{cpx, cny}].ID[px][y + 1][nz] == 0;
                    airBlocks[9] = worldChunkMap[{cnx, yy}].ID[nx][y - 1][z] == 0;
                    airBlocks[10] = worldChunkMap[{xx, yy}].ID[x][y - 1][z] == 0;
                    airBlocks[11] = worldChunkMap[{cpx, yy}].ID[px][y - 1][z] == 0;
                    airBlocks[12] = worldChunkMap[{cnx, yy}].ID[nx][y][z] == 0;
                    airBlocks[13] = worldChunkMap[{cpx, yy}].ID[px][y][z] == 0;
                    airBlocks[14] = worldChunkMap[{cnx, yy}].ID[nx][y + 1][z] == 0;
                    airBlocks[15] = worldChunkMap[{xx, yy}].ID[x][y + 1][z] == 0;
                    airBlocks[16] = worldChunkMap[{cpx, yy}].ID[px][y + 1][z] == 0;
                    airBlocks[17] = worldChunkMap[{cnx, cpy}].ID[nx][y - 1][pz] == 0;
                    airBlocks[18] = worldChunkMap[{xx, cpy}].ID[x][y - 1][pz] == 0;
                    airBlocks[19] = worldChunkMap[{cpx, cpy}].ID[px][y - 1][pz] == 0;
                    airBlocks[20] = worldChunkMap[{cnx, cpy}].ID[nx][y][pz] == 0;
                    airBlocks[21] = worldChunkMap[{xx, cpy}].ID[x][y][pz] == 0;
                    airBlocks[22] = worldChunkMap[{cpx, cpy}].ID[px][y][pz] == 0;
                    airBlocks[23] = worldChunkMap[{cnx, cpy}].ID[nx][y + 1][pz] == 0;
                    airBlocks[24] = worldChunkMap[{xx, cpy}].ID[x][y + 1][pz] == 0;
                    airBlocks[25] = worldChunkMap[{cpx, cpy}].ID[px][y + 1][pz] == 0;

                    // if (stage)
                    // {
                    //     // Calculate geometry for the block
                    //     calculateGeometry(x, y, z);
                    // }
                    // else
                    // {
                    // Increase size
                    sizeIncrementLambda();
                    // }
                }
            }
        }
    }
    // Bottom
    for (int x = 0; x < chunk_xSize; x++)
    {
        for (int z = 0; z < chunk_ySize; z++)
        {
            // Calculate relative coords
            if (worldChunkMap[{xx, yy}].ID[x][0][z] > 0)
            {
                if (x + 1 > chunk_xSize - 1)
                {
                    cpx = xx + 1;
                    px = 0;
                }
                else
                {
                    cpx = xx;
                    px = x + 1;
                }
                if (x - 1 < 0)
                {
                    cnx = xx - 1;
                    nx = chunk_xSize - 1;
                }
                else
                {
                    cnx = xx;
                    nx = x - 1;
                }
                if (z + 1 > chunk_ySize - 1)
                {
                    cpy = yy + 1;
                    pz = 0;
                }
                else
                {
                    cpy = yy;
                    pz = z + 1;
                }
                if (z - 1 < 0)
                {
                    cny = yy - 1;
                    nz = chunk_ySize - 1;
                }
                else
                {
                    cny = yy;
                    nz = z - 1;
                }

                // Check neigbors
                airBlocks[0] = true;
                airBlocks[1] = true;
                airBlocks[2] = true;
                airBlocks[3] = worldChunkMap[{cnx, cny}].ID[nx][0][nz] == 0;
                airBlocks[4] = worldChunkMap[{xx, cny}].ID[x][0][nz] == 0;
                airBlocks[5] = worldChunkMap[{cpx, cny}].ID[px][0][nz] == 0;
                airBlocks[6] = worldChunkMap[{cnx, cny}].ID[nx][1][nz] == 0;
                airBlocks[7] = worldChunkMap[{xx, cny}].ID[x][1][nz] == 0;
                airBlocks[8] = worldChunkMap[{cpx, cny}].ID[px][1][nz] == 0;
                airBlocks[9] = true;
                airBlocks[10] = true;
                airBlocks[11] = true;
                airBlocks[12] = worldChunkMap[{cnx, yy}].ID[nx][0][z] == 0;
                airBlocks[13] = worldChunkMap[{cpx, yy}].ID[px][0][z] == 0;
                airBlocks[14] = worldChunkMap[{cnx, yy}].ID[nx][1][z] == 0;
                airBlocks[15] = worldChunkMap[{xx, yy}].ID[x][1][z] == 0;
                airBlocks[16] = worldChunkMap[{cpx, yy}].ID[px][1][z] == 0;
                airBlocks[17] = true;
                airBlocks[18] = true;
                airBlocks[19] = true;
                airBlocks[20] = worldChunkMap[{cnx, cpy}].ID[nx][0][pz] == 0;
                airBlocks[21] = worldChunkMap[{xx, cpy}].ID[x][0][pz] == 0;
                airBlocks[22] = worldChunkMap[{cpx, cpy}].ID[px][0][pz] == 0;
                airBlocks[23] = worldChunkMap[{cnx, cpy}].ID[nx][1][pz] == 0;
                airBlocks[24] = worldChunkMap[{xx, cpy}].ID[x][1][pz] == 0;
                airBlocks[25] = worldChunkMap[{cpx, cpy}].ID[px][1][pz] == 0;

                // if (stage)
                // {
                //     // Calculate geometry for the block
                //     calculateGeometry(x, 0, z);
                // }
                // else
                // {
                // Increase size
                sizeIncrementLambda();
                // }
            }
        }
    }
    // Top
    for (int x = 0; x < chunk_xSize; x++)
    {
        for (int z = 0; z < chunk_ySize; z++)
        {
            // Calculate relative coords
            if (worldChunkMap[{xx, yy}].ID[x][chunk_zSize - 1][z] > 0)
            {
                if (x + 1 > chunk_xSize - 1)
                {
                    cpx = xx + 1;
                    px = 0;
                }
                else
                {
                    cpx = xx;
                    px = x + 1;
                }
                if (x - 1 < 0)
                {
                    cnx = xx - 1;
                    nx = chunk_xSize - 1;
                }
                else
                {
                    cnx = xx;
                    nx = x - 1;
                }
                if (z + 1 > chunk_ySize - 1)
                {
                    cpy = yy + 1;
                    pz = 0;
                }
                else
                {
                    cpy = yy;
                    pz = z + 1;
                }
                if (z - 1 < 0)
                {
                    cny = yy - 1;
                    nz = chunk_ySize - 1;
                }
                else
                {
                    cny = yy;
                    nz = z - 1;
                }

                // Check neigbors
                airBlocks[0] = worldChunkMap[{cnx, cny}].ID[nx][chunk_zSize - 2][nz] == 0;
                airBlocks[1] = worldChunkMap[{xx, cny}].ID[x][chunk_zSize - 2][nz] == 0;
                airBlocks[2] = worldChunkMap[{cpx, cny}].ID[px][chunk_zSize - 2][nz] == 0;
                airBlocks[3] = worldChunkMap[{cnx, cny}].ID[nx][chunk_zSize - 1][nz] == 0;
                airBlocks[4] = worldChunkMap[{xx, cny}].ID[x][chunk_zSize - 1][nz] == 0;
                airBlocks[5] = worldChunkMap[{cpx, cny}].ID[px][chunk_zSize - 1][nz] == 0;
                airBlocks[6] = true;
                airBlocks[7] = true;
                airBlocks[8] = true;
                airBlocks[9] = worldChunkMap[{cnx, yy}].ID[nx][chunk_zSize - 2][z] == 0;
                airBlocks[10] = worldChunkMap[{xx, yy}].ID[x][chunk_zSize - 2][z] == 0;
                airBlocks[11] = worldChunkMap[{cpx, yy}].ID[px][chunk_zSize - 2][z] == 0;
                airBlocks[12] = worldChunkMap[{cnx, yy}].ID[nx][chunk_zSize - 1][z] == 0;
                airBlocks[13] = worldChunkMap[{cpx, yy}].ID[px][chunk_zSize - 1][z] == 0;
                airBlocks[14] = true;
                airBlocks[15] = true;
                airBlocks[16] = true;
                airBlocks[17] = worldChunkMap[{cnx, cpy}].ID[nx][chunk_zSize - 2][pz] == 0;
                airBlocks[18] = worldChunkMap[{xx, cpy}].ID[x][chunk_zSize - 2][pz] == 0;
                airBlocks[19] = worldChunkMap[{cpx, cpy}].ID[px][chunk_zSize - 2][pz] == 0;
                airBlocks[20] = worldChunkMap[{cnx, cpy}].ID[nx][chunk_zSize - 1][pz] == 0;
                airBlocks[21] = worldChunkMap[{xx, cpy}].ID[x][chunk_zSize - 1][pz] == 0;
                airBlocks[22] = worldChunkMap[{cpx, cpy}].ID[px][chunk_zSize - 1][pz] == 0;
                airBlocks[23] = true;
                airBlocks[24] = true;
                airBlocks[25] = true;

                // if (stage)
                // {
                //     // Calculate geometry for the block
                //     calculateGeometry(x, chunk_zSize - 1, z);
                // }
                // else
                // {
                // Increase size
                sizeIncrementLambda();
                //}
            }
        }
    }

    // just_do_it(false);
    // Now we can finally create the worldChunkGeometry[{xx, yy}].vertices and worldChunkGeometry[{xx, yy}].faces
    worldChunkGeometry[{xx, yy}].vertices = new float[(worldChunkGeometry[{xx, yy}].vtx_count * 8u)];
    worldChunkGeometry[{xx, yy}].faces = new unsigned int[(worldChunkGeometry[{xx, yy}].fac_count * 3u)];

    worldChunkState[{xx, yy}].store(2);
}

// This function transforms the world data to geometry
void Engine::genChunkGeometry(int &&xxxx, int &&yyyy)
{
    int xx = xxxx;
    int yy = yyyy;
    if (worldChunkGeometry[{xx, yy}].vertices == nullptr || worldChunkGeometry[{xx, yy}].faces == nullptr || worldChunkGeometry[{xx, yy}].vtx_count == 0 || worldChunkGeometry[{xx, yy}].fac_count == 0)
    {
        return;
    }
    // Initialize air gaps array
    bool airBlocks[26];
    // Initialize the array in which is stored if a vertex should exist
    bool ifVTX[8];

    // Initialize counters
    // Vertex counter
    int VTXcount = 0;
    // Previouse vertex counter
    int pVTXcount = 0;
    // Vertex counter (pVTXcount/(vertex_attribute_count (currently 8))
    int pVTXcountf = 0;
    // Face counter
    int FACcount = 0;
    // Previous face counter
    int pFACcount = 0;

    // Initialize relative blocks variables
    // Chunk positive X neighbour
    int cpx = 0;
    // Chunk negative X neighbour
    int cnx = 0;
    // Chunk positive Y neighbour
    int cpy = 0;
    // Chunk positive Y neighbour
    int cny = 0;
    // Block positive X neighbour
    int px = 0;
    // Block negative X neighbour
    int nx = 0;
    // Block positive Y neighbour
    int pz = 0;
    // Block negative Y neighbour
    int nz = 0;
    // The chunk the program is at

    // Use a lambda to calculate the geometry arrays (vertex spawning, face spawning)
    auto calculateGeometry = [&](int x, int y, int z)
    {
        // Calculate if vertex should spawn
        // Vertex group 000
        ifVTX[0] = airBlocks[4] || airBlocks[10] || airBlocks[12];
        // Vertex group 100
        ifVTX[1] = airBlocks[4] || airBlocks[10] || airBlocks[13];
        // Vertex group 010
        ifVTX[2] = airBlocks[4] || airBlocks[12] || airBlocks[15];
        // Vertex group 110
        ifVTX[3] = airBlocks[4] || airBlocks[13] || airBlocks[15];
        // Vertex group 001
        ifVTX[4] = airBlocks[10] || airBlocks[12] || airBlocks[21];
        // Vertex group 101
        ifVTX[5] = airBlocks[10] || airBlocks[13] || airBlocks[21];
        // Vertex group 011
        ifVTX[6] = airBlocks[12] || airBlocks[15] || airBlocks[21];
        // Vertex group 111
        ifVTX[7] = airBlocks[13] || airBlocks[15] || airBlocks[21];

        // Making a cube (a cumbersome thing to do)
        // Vertex mask (to avoid vertex array fragmentation)
        int Vmask[288];
        for (int i = 0; i < 288; i++)
        {
            Vmask[i] = i;
        }
        // Index mask (Vmask/vertex_attribute_count (currently 8)), (to correctly match face indecies with vertices)
        int Imask[36] = {0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35};
        // Face mask (to avoid face array fragmentation)
        int Fmask[36] = {0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35};

        // Attach tags (connect block to geometry)
        worldChunkMap[{xx, yy}].vtxTag[x][y][z] = pVTXcount;
        worldChunkMap[{xx, yy}].facTag[x][y][z] = pFACcount;

        unsigned int block = worldChunkMap[{xx, yy}].ID[x][y][z];

        // The algorithm for generating 1 vertex
        auto vertexInit = [&](int index, float x, float y, float z, float u, float v)
        {
            // The UV coords of the vertex
            float uu = 0;
            float vv = 0;
            // Calculating the UV coords
            if (blockData[block].size[1] != 0)
            {
                // Use an unordered map to get the location of the block texture
                unsigned int tex = blockCollage_imageNames[blockData[block].textures[(unsigned int)floor((float)index / 6.0f)]];
                uu = ((float)(tex % blockCollage_xSize) * blockPaddedTexture_xSize + (blockPaddedTexture_xSize - blockTexture_xSize) / 2 + u * blockTexture_xSize) /
                     blockCollage_xSize / blockPaddedTexture_xSize;
                vv = (floor((float)tex / (float)blockCollage_xSize) * blockPaddedTexture_ySize + (blockPaddedTexture_ySize - blockTexture_ySize) / 2 + v * blockTexture_ySize) /
                     blockCollage_ySize / blockPaddedTexture_ySize;
            }
            // Calculate the position and put the rest of the numbers inside the vertex
            // Position
            worldChunkGeometry[{xx, yy}].vertices[Imask[index] * 8 + pVTXcount] = x - worldMid * chunk_xSize;
            worldChunkGeometry[{xx, yy}].vertices[Imask[index] * 8 + 1 + pVTXcount] = y;
            worldChunkGeometry[{xx, yy}].vertices[Imask[index] * 8 + 2 + pVTXcount] = z - worldMid * chunk_ySize;
            // Normal
            worldChunkGeometry[{xx, yy}].vertices[Imask[index] * 8 + 3 + pVTXcount] = 0;
            worldChunkGeometry[{xx, yy}].vertices[Imask[index] * 8 + 4 + pVTXcount] = 0;
            worldChunkGeometry[{xx, yy}].vertices[Imask[index] * 8 + 5 + pVTXcount] = 0;
            // UV coords
            worldChunkGeometry[{xx, yy}].vertices[Imask[index] * 8 + 6 + pVTXcount] = uu;
            worldChunkGeometry[{xx, yy}].vertices[Imask[index] * 8 + 7 + pVTXcount] = vv;
        };

        // The algorithm for handling the deletion of a vertex
        auto vertexDelete = [&](int index)
        {
            // Vertex mask
            for (int i = index * 8; i < 288; i++)
            {
                Vmask[i] -= 8;
            }
            // Index mask
            for (int i = index; i < 36; i++)
            {
                Imask[i]--;
            }
        };

        // worldChunkGeometry[{xx, yy}].vertices:
        // Vertex #1
        if (ifVTX[0])
        {
            vertexInit(0, xx * chunk_xSize + x, y, yy * chunk_ySize + z, 0.0f, 0.0f);
            VTXcount += 40;
        }
        else
        {
            vertexDelete(1);
        }

        // Vertex #2
        if (ifVTX[1])
        {
            vertexInit(1, xx * chunk_xSize + x + 1, y, yy * chunk_ySize + z, 1.0f, 0.0f);
            VTXcount += 32;
        }
        else
        {
            vertexDelete(2);
        }

        // Vertex #3
        if (ifVTX[5])
        {
            vertexInit(2, xx * chunk_xSize + x + 1, y, yy * chunk_ySize + z + 1, 1.0f, 1.0f);
        }
        else
        {
            vertexDelete(3);
        }

        // Vertex #4
        if (ifVTX[0])
        {
            vertexInit(3, xx * chunk_xSize + x, y, yy * chunk_ySize + z, 0.0f, 0.0f);
        }
        else
        {
            vertexDelete(4);
        }

        // Vertex #5
        if (ifVTX[5])
        {
            vertexInit(4, xx * chunk_xSize + x + 1, y, yy * chunk_ySize + z + 1, 1.0f, 1.0f);
        }
        else
        {
            vertexDelete(5);
        }

        // Vertex #6
        if (ifVTX[4])
        {
            vertexInit(5, xx * chunk_xSize + x, y, yy * chunk_ySize + z + 1, 0.0f, 1.0f);
        }
        else
        {
            vertexDelete(6);
        }

        // Vertex #7
        if (ifVTX[6])
        {
            vertexInit(6, xx * chunk_xSize + x, y + 1, yy * chunk_ySize + z + 1, 0.0f, 1.0f);
        }
        else
        {
            vertexDelete(7);
        }

        // Vertex #8
        if (ifVTX[7])
        {
            vertexInit(7, xx * chunk_xSize + x + 1, y + 1, yy * chunk_ySize + z + 1, 1.0f, 1.0f);
        }
        else
        {
            vertexDelete(8);
        }

        // Vertex #9
        if (ifVTX[2])
        {
            vertexInit(8, xx * chunk_xSize + x, y + 1, yy * chunk_ySize + z, 0.0f, 0.0f);
            VTXcount += 40;
        }
        else
        {
            vertexDelete(9);
        }

        // Vertex #10
        if (ifVTX[7])
        {
            vertexInit(9, xx * chunk_xSize + x + 1, y + 1, yy * chunk_ySize + z + 1, 1.0f, 1.0f);
        }
        else
        {
            vertexDelete(10);
        }

        // Vertex #11
        if (ifVTX[3])
        {
            vertexInit(10, xx * chunk_xSize + x + 1, y + 1, yy * chunk_ySize + z, 1.0f, 0.0f);
        }
        else
        {
            vertexDelete(11);
        }

        // Vertex #12
        if (ifVTX[2])
        {
            vertexInit(11, xx * chunk_xSize + x, y + 1, yy * chunk_ySize + z, 0.0f, 0.0f);
        }
        else
        {
            vertexDelete(12);
        }

        // Vertex #13
        if (ifVTX[2])
        {
            vertexInit(12, xx * chunk_xSize + x, y + 1, yy * chunk_ySize + z, 0.0f, 1.0f);
        }
        else
        {
            vertexDelete(13);
        }

        // Vertex #14
        if (ifVTX[3])
        {
            vertexInit(13, xx * chunk_xSize + x + 1, y + 1, yy * chunk_ySize + z, 1.0f, 1.0f);
        }
        else
        {
            vertexDelete(14);
        }

        // Vertex #15
        if (ifVTX[0])
        {
            vertexInit(14, xx * chunk_xSize + x, y, yy * chunk_ySize + z, 0.0f, 0.0f);
        }
        else
        {
            vertexDelete(15);
        }

        // Vertex #16
        if (ifVTX[0])
        {
            vertexInit(15, xx * chunk_xSize + x, y, yy * chunk_ySize + z, 0.0f, 0.0f);
        }
        else
        {
            vertexDelete(16);
        }

        // Vertex #17
        if (ifVTX[1])
        {
            vertexInit(16, xx * chunk_xSize + x + 1, y, yy * chunk_ySize + z, 1.0f, 0.0f);
        }
        else
        {
            vertexDelete(17);
        }

        // Vertex #18
        if (ifVTX[3])
        {
            vertexInit(17, xx * chunk_xSize + x + 1, y + 1, yy * chunk_ySize + z, 1.0f, 1.0f);
        }
        else
        {
            vertexDelete(18);
        }

        // Vertex #19
        if (ifVTX[7])
        {
            vertexInit(18, xx * chunk_xSize + x + 1, y + 1, yy * chunk_ySize + z + 1, 1.0f, 1.0f);
        }
        else
        {
            vertexDelete(19);
        }

        // Vertex #20
        if (ifVTX[6])
        {
            vertexInit(19, xx * chunk_xSize + x, y + 1, yy * chunk_ySize + z + 1, 0.0f, 1.0f);
        }
        else
        {
            vertexDelete(20);
        }

        // Vertex #21
        if (ifVTX[5])
        {
            vertexInit(20, xx * chunk_xSize + x + 1, y, yy * chunk_ySize + z + 1, 1.0f, 0.0f);
        }
        else
        {
            vertexDelete(21);
        }

        // Vertex #22
        if (ifVTX[4])
        {
            vertexInit(21, xx * chunk_xSize + x, y, yy * chunk_ySize + z + 1, 0.0f, 0.0f);
        }
        else
        {
            vertexDelete(22);
        }

        // Vertex #23
        if (ifVTX[5])
        {
            vertexInit(22, xx * chunk_xSize + x + 1, y, yy * chunk_ySize + z + 1, 1.0f, 0.0f);
        }
        else
        {
            vertexDelete(23);
        }

        // Vertex #24
        if (ifVTX[6])
        {
            vertexInit(23, xx * chunk_xSize + x, y + 1, yy * chunk_ySize + z + 1, 0.0f, 1.0f);
        }
        else
        {
            vertexDelete(24);
        }

        // Vertex #25
        if (ifVTX[2])
        {
            vertexInit(24, xx * chunk_xSize + x, y + 1, yy * chunk_ySize + z, 1.0f, 1.0f);
        }
        else
        {
            vertexDelete(25);
        }

        // Vertex #26
        if (ifVTX[0])
        {
            vertexInit(25, xx * chunk_xSize + x, y, yy * chunk_ySize + z, 1.0f, 0.0f);
        }
        else
        {
            vertexDelete(26);
        }

        // Vertex #27
        if (ifVTX[4])
        {
            vertexInit(26, xx * chunk_xSize + x, y, yy * chunk_ySize + z + 1, 0.0f, 0.0f);
        }
        else
        {
            vertexDelete(27);
        }

        // Vertex #28
        if (ifVTX[4])
        {
            vertexInit(27, xx * chunk_xSize + x, y, yy * chunk_ySize + z + 1, 0.0f, 0.0f);
            VTXcount += 32;
        }
        else
        {
            vertexDelete(28);
        }

        // Vertex #29
        if (ifVTX[2])
        {
            vertexInit(28, xx * chunk_xSize + x, y + 1, yy * chunk_ySize + z, 1.0f, 1.0f);
        }
        else
        {
            vertexDelete(29);
        }

        // Vertex #30
        if (ifVTX[6])
        {
            vertexInit(29, xx * chunk_xSize + x, y + 1, yy * chunk_ySize + z + 1, 0.0f, 1.0f);
            VTXcount += 32;
        }
        else
        {
            vertexDelete(30);
        }

        // Vertex #31
        if (ifVTX[3])
        {
            vertexInit(30, xx * chunk_xSize + x + 1, y + 1, yy * chunk_ySize + z, 0.0f, 1.0f);
            VTXcount += 32;
        }
        else
        {
            vertexDelete(31);
        }

        // Vertex #32
        if (ifVTX[7])
        {
            vertexInit(31, xx * chunk_xSize + x + 1, y + 1, yy * chunk_ySize + z + 1, 1.0f, 1.0f);
        }
        else
        {
            vertexDelete(32);
        }

        // Vertex #33
        if (ifVTX[1])
        {
            vertexInit(32, xx * chunk_xSize + x + 1, y, yy * chunk_ySize + z, 0.0f, 0.0f);
        }
        else
        {
            vertexDelete(33);
        }

        // Vertex #34
        if (ifVTX[7])
        {
            vertexInit(33, xx * chunk_xSize + x + 1, y + 1, yy * chunk_ySize + z + 1, 1.0f, 1.0f);
            VTXcount += 40;
        }
        else
        {
            vertexDelete(34);
        }

        // Vertex #35
        if (ifVTX[5])
        {
            vertexInit(34, xx * chunk_xSize + x + 1, y, yy * chunk_ySize + z + 1, 1.0f, 0.0f);
            VTXcount += 40;
        }
        else
        {
            vertexDelete(35);
        }

        // Vertex #36
        if (ifVTX[1])
        {
            vertexInit(35, xx * chunk_xSize + x + 1, y, yy * chunk_ySize + z, 0.0f, 0.0f);
        }

        // The algorithm for generating 1 face
        auto faceInit = [&](/*int xx, int yy,*/ int rIndex0, int rIndex1, int rIndex2, int wIndex0, int wIndex1, int wIndex2)
        {
            worldChunkGeometry[{xx, yy}].faces[Fmask[wIndex0] + pFACcount] = Imask[rIndex0] + pVTXcountf;
            worldChunkGeometry[{xx, yy}].faces[Fmask[wIndex1] + pFACcount] = Imask[rIndex1] + pVTXcountf;
            worldChunkGeometry[{xx, yy}].faces[Fmask[wIndex2] + pFACcount] = Imask[rIndex2] + pVTXcountf;
            glm::vec3 edge1 = glm::vec3(
                worldChunkGeometry[{xx, yy}].vertices[Imask[wIndex1] * 8 + pVTXcount] - worldChunkGeometry[{xx, yy}].vertices[Imask[wIndex0] * 8 + pVTXcount],
                worldChunkGeometry[{xx, yy}].vertices[Imask[wIndex1] * 8 + pVTXcount + 1] - worldChunkGeometry[{xx, yy}].vertices[Imask[wIndex0] * 8 + pVTXcount + 1],
                worldChunkGeometry[{xx, yy}].vertices[Imask[wIndex1] * 8 + pVTXcount + 2] - worldChunkGeometry[{xx, yy}].vertices[Imask[wIndex0] * 8 + pVTXcount + 2]);
            glm::vec3 edge2 = glm::vec3(
                worldChunkGeometry[{xx, yy}].vertices[Imask[wIndex2] * 8 + pVTXcount] - worldChunkGeometry[{xx, yy}].vertices[Imask[wIndex0] * 8 + pVTXcount],
                worldChunkGeometry[{xx, yy}].vertices[Imask[wIndex2] * 8 + pVTXcount + 1] - worldChunkGeometry[{xx, yy}].vertices[Imask[wIndex0] * 8 + pVTXcount + 1],
                worldChunkGeometry[{xx, yy}].vertices[Imask[wIndex2] * 8 + pVTXcount + 2] - worldChunkGeometry[{xx, yy}].vertices[Imask[wIndex0] * 8 + pVTXcount + 2]);
            glm::vec3 norm = glm::cross(edge1, edge2);
            worldChunkGeometry[{xx, yy}].vertices[Imask[rIndex0] * 8 + pVTXcount + 3] = norm.x;
            worldChunkGeometry[{xx, yy}].vertices[Imask[rIndex1] * 8 + pVTXcount + 3] = norm.x;
            worldChunkGeometry[{xx, yy}].vertices[Imask[rIndex2] * 8 + pVTXcount + 3] = norm.x;
            worldChunkGeometry[{xx, yy}].vertices[Imask[rIndex0] * 8 + pVTXcount + 4] = norm.y;
            worldChunkGeometry[{xx, yy}].vertices[Imask[rIndex1] * 8 + pVTXcount + 4] = norm.y;
            worldChunkGeometry[{xx, yy}].vertices[Imask[rIndex2] * 8 + pVTXcount + 4] = norm.y;
            worldChunkGeometry[{xx, yy}].vertices[Imask[rIndex0] * 8 + pVTXcount + 5] = norm.z;
            worldChunkGeometry[{xx, yy}].vertices[Imask[rIndex1] * 8 + pVTXcount + 5] = norm.z;
            worldChunkGeometry[{xx, yy}].vertices[Imask[rIndex2] * 8 + pVTXcount + 5] = norm.z;
        };

        // Faces:
        // Face #1
        if (airBlocks[10])
        {
            faceInit(/*xx, yy,*/ 0, 1, 2, 0, 1, 2);
        }
        else
        {
            for (int i = 3; i < 36; i++)
            {
                Fmask[i] -= 3;
            }
        }

        // Face #2
        if (airBlocks[10])
        {
            faceInit(/*xx, yy,*/ 3, 4, 5, 3, 4, 5);
            FACcount += 6;
        }
        else
        {
            for (int i = 6; i < 36; i++)
            {
                Fmask[i] -= 3;
            }
        }

        // Face #3
        if (airBlocks[15])
        {
            faceInit(/*xx, yy,*/ 6, 7, 8, 6, 7, 8);
        }
        else
        {
            for (int i = 9; i < 36; i++)
            {
                Fmask[i] -= 3;
            }
        }

        // Face #4
        if (airBlocks[15])
        {
            faceInit(/*xx, yy,*/ 9, 10, 11, 9, 10, 11);
            FACcount += 6;
        }
        else
        {
            for (int i = 12; i < 36; i++)
            {
                Fmask[i] -= 3;
            }
        }

        // Face #5
        if (airBlocks[4])
        {
            faceInit(/*xx, yy,*/ 12, 13, 14, 12, 13, 14);
        }
        else
        {
            for (int i = 15; i < 36; i++)
            {
                Fmask[i] -= 3;
            }
        }

        // Face #6
        if (airBlocks[4])
        {
            faceInit(/*xx, yy,*/ 15, 16, 17, 15, 17, 16);
            FACcount += 6;
        }
        else
        {
            for (int i = 18; i < 36; i++)
            {
                Fmask[i] -= 3;
            }
        }

        // Face #7
        if (airBlocks[21])
        {
            faceInit(/*xx, yy,*/ 18, 19, 20, 18, 19, 20);
        }
        else
        {
            for (int i = 21; i < 36; i++)
            {
                Fmask[i] -= 3;
            }
        }

        // Face #8
        if (airBlocks[21])
        {
            faceInit(/*xx, yy,*/ 21, 22, 23, 21, 22, 23);
            FACcount += 6;
        }
        else
        {
            for (int i = 24; i < 36; i++)
            {
                Fmask[i] -= 3;
            }
        }

        // Face #9
        if (airBlocks[12])
        {
            faceInit(/*xx, yy,*/ 24, 25, 26, 24, 25, 26);
        }
        else
        {
            for (int i = 27; i < 36; i++)
            {
                Fmask[i] -= 3;
            }
        }

        // Face #10
        if (airBlocks[12])
        {
            faceInit(/*xx, yy,*/ 27, 28, 29, 27, 29, 28);
            FACcount += 6;
        }
        else
        {
            for (int i = 30; i < 36; i++)
            {
                Fmask[i] -= 3;
            }
        }

        // Face #11
        if (airBlocks[13])
        {
            faceInit(/*xx, yy,*/ 30, 31, 32, 30, 31, 32);
        }
        else
        {
            for (int i = 33; i < 36; i++)
            {
                Fmask[i] -= 3;
            }
        }

        // Face #12
        if (airBlocks[13])
        {
            faceInit(/*xx, yy,*/ 33, 34, 35, 33, 34, 35);
            FACcount += 6;
        }

        pFACcount = FACcount;
        pVTXcount = VTXcount;
        pVTXcountf = pVTXcount / 8;
    };

    // This code checks every block and it's neighbors for geometry optimizations (just calculating all worldChunkGeometry[{xx, yy}].faces and worldChunkGeometry[{xx, yy}].vertices)
    for (int x = 0; x < chunk_xSize; x++)
    {
        for (int z = 0; z < chunk_ySize; z++)
        {
            for (int y = 1; y < chunk_zSize - 1; y++)
            {
                // Calculate relative coords
                if (worldChunkMap[{xx, yy}].ID[x][y][z] > 0)
                {
                    if (x + 1 > chunk_xSize - 1)
                    {
                        cpx = xx + 1;
                        px = 0;
                    }
                    else
                    {
                        cpx = xx;
                        px = x + 1;
                    }
                    if (x - 1 < 0)
                    {
                        cnx = xx - 1;
                        nx = chunk_xSize - 1;
                    }
                    else
                    {
                        cnx = xx;
                        nx = x - 1;
                    }
                    if (z + 1 > chunk_ySize - 1)
                    {
                        cpy = yy + 1;
                        pz = 0;
                    }
                    else
                    {
                        cpy = yy;
                        pz = z + 1;
                    }
                    if (z - 1 < 0)
                    {
                        cny = yy - 1;
                        nz = chunk_ySize - 1;
                    }
                    else
                    {
                        cny = yy;
                        nz = z - 1;
                    }

                    // Check neigbors
                    airBlocks[0] = worldChunkMap[{cnx, cny}].ID[nx][y - 1][nz] == 0;
                    airBlocks[1] = worldChunkMap[{xx, cny}].ID[x][y - 1][nz] == 0;
                    airBlocks[2] = worldChunkMap[{cpx, cny}].ID[px][y - 1][nz] == 0;
                    airBlocks[3] = worldChunkMap[{cnx, cny}].ID[nx][y][nz] == 0;
                    airBlocks[4] = worldChunkMap[{xx, cny}].ID[x][y][nz] == 0;
                    airBlocks[5] = worldChunkMap[{cpx, cny}].ID[px][y][nz] == 0;
                    airBlocks[6] = worldChunkMap[{cnx, cny}].ID[nx][y + 1][nz] == 0;
                    airBlocks[7] = worldChunkMap[{xx, cny}].ID[x][y + 1][nz] == 0;
                    airBlocks[8] = worldChunkMap[{cpx, cny}].ID[px][y + 1][nz] == 0;
                    airBlocks[9] = worldChunkMap[{cnx, yy}].ID[nx][y - 1][z] == 0;
                    airBlocks[10] = worldChunkMap[{xx, yy}].ID[x][y - 1][z] == 0;
                    airBlocks[11] = worldChunkMap[{cpx, yy}].ID[px][y - 1][z] == 0;
                    airBlocks[12] = worldChunkMap[{cnx, yy}].ID[nx][y][z] == 0;
                    airBlocks[13] = worldChunkMap[{cpx, yy}].ID[px][y][z] == 0;
                    airBlocks[14] = worldChunkMap[{cnx, yy}].ID[nx][y + 1][z] == 0;
                    airBlocks[15] = worldChunkMap[{xx, yy}].ID[x][y + 1][z] == 0;
                    airBlocks[16] = worldChunkMap[{cpx, yy}].ID[px][y + 1][z] == 0;
                    airBlocks[17] = worldChunkMap[{cnx, cpy}].ID[nx][y - 1][pz] == 0;
                    airBlocks[18] = worldChunkMap[{xx, cpy}].ID[x][y - 1][pz] == 0;
                    airBlocks[19] = worldChunkMap[{cpx, cpy}].ID[px][y - 1][pz] == 0;
                    airBlocks[20] = worldChunkMap[{cnx, cpy}].ID[nx][y][pz] == 0;
                    airBlocks[21] = worldChunkMap[{xx, cpy}].ID[x][y][pz] == 0;
                    airBlocks[22] = worldChunkMap[{cpx, cpy}].ID[px][y][pz] == 0;
                    airBlocks[23] = worldChunkMap[{cnx, cpy}].ID[nx][y + 1][pz] == 0;
                    airBlocks[24] = worldChunkMap[{xx, cpy}].ID[x][y + 1][pz] == 0;
                    airBlocks[25] = worldChunkMap[{cpx, cpy}].ID[px][y + 1][pz] == 0;

                    // Calculate geometry for the block
                    calculateGeometry(x, y, z);
                }
            }
        }
    }
    for (int x = 0; x < chunk_xSize; x++)
    {
        for (int z = 0; z < chunk_ySize; z++)
        {
            if (worldChunkMap[{xx, yy}].ID[x][0][z] > 0)
            {
                // Calculate relative coords
                if (x + 1 > chunk_xSize - 1)
                {
                    cpx = xx + 1;
                    px = 0;
                }
                else
                {
                    cpx = xx;
                    px = x + 1;
                }
                if (x - 1 < 0)
                {
                    cnx = xx - 1;
                    nx = chunk_xSize - 1;
                }
                else
                {
                    cnx = xx;
                    nx = x - 1;
                }
                if (z + 1 > chunk_ySize - 1)
                {
                    cpy = yy + 1;
                    pz = 0;
                }
                else
                {
                    cpy = yy;
                    pz = z + 1;
                }
                if (z - 1 < 0)
                {
                    cny = yy - 1;
                    nz = chunk_ySize - 1;
                }
                else
                {
                    cny = yy;
                    nz = z - 1;
                }

                // Check neigbors
                airBlocks[0] = true;
                airBlocks[1] = true;
                airBlocks[2] = true;
                airBlocks[3] = worldChunkMap[{cnx, cny}].ID[nx][0][nz] == 0;
                airBlocks[4] = worldChunkMap[{xx, cny}].ID[x][0][nz] == 0;
                airBlocks[5] = worldChunkMap[{cpx, cny}].ID[px][0][nz] == 0;
                airBlocks[6] = worldChunkMap[{cnx, cny}].ID[nx][1][nz] == 0;
                airBlocks[7] = worldChunkMap[{xx, cny}].ID[x][1][nz] == 0;
                airBlocks[8] = worldChunkMap[{cpx, cny}].ID[px][1][nz] == 0;
                airBlocks[9] = true;
                airBlocks[10] = true;
                airBlocks[11] = true;
                airBlocks[12] = worldChunkMap[{cnx, yy}].ID[nx][0][z] == 0;
                airBlocks[13] = worldChunkMap[{cpx, yy}].ID[px][0][z] == 0;
                airBlocks[14] = worldChunkMap[{cnx, yy}].ID[nx][1][z] == 0;
                airBlocks[15] = worldChunkMap[{xx, yy}].ID[x][1][z] == 0;
                airBlocks[16] = worldChunkMap[{cpx, yy}].ID[px][1][z] == 0;
                airBlocks[17] = true;
                airBlocks[18] = true;
                airBlocks[19] = true;
                airBlocks[20] = worldChunkMap[{cnx, cpy}].ID[nx][0][pz] == 0;
                airBlocks[21] = worldChunkMap[{xx, cpy}].ID[x][0][pz] == 0;
                airBlocks[22] = worldChunkMap[{cpx, cpy}].ID[px][0][pz] == 0;
                airBlocks[23] = worldChunkMap[{cnx, cpy}].ID[nx][1][pz] == 0;
                airBlocks[24] = worldChunkMap[{xx, cpy}].ID[x][1][pz] == 0;
                airBlocks[25] = worldChunkMap[{cpx, cpy}].ID[px][1][pz] == 0;

                // Calculate geometry for the block
                calculateGeometry(x, 0, z);
            }
        }
    }

    for (int x = 0; x < chunk_xSize; x++)
    {
        for (int z = 0; z < chunk_ySize; z++)
        {
            if (worldChunkMap[{xx, yy}].ID[x][chunk_zSize - 1][z] > 0)
            {
                // Calculate relative coords
                if (x + 1 > chunk_xSize - 1)
                {
                    cpx = xx + 1;
                    px = 0;
                }
                else
                {
                    cpx = xx;
                    px = x + 1;
                }
                if (x - 1 < 0)
                {
                    cnx = xx - 1;
                    nx = chunk_xSize - 1;
                }
                else
                {
                    cnx = xx;
                    nx = x - 1;
                }
                if (z + 1 > chunk_ySize - 1)
                {
                    cpy = yy + 1;
                    pz = 0;
                }
                else
                {
                    cpy = yy;
                    pz = z + 1;
                }
                if (z - 1 < 0)
                {
                    cny = yy - 1;
                    nz = chunk_ySize - 1;
                }
                else
                {
                    cny = yy;
                    nz = z - 1;
                }

                // Check neigbors
                airBlocks[0] = worldChunkMap[{cnx, cny}].ID[nx][chunk_zSize - 2][nz] == 0;
                airBlocks[1] = worldChunkMap[{xx, cny}].ID[x][chunk_zSize - 2][nz] == 0;
                airBlocks[2] = worldChunkMap[{cpx, cny}].ID[px][chunk_zSize - 2][nz] == 0;
                airBlocks[3] = worldChunkMap[{cnx, cny}].ID[nx][chunk_zSize - 1][nz] == 0;
                airBlocks[4] = worldChunkMap[{xx, cny}].ID[x][chunk_zSize - 1][nz] == 0;
                airBlocks[5] = worldChunkMap[{cpx, cny}].ID[px][chunk_zSize - 1][nz] == 0;
                airBlocks[6] = true;
                airBlocks[7] = true;
                airBlocks[8] = true;
                airBlocks[9] = worldChunkMap[{cnx, yy}].ID[nx][chunk_zSize - 2][z] == 0;
                airBlocks[10] = worldChunkMap[{xx, yy}].ID[x][chunk_zSize - 2][z] == 0;
                airBlocks[11] = worldChunkMap[{cpx, yy}].ID[px][chunk_zSize - 2][z] == 0;
                airBlocks[12] = worldChunkMap[{cnx, yy}].ID[nx][chunk_zSize - 1][z] == 0;
                airBlocks[13] = worldChunkMap[{cpx, yy}].ID[px][chunk_zSize - 1][z] == 0;
                airBlocks[14] = true;
                airBlocks[15] = true;
                airBlocks[16] = true;
                airBlocks[17] = worldChunkMap[{cnx, cpy}].ID[nx][chunk_zSize - 2][pz] == 0;
                airBlocks[18] = worldChunkMap[{xx, cpy}].ID[x][chunk_zSize - 2][pz] == 0;
                airBlocks[19] = worldChunkMap[{cpx, cpy}].ID[px][chunk_zSize - 2][pz] == 0;
                airBlocks[20] = worldChunkMap[{cnx, cpy}].ID[nx][chunk_zSize - 1][pz] == 0;
                airBlocks[21] = worldChunkMap[{xx, cpy}].ID[x][chunk_zSize - 1][pz] == 0;
                airBlocks[22] = worldChunkMap[{cpx, cpy}].ID[px][chunk_zSize - 1][pz] == 0;
                airBlocks[23] = true;
                airBlocks[24] = true;
                airBlocks[25] = true;

                // Calculate geometry for the block
                calculateGeometry(x, chunk_zSize - 1, z);
            }
        }
    }

    worldChunkState[{xx, yy}].store(3);
}