// Dependencies
#include <pch.hpp>
// // Include standard libraries
// #include <iostream>
// #include <cmath>
// #include <unordered_map>
// #include <atomic>

// Globals
#include "globals.hpp"
// Include perlin noise
#include "math.hpp"
// Game engine
#include "engine.hpp"

void Engine::genChunkMap(int &&xxxx, int &&yyyy)
{
    int xx = xxxx;
    int yy = yyyy;
    // Set dimension to overworld
    worldChunkMap[{xx, yy}].dim = 0x00;
    for (int x = 0; x < chunk_xSize; x++)
    {
        for (int y = 0; y < chunk_zSize; y++)
        {
            for (int z = 0; z < chunk_ySize; z++)
            {
                worldChunkMap[{xx, yy}].ID[x][y][z] = 0;
                worldChunkMap[{xx, yy}].vtxTag[x][y][z] = 0;
                worldChunkMap[{xx, yy}].facTag[x][y][z] = 0;
                worldChunkMap[{xx, yy}].properties[x][y][z] = std::string("");
            }
        }
    }
    // Generating world terrain
    for (int x = 0; x < chunk_xSize; x++)
    {
        for (int y = 0; y < chunk_ySize; y++)
        {

            // std::cout << perlin(123, xx + x / chunk_xSize, yy + y / chunk_ySize) << std::endl;
            float smallSpikes = perlin(123, ((float)xx + (float)x / (float)chunk_xSize) / 1.0f, ((float)yy + (float)y / (float)chunk_ySize) / 1.0f) * 1.0f + 5.0f;
            float smallHills = perlin(234, ((float)xx + (float)x / (float)chunk_xSize) / 3.0f, ((float)yy + (float)y / (float)chunk_ySize) / 3.0f) * 8.0f + 10.0f;
            float bigMountains = perlin(345, ((float)xx + (float)x / (float)chunk_xSize) / 8.0f, ((float)yy + (float)y / (float)chunk_ySize) / 8.0f) * 10.0f;
            float temperature = perlin(456, ((float)xx + (float)x / (float)chunk_xSize) / 16.0f, ((float)yy + (float)y / (float)chunk_ySize) / 16.0f) + 2.0f;
            int height = (int)floor(smallSpikes * temperature * temperature * temperature + smallHills + bigMountains * bigMountains * sqrt(temperature)) + 42;
            worldChunkMap[{xx, yy}].ID[x][0][y] = 4;
            if (height > chunk_zSize)
                height = chunk_zSize;
            if (height < 0)
                height = 0;

            for (int z = 1; z < height - 3; z++)
            {
                worldChunkMap[{xx, yy}].ID[x][z][y] = 3;
            }
            if (height < chunk_zSize && height - 3 > 0)
            {
                if (height > 32 + (int)floor(smallSpikes * temperature * temperature * temperature * 2) ||
                    height > 84 + (int)floor(smallSpikes * temperature * temperature * temperature))
                {
                    worldChunkMap[{xx, yy}].ID[x][height - 3][y] = 3;
                    worldChunkMap[{xx, yy}].ID[x][height - 2][y] = 3;
                    worldChunkMap[{xx, yy}].ID[x][height - 1][y] = 3;
                }
                else if (height > 75)
                {
                    worldChunkMap[{xx, yy}].ID[x][height - 3][y] = 2;
                    worldChunkMap[{xx, yy}].ID[x][height - 2][y] = 2;
                    worldChunkMap[{xx, yy}].ID[x][height - 1][y] = 1;
                }
                else
                {
                    worldChunkMap[{xx, yy}].ID[x][height - 3][y] = 11;
                    worldChunkMap[{xx, yy}].ID[x][height - 2][y] = 11;
                    worldChunkMap[{xx, yy}].ID[x][height - 1][y] = 11;
                }
            }
            // Add water
            for (int z = height; z < 72; z++)
            {
                worldChunkMap[{xx, yy}].ID[x][z][y] = 10;
            }
        }
    }
    // Random blocks in the air
    worldChunkMap[{xx, yy}].ID[7][255][15] = 1;
    worldChunkMap[{xx, yy}].ID[15][255][7] = 2;
    worldChunkMap[{xx, yy}].ID[7][255][0] = 3;
    worldChunkMap[{xx, yy}].ID[0][255][7] = 4;

    worldChunkMap[{xx, yy}].ID[7][127][15] = 5;
    worldChunkMap[{xx, yy}].ID[15][127][7] = 6;
    worldChunkMap[{xx, yy}].ID[7][127][0] = 7;
    worldChunkMap[{xx, yy}].ID[0][127][7] = 8;

    worldChunkMap[{xx, yy}].ID[7][129][15] = 9;
    worldChunkMap[{xx, yy}].ID[15][129][7] = 10;
    worldChunkMap[{xx, yy}].ID[7][129][0] = 11;
    worldChunkMap[{xx, yy}].ID[0][129][7] = 10;
    worldChunkState[{xx, yy}].store(1);
}
