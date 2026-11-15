// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <iostream>
// #include <unordered_map>
// #include <atomic>

// Globals
#include "globals.hpp"
// Game engine
#include "engine.hpp"

void Engine::delChunkMap(int &&xxxx, int &&yyyy)
{
    int xx = xxxx;
    int yy = yyyy;
    // Empty the chunk
    for (int x = 0; x < chunk_xSize; x++)
    {
        for (int y = 0; y < chunk_zSize; y++)
        {
            for (int z = 0; z < chunk_ySize; z++)
            {
                worldChunkMap[{xx, yy}].ID[x][y][z] = 0;
                worldChunkMap[{xx, yy}].vtxTag[x][y][z] = 0;
                worldChunkMap[{xx, yy}].facTag[x][y][z] = 0;
                worldChunkMap[{xx, yy}].properties[x][y][z] = "";
            }
        }
    }

    worldChunkState[{xx, yy}].store(0);
}