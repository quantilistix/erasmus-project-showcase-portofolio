// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <iostream>
// #include <cmath>
// #include <unordered_map>
// #include <atomic>

// // Include OpenGL libraries
// // #include <GLEW/glew.h>
// #include "glad/glad.h"
// #include <KHR/khrplatform.h>

// // Include OpenGL math library (GLM)
// #include "glm/glm.hpp"
// #include "glm/gtc/matrix_transform.hpp"
// #include "glm/gtc/type_ptr.hpp"

// Globals
#include "globals.hpp"
// Game engine
#include "engine.hpp"

// This function loads the engine with geometry data and initializes everything
void Engine::genChunkGLBuffers(int &&xxxx, int &&yyyy)
{
    int xx = xxxx;
    int yy = yyyy;
    worldChunkVBO[{xx, yy}] = 0;
    worldChunkVAO[{xx, yy}] = 0;
    worldChunkEBO[{xx, yy}] = 0;

    // Generate vertex array object
    glGenVertexArrays(1, &worldChunkVAO[{xx, yy}]);

    // Generate vertex buffer object
    glGenBuffers(1, &worldChunkVBO[{xx, yy}]);

    // Generate element buffer object
    glGenBuffers(1, &worldChunkEBO[{xx, yy}]);

    // Bind the vertex array
    glBindVertexArray(worldChunkVAO[{xx, yy}]);

    // Bind the vertex buffer
    glBindBuffer(GL_ARRAY_BUFFER, worldChunkVBO[{xx, yy}]);
    // Fill the buffer with data
    glBufferData(GL_ARRAY_BUFFER, worldChunkGeometry[{xx, yy}].vtx_count * sizeof(float) * 8, worldChunkGeometry[{xx, yy}].vertices, GL_DYNAMIC_DRAW);
    // Link and enable vertex attributes
    // Vertex position
    glVertexAttribPointer(0, 3, GL_FLOAT, GL_FALSE, 8 * sizeof(float), (const void *)(0 * sizeof(float)));
    glEnableVertexAttribArray(0);
    // Surface normal
    glVertexAttribPointer(1, 3, GL_FLOAT, GL_FALSE, 8 * sizeof(float), (const void *)(3 * sizeof(float)));
    glEnableVertexAttribArray(1);
    // UV coordinates
    glVertexAttribPointer(2, 2, GL_FLOAT, GL_FALSE, 8 * sizeof(float), (const void *)(6 * sizeof(float)));
    glEnableVertexAttribArray(2);

    // Bind the element buffer
    glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, worldChunkEBO[{xx, yy}]);
    // Fill the buffer with data
    glBufferData(GL_ELEMENT_ARRAY_BUFFER, worldChunkGeometry[{xx, yy}].fac_count * sizeof(int) * 3, worldChunkGeometry[{xx, yy}].faces, GL_DYNAMIC_DRAW);

    // std::cout << "Data done!" << std::endl;

    worldChunkState[{xx, yy}].store(4);
}