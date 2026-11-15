// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <iostream>
// #include <thread>
// #include <vector>
// #include <set>
// #include <unordered_map>
// #include <atomic>

// Globals
#include "globals.hpp"
// Game engine
#include "engine.hpp"

void Engine::manageChunks()
{

    // Chunk geometry
    for (auto &pair : worldChunkState)
    {
        // Abort process if there are still chunks working.
        if (worldChunkState[pair.first].load() == -1)
            return;
    }

    // Erase chunk elements.
    // Chunk map
    int howManyMustDel = 0;
    std::vector<std::pair<int, int>> mustDelete;
    for (auto &pair : worldChunkMap)
    {
        int xx = pair.first.first;
        int yy = pair.first.second;
        if (((xx < (int)floor(playerX) / chunk_xSize - (renderDistance + 2) || xx > (int)floor(playerX) / chunk_xSize + (renderDistance + 2)) ||
             (yy < (int)floor(playerY) / chunk_ySize - (renderDistance + 2) || yy > (int)floor(playerY) / chunk_ySize + (renderDistance + 2))) &&
            worldChunkState[pair.first].load() == 0)
        {
            howManyMustDel++;
            mustDelete.resize(howManyMustDel);
            mustDelete[howManyMustDel - 1] = pair.first;
        }
    }
    for (int i = 0; i < howManyMustDel; i++)
    {
        worldChunkMap.erase(mustDelete[i]);
    }
    // Chunk state
    howManyMustDel = 0;
    mustDelete.clear();
    for (auto &pair : worldChunkState)
    {
        int xx = pair.first.first;
        int yy = pair.first.second;
        if (((xx < (int)floor(playerX) / chunk_xSize - (renderDistance + 2) || xx > (int)floor(playerX) / chunk_xSize + (renderDistance + 2)) ||
             (yy < (int)floor(playerY) / chunk_ySize - (renderDistance + 2) || yy > (int)floor(playerY) / chunk_ySize + (renderDistance + 2))) &&
            worldChunkState[pair.first].load() == 0)
        {
            howManyMustDel++;
            mustDelete.resize(howManyMustDel);
            mustDelete[howManyMustDel - 1] = pair.first;
        }
        else
        {
            if (pair.second.load() < 5)
                pair.second.store(pair.second.load() + 4);
        }
    }
    for (int i = 0; i < howManyMustDel; i++)
    {
        worldChunkState.erase(mustDelete[i]);
    }
    // Chunk geometry
    howManyMustDel = 0;
    mustDelete.clear();
    for (auto &pair : worldChunkGeometry)
    {
        int xx = pair.first.first;
        int yy = pair.first.second;
        if (((xx < (int)floor(playerX) / chunk_xSize - renderDistance || xx > (int)floor(playerX) / chunk_xSize + renderDistance) ||
             (yy < (int)floor(playerY) / chunk_ySize - renderDistance || yy > (int)floor(playerY) / chunk_ySize + renderDistance)) &&
            worldChunkState[pair.first].load() == 0)
        {
            howManyMustDel++;
            mustDelete.resize(howManyMustDel);
            mustDelete[howManyMustDel - 1] = pair.first;
        }
    }
    for (int i = 0; i < howManyMustDel; i++)
    {
        worldChunkGeometry.erase(mustDelete[i]);
    }
    // Chunk GL buffers
    howManyMustDel = 0;
    mustDelete.clear();
    for (auto &pair : worldChunkVBO)
    {
        int xx = pair.first.first;
        int yy = pair.first.second;
        if (((xx < (int)floor(playerX) / chunk_xSize - renderDistance || xx > (int)floor(playerX) / chunk_xSize + renderDistance) ||
             (yy < (int)floor(playerY) / chunk_ySize - renderDistance || yy > (int)floor(playerY) / chunk_ySize + renderDistance + 2)) &&
            worldChunkState[pair.first].load() <= 7)
        {
            howManyMustDel++;
            mustDelete.resize(howManyMustDel);
            mustDelete[howManyMustDel - 1] = pair.first;
        }
    }
    for (int i = 0; i < howManyMustDel; i++)
    {
        worldChunkVBO.erase(mustDelete[i]);
        worldChunkVAO.erase(mustDelete[i]);
        worldChunkEBO.erase(mustDelete[i]);
    }
    // Poll for and process events
    glfwPollEvents();

    // Create elements.
    // Chunk map
    for (int xx = (int)floor(playerX) / chunk_xSize - (renderDistance + 2); xx < (int)floor(playerX) / chunk_xSize + (renderDistance + 2); xx++)
    {
        for (int yy = (int)floor(playerY) / chunk_ySize - (renderDistance + 2); yy < (int)floor(playerY) / chunk_ySize + (renderDistance + 2); yy++)
        {
            if (worldChunkState[{xx, yy}].load() == 0)
            {
                worldChunkMap[{xx, yy}] = Chunk();
                worldChunkState[{xx, yy}].store(0);
            }
        }
    }
    // Chunk geometry
    for (int xx = (int)floor(playerX) / chunk_xSize - renderDistance; xx < (int)floor(playerX) / chunk_xSize + renderDistance; xx++)
    {
        for (int yy = (int)floor(playerY) / chunk_ySize - renderDistance; yy < (int)floor(playerY) / chunk_ySize + renderDistance; yy++)
        {
            if (worldChunkState[{xx, yy}].load() == 5)
            {
                worldChunkGeometry[{xx, yy}] = Mesh();
            }
        }
    }
    // Chunk geometry allocation
    for (int xx = (int)floor(playerX) / chunk_xSize - renderDistance; xx < (int)floor(playerX) / chunk_xSize + renderDistance; xx++)
    {
        for (int yy = (int)floor(playerY) / chunk_ySize - renderDistance; yy < (int)floor(playerY) / chunk_ySize + renderDistance; yy++)
        {
            if (worldChunkState[{xx, yy}].load() == 6)
            {
            }
        }
    }
    // Chunk GL buffers
    for (int xx = (int)floor(playerX) / chunk_xSize - renderDistance; xx < (int)floor(playerX) / chunk_xSize + renderDistance; xx++)
    {
        for (int yy = (int)floor(playerY) / chunk_ySize - renderDistance; yy < (int)floor(playerY) / chunk_ySize + renderDistance; yy++)
        {
            if (worldChunkState[{xx, yy}].load() == 7)
            {
                worldChunkVBO[{xx, yy}] = 0;
                worldChunkVAO[{xx, yy}] = 0;
                worldChunkEBO[{xx, yy}] = 0;
            }
        }
    }
    // Poll for and process events
    glfwPollEvents();

    // Assign for each element a new thread.
    // For filling.
    // Chunk map
    for (int xx = (int)floor(playerX) / chunk_xSize - (renderDistance + 2); xx < (int)floor(playerX) / chunk_xSize + (renderDistance + 2); xx++)
    {
        for (int yy = (int)floor(playerY) / chunk_ySize - (renderDistance + 2); yy < (int)floor(playerY) / chunk_ySize + (renderDistance + 2); yy++)
        {
            if (worldChunkState[{xx, yy}].load() == 0)
            {
                // Announce area is in work.
                worldChunkState[{xx, yy}].store(-1);
                std::thread thread = std::thread(&Engine::genChunkMap, this, xx, yy);
                thread.detach();
            }
        }
    }
    // Chunk geometry size
    for (int xx = (int)floor(playerX) / chunk_xSize - renderDistance; xx < (int)floor(playerX) / chunk_xSize + renderDistance; xx++)
    {
        for (int yy = (int)floor(playerY) / chunk_ySize - renderDistance; yy < (int)floor(playerY) / chunk_ySize + renderDistance; yy++)
        {

            if (worldChunkState[{xx - 1, yy}].load() >= 5 &&
                worldChunkState[{xx, yy - 1}].load() >= 5 && worldChunkState[{xx, yy}].load() == 5 && worldChunkState[{xx, yy + 1}].load() >= 5 &&
                worldChunkState[{xx + 1, yy}].load() >= 5)
            {
                // Announce area is in work.
                worldChunkState[{xx, yy}].store(-1);
                std::thread thread = std::thread(&Engine::calChunkGeometrySize, this, xx, yy);
                thread.detach();
            }
        }
    }
    // Chunk geometry
    for (int xx = (int)floor(playerX) / chunk_xSize - renderDistance; xx < (int)floor(playerX) / chunk_xSize + renderDistance; xx++)
    {
        for (int yy = (int)floor(playerY) / chunk_ySize - renderDistance; yy < (int)floor(playerY) / chunk_ySize + renderDistance; yy++)
        {
            if (worldChunkState[{xx - 1, yy}].load() >= 5 &&
                worldChunkState[{xx, yy - 1}].load() >= 5 && worldChunkState[{xx, yy}].load() == 6 && worldChunkState[{xx, yy + 1}].load() >= 5 &&
                worldChunkState[{xx + 1, yy}].load() >= 5)
            {
                // Announce area is in work.
                worldChunkState[{xx, yy}]
                    .store(-1);
                std::thread thread = std::thread(&Engine::genChunkGeometry, this, xx, yy);
                thread.detach();
            }
        }
    }
    // Chunk GL buffers
    for (int xx = (int)floor(playerX) / chunk_xSize - renderDistance; xx < (int)floor(playerX) / chunk_xSize + renderDistance; xx++)
    {
        for (int yy = (int)floor(playerY) / chunk_ySize - renderDistance; yy < (int)floor(playerY) / chunk_ySize + renderDistance; yy++)
        {
            if (worldChunkState[{xx, yy}].load() == 7)
            {
                // Announce area is in work.
                worldChunkState[{xx, yy}].store(-1);
                genChunkGLBuffers((const int)xx, (const int)yy);
            }
        }
    }
    // Poll for and process events
    glfwPollEvents();

    // For deletion
    // Chunk map
    for (auto &pair : worldChunkMap)
    {
        int xx = pair.first.first;
        int yy = pair.first.second;
        // Check if in desired area.
        if (((xx < (int)floor(playerX) / chunk_xSize - (renderDistance + 2) || xx > (int)floor(playerX) / chunk_xSize + (renderDistance + 2)) ||
             (yy < (int)floor(playerY) / chunk_ySize - (renderDistance + 2) || yy > (int)floor(playerY) / chunk_ySize + (renderDistance + 2))) &&
            worldChunkState[pair.first].load() == 5)
        {
            // Announce area is in work.
            worldChunkState[pair.first].store(-1);
            std::thread thread = std::thread(&Engine::delChunkMap, this, xx, yy);
            thread.detach();
        }
    }
    // Chunk geometry
    for (auto &pair : worldChunkGeometry)
    {
        int xx = pair.first.first;
        int yy = pair.first.second;

        // More checking then usuall.
        // Check if in desired area.
        if (((xx < (int)floor(playerX) / chunk_xSize - renderDistance || xx > (int)floor(playerX) / chunk_xSize + renderDistance) ||
             (yy < (int)floor(playerY) / chunk_ySize - renderDistance || yy > (int)floor(playerY) / chunk_ySize + renderDistance)) &&
            worldChunkState[pair.first].load() == 7)
        {
            // Announce area is in work.
            worldChunkState[pair.first].store(-1);
            std::thread thread = std::thread(&Engine::delChunkGeometry, this, xx, yy);
            thread.detach();
        }
    }
    // Chunk GL buffers
    for (auto &pair : worldChunkVBO)
    {
        int xx = pair.first.first;
        int yy = pair.first.second;
        // Check if in desired area.
        if (((xx < (int)floor(playerX) / chunk_xSize - renderDistance || xx > (int)floor(playerX) / chunk_xSize + renderDistance) ||
             (yy < (int)floor(playerY) / chunk_ySize - renderDistance || yy > (int)floor(playerY) / chunk_ySize + renderDistance)) &&
            worldChunkState[pair.first].load() == 8)
        {
            // Announce area is in work.
            worldChunkState[pair.first].store(-1);
            delChunkGLBuffers((const int)xx, (const int)yy);
        }
    }
    // Poll for and process events
    glfwPollEvents();
}