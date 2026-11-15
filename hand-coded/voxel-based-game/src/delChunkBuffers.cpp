// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <iostream>
// #include <unordered_map>
// #include <atomic>

// // Include OpenGL libraries
// // #include <GLEW/glew.h>
// #include "glad/glad.h"
// #include <KHR/khrplatform.h>

// Globals
#include "globals.hpp"
// Game engine
#include "engine.hpp"

// This function loads the engine with geometry data and initializes everything
void Engine::delChunkGLBuffers(int &&xxxx, int &&yyyy)
{
    int xx = xxxx;
    int yy = yyyy;
    // Generate vertex array object
    glDeleteVertexArrays(1, &worldChunkVAO[{xx, yy}]);

    // Generate vertex buffer object
    glDeleteBuffers(1, &worldChunkVBO[{xx, yy}]);

    // Generate element buffer object
    glDeleteBuffers(1, &worldChunkEBO[{xx, yy}]);

    worldChunkVBO[{xx, yy}] = 0;
    worldChunkVAO[{xx, yy}] = 0;
    worldChunkEBO[{xx, yy}] = 0;

    worldChunkState[{xx, yy}].store(3);
}