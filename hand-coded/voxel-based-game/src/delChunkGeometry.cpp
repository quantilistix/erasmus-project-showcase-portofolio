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

// This function transforms the world data to geometry
void Engine::delChunkGeometry(int &&xxxx, int &&yyyy)
{
    int xx = xxxx;
    int yy = yyyy;
    // Now we can finally set up the worldChunkGeometry[{xx, yy}].vertices and worldChunkGeometry[{xx, yy}].faces
    if (worldChunkGeometry[{xx, yy}].vertices != nullptr || worldChunkGeometry[{xx, yy}].vtx_count == 0)
    {
        try
        {
            delete[] worldChunkGeometry[{xx, yy}].vertices;
        }
        catch (std::exception &e)
        {
            std::cerr << "Exception while deleting vertices: " << e.what() << std::endl;
        }
        worldChunkGeometry[{xx, yy}].vertices = nullptr;
        worldChunkGeometry[{xx, yy}].vtx_count = 0;
    }
    if (worldChunkGeometry[{xx, yy}].faces != nullptr || worldChunkGeometry[{xx, yy}].fac_count == 0)
    {
        try
        {
            delete[] worldChunkGeometry[{xx, yy}].faces;
        }
        catch (std::exception &e)
        {
            std::cerr << "Exception while deleting faces: " << e.what() << std::endl;
        }
        worldChunkGeometry[{xx, yy}].faces = nullptr;
        worldChunkGeometry[{xx, yy}].fac_count = 0;
    }

    worldChunkState[{xx, yy}].store(1);
}