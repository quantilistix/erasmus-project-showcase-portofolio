// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <iostream>
// #include <cmath>
// #include <thread>
// #include <unordered_map>
// #include <set>
// #include <vector>
// #include <atomic>

// // Include OpenGL libraries
// // #include <GLEW/glew.h>
// #include "glad/glad.h"
// #include <KHR/khrplatform.h>
// #include <GLFW/glfw3.h>

// // Include OpenGL math library (GLM)
// #include "glm/glm.hpp"
// #include "glm/gtc/matrix_transform.hpp"
// #include "glm/gtc/type_ptr.hpp"

// Globals
#include "globals.hpp"
// Shader class
#include "shaderLoader.hpp"
// Game engine
#include "engine.hpp"

// Block functions
void BlockFunc::none()
{
}
void BlockFunc::grass_block()
{
}
void BlockFunc::door()
{
}
void BlockFunc::trapdoor()
{
}
void BlockFunc::tnt()
{
}
void BlockFunc::gate()
{
}
void BlockFunc::bed()
{
}
void BlockFunc::workbench()
{
}
void BlockFunc::furnace()
{
}

Engine::Engine(GLFWwindow *aWindow, char *renderVertexShader, char *renderFragmentShader, char *postVertexShader, char *postFragmentShader, int samples)
    : renderShader(renderVertexShader, renderFragmentShader, "render"),
      postProcessingShader(postVertexShader, postFragmentShader, "postProcess")
{
    // Init window
    // Initializing window
    window = aWindow;

    // Generate texture buffer
    glGenTextures(1, &textureAtlas);

    // Enable tests
    // Enable multisampling
    glEnable(GL_MULTISAMPLE);

    // Enable alpha test
    glEnable(GL_ALPHA_TEST);
    glAlphaFunc(GL_GREATER, 0);

    // Depth buffer initialization
    // Enable depth buffer
    glEnable(GL_DEPTH_TEST);
    // Set depth correctly
    glDepthFunc(GL_LESS);
    glDepthMask(GL_TRUE);
    glDepthRange(0.0, 1.0);

    // Enable stencil test
    glEnable(GL_STENCIL_TEST);
    glStencilMask(0xFF);
    glStencilFunc(GL_ALWAYS, 1, 0xFF);
    glStencilMask(0xFF);

    // Enable blend test
    glEnable(GL_BLEND);
    glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
    glBlendFuncSeparate(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA, GL_ONE, GL_ZERO);
    glBlendEquation(GL_FUNC_ADD);

    // Enable culling
    glEnable(GL_CULL_FACE);
    glCullFace(GL_BACK);

    // Generate custom framebuffer
    // Get framebuffer size
    glfwGetFramebufferSize(window, &windowWidth, &windowHeight);

    // Generate frame buffer
    glGenFramebuffers(1, &screenFBO);
    // Bind frame buffer
    glBindFramebuffer(GL_FRAMEBUFFER, screenFBO);

    // Generate screen texture buffer
    glGenTextures(1, &screenTBO);
    // Bind screen texture buffer
    glBindTexture(GL_TEXTURE_2D_MULTISAMPLE, screenTBO);

    // Attach all buffers to frame buffer
    // Attach color buffer
    glTexImage2DMultisample(GL_TEXTURE_2D_MULTISAMPLE, samples, GL_RGBA8, windowWidth, windowHeight, GL_TRUE);
    // Screen color texture settings
    glTexParameteri(GL_TEXTURE_2D_MULTISAMPLE, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D_MULTISAMPLE, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D_MULTISAMPLE, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D_MULTISAMPLE, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    // Bind nothing
    glBindTexture(GL_TEXTURE_2D_MULTISAMPLE, 0);

    // Attach
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D_MULTISAMPLE, screenTBO, 0);

    // Attach depth and stencil buffer
    // Generate previous depth texture object
    glGenTextures(1, &screenDSBO);
    // Bind depth texture object
    glBindTexture(GL_TEXTURE_2D_MULTISAMPLE, screenDSBO);

    // Allocate memory for depth texture object
    glTexImage2DMultisample(GL_TEXTURE_2D_MULTISAMPLE, samples, GL_DEPTH24_STENCIL8, windowWidth, windowHeight, GL_TRUE);
    // Set depth texture parameters
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    // Bind nothing
    glBindTexture(GL_TEXTURE_2D_MULTISAMPLE, 0);

    // Attach
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_DEPTH_STENCIL_ATTACHMENT, GL_TEXTURE_2D_MULTISAMPLE, screenDSBO, 0);

    // Attach render buffer
    // Generate render buffer
    glGenRenderbuffers(1, &sceneRBO);
    // Bind the render buffer
    glBindRenderbuffer(GL_RENDERBUFFER, sceneRBO);
    // Render buffer storage
    glRenderbufferStorageMultisample(GL_RENDERBUFFER, samples, GL_DEPTH24_STENCIL8, windowWidth, windowHeight);
    // Bind classic renderbuffer
    glBindRenderbuffer(GL_RENDERBUFFER, 0);
    // Attach
    glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_DEPTH_STENCIL_ATTACHMENT, GL_RENDERBUFFER, sceneRBO);

    // Check buffer integrity
    if (glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE)
    {
        std::cout << "Framebuffer generation successful!" << std::endl;
    }
    else
    {
        std::cerr << "Framebuffer generation failed!" << std::endl;
    }

    // Generate vertex array object
    glGenVertexArrays(1, &screenVAO);
    // Bind vertex array object
    glBindVertexArray(screenVAO);

    // Generate vertex buffer object
    glGenBuffers(1, &screenVBO);
    // Bind vertex buffer object
    glBindBuffer(GL_ARRAY_BUFFER, screenVBO);
    // Fill the buffer with data
    float screenQuad[30] = {
        -1, -1, 0, 0,
        1, -1, 1, 0,
        -1, 1, 0, 1,
        -1, 1, 0, 1,
        1, -1, 1, 0,
        1, 1, 1, 1};
    // Fill vertex buffer with data (a quad for rendering the screen)
    glBufferData(GL_ARRAY_BUFFER, 24 * sizeof(float), screenQuad, GL_DYNAMIC_DRAW);
    // Link and enable vertex attributes for the screen
    // Vertex position
    glVertexAttribPointer(0, 2, GL_FLOAT, GL_FALSE, 4 * sizeof(float), (const void *)0);
    glEnableVertexAttribArray(0);
    // UV coordinates
    glVertexAttribPointer(1, 2, GL_FLOAT, GL_FALSE, 4 * sizeof(float), (void *)(2 * sizeof(float)));
    glEnableVertexAttribArray(1);
    // Send the screen textures to the shader
    postProcessingShader.use();
    postProcessingShader.setInt("screenTexture", 0);
    postProcessingShader.setInt("screenDepth", 1);

    // Now fill buffers with data once and for all
    load_blockTextures((char *)"Assets/Textures/");
    // Now load all block data
    load_blockData((char *)"Assets/Metadata/");

    // Send the texture to the shader
    renderShader.use();
    renderShader.setInt("textureAtlas", 0);
    // Bind the textures
    glBindTexture(GL_TEXTURE_2D, textureAtlas);
    // Texture rendering method
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    // Mipmap method setup
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST_MIPMAP_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    // Disable anisotropy effect
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAX_ANISOTROPY /*_EXT*/, 16);

    if (blockCollage_buffer)
    {
        // Fill the texture buffer with actuall data
        glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, blockCollage_xSize * blockPaddedTexture_xSize, blockCollage_ySize * blockPaddedTexture_ySize, 0, GL_RGBA, GL_UNSIGNED_BYTE, blockCollage_buffer);
        // Generate mipmap
        glGenerateMipmap(GL_TEXTURE_2D);
    }
    else
    {
        std::cerr << "Failed to load texture" << std::endl;
    }

    // Print if the construction of the engine has been successful
    std::cout << "New engine constructed!" << std::endl;
}

// This is the engine deconstructor (where everything gets deleted)
Engine::~Engine()
{

    // Erase chunk elements.
    // Chunk map
    for (auto &pair : worldChunkMap)
    {
        if (worldChunkState[pair.first].load() == 5 || worldChunkState[pair.first].load() == 1)
        {
            delChunkMap((const int)pair.first.first, (const int)pair.first.second);
        }
    }
    worldChunkMap.clear();

    // Chunk geometry
    for (auto &pair : worldChunkGeometry)
    {
        if (worldChunkState[pair.first].load() == 6 || worldChunkState[pair.first].load() == 2)
        {
            delChunkGeometry((const int)pair.first.first, (const int)pair.first.second);
        }
    }
    worldChunkGeometry.clear();

    // Chunk GL buffers
    for (auto &pair : worldChunkVBO)
    {
        if (worldChunkState[pair.first].load() == 8 || worldChunkState[pair.first].load() == 4)
        {
            delChunkGLBuffers((const int)pair.first.first, (const int)pair.first.second);
        }
    }
    worldChunkVBO.clear();
    worldChunkVAO.clear();
    worldChunkEBO.clear();

    // Clear world chunk state
    worldChunkState.clear();
}
