// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <iostream>
// #include <cmath>
// #include <string>
// #include <set>
// #include <unordered_map>
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

// This function renders the screen once (so use it in a loop)
const void Engine::render(double x, double y, double z, double xr, double yr, double zr, float FOV)
{

    // Render
    // Create transformations
    // Rotation matrices
    // X rotation
    glm::mat4 xRot = glm::mat4(1.0f);
    xRot = glm::rotate(xRot, (float)(-xr toRad), glm::vec3(0.0f, 1.0f, 0.0f));
    // Y rotation
    glm::mat4 yRot = glm::mat4(1.0f);
    yRot = glm::rotate(yRot, (float)(yr toRad), glm::vec3(1.0f, 0.0f, 0.0f));
    // Z rotation
    glm::mat4 zRot = glm::mat4(1.0f);
    zRot = glm::rotate(zRot, (float)(zr toRad), glm::vec3(0.0f, 0.0f, 1.0f));

    // Camera position
    glm::mat4 translate = glm::mat4(1.0f);
    translate = glm::translate(translate, glm::vec3(-x, -y, -z));
    glm::vec3 camPos = glm::vec3(x, y, z);

    // Projection
    glm::mat4 projection = glm::mat4(1.0f);
    projection = glm::perspective((float)(FOV toRad), (float)windowWidth / (float)windowHeight, 0.1f, 512.0f);

    // Ambient light
    glm::vec4 ambientLight = glm::vec4(0.33f, 0.6f, 1.0f, 1.0f);
    // glm::vec4 ambientLight = glm::vec4(0.0f, 0.0f, 0.0f, 1.0f);

    // Uniforms
    // Use the shader (make sure it's the shader from earlier)
    renderShader.use();
    // Uniform (send) all necessary data to the GPU
    renderShader.setMat4("xRot", xRot);
    renderShader.setMat4("yRot", yRot);
    renderShader.setMat4("zRot", zRot);
    renderShader.setMat4("translate", translate);
    renderShader.setMat4("projection", projection);
    renderShader.setVec4("ambientLight", ambientLight);
    renderShader.setVec3("camPos", camPos);
    renderShader.setInt("previousDepthTexture", 1);

    // Tests
    // Enable tests
    glEnable(GL_DEPTH_TEST);
    glDisable(GL_STENCIL_TEST);
    glDisable(GL_BLEND);
    glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
    glEnable(GL_CULL_FACE);

    // Binding
    // Bind the textures
    // Set active texture
    glActiveTexture(GL_TEXTURE0);
    checkGLerror("set active texture0");

    // Bind the texture buffer
    glBindTexture(GL_TEXTURE_2D, textureAtlas);
    checkGLerror("bind texture0");
    // Set active texture
    glActiveTexture(GL_TEXTURE1);
    checkGLerror("set active textureAtlas");
    // Bind the texture buffer
    glBindTexture(GL_TEXTURE_2D_MULTISAMPLE, screenDSBO);
    checkGLerror("bind textureAtlas");

    // Bind screen related buffers
    // Bind the render buffer
    glBindRenderbuffer(GL_RENDERBUFFER, sceneRBO);
    checkGLerror("bind framebuffer sceneRBO");
    // Bind the frame buffer
    glBindFramebuffer(GL_FRAMEBUFFER, screenFBO);
    checkGLerror("bind framebuffer screenFBO");
    // Attach buffers to frame buffer
    // glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D_MULTISAMPLE, screenTBO, 0);
    // glFramebufferTexture2D(GL_FRAMEBUFFER, GL_DEPTH_STENCIL_ATTACHMENT, GL_TEXTURE_2D_MULTISAMPLE, screenDSBO, 0);

    // Drawing
    // Clear the screen
    glClearColor(0.33f, 0.6f, 1.0f, 1.0f);
    // glClearColor(0.0f, 0.0f, 0.0f, 0.0f);
    glClearDepth(1.0f); // Clear depth to 0.0 for greater test to work
    glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT | GL_STENCIL_BUFFER_BIT);
    checkGLerror("clearing buffers");
    // Set polygon mode to fill
    glPolygonMode(GL_FRONT_AND_BACK, GL_FILL);
    checkGLerror("set polygon mode");
    // Rendering each chunk individually
    for (auto &pair : worldChunkVBO)
    {
        int xx = pair.first.first;
        int yy = pair.first.second;

        if (worldChunkState[{xx, yy}] == 8)
        {
            // Bind the geometry
            // Bind the vertex array
            glBindVertexArray(worldChunkVAO[{xx, yy}]);
            checkGLerror("bind verrtex array");
            // Bind the vertex buffer
            glBindBuffer(GL_ARRAY_BUFFER, worldChunkVBO[{xx, yy}]);
            checkGLerror("bind verrtex buffer");
            // Bind the element buffer
            glBindBuffer(GL_ELEMENT_ARRAY_BUFFER, worldChunkEBO[{xx, yy}]);
            checkGLerror("bind element buffer");

            // Render the scene
            glDrawElements(GL_TRIANGLES, worldChunkGeometry[{xx, yy}].fac_count * 3u, GL_UNSIGNED_INT, (void *)0);
            checkGLerror("draw elements");
        }
        else if (worldChunkState[{xx, yy}] == 0)
        {
            worldChunkState.erase(worldChunkState.find({xx, yy}));
        }
    }

    // Post process image
    // Uniforms
    // Switch back to default
    postProcessingShader.use();
    // Uniform (send) all necessary data to the GPU
    glm::vec2 windowSize = glm::vec2(windowWidth, windowHeight);
    postProcessingShader.setVec2("windowSize", windowSize);

    // Tests
    // Disable tests (except for blending)
    glDisable(GL_DEPTH_TEST);
    glDisable(GL_STENCIL_TEST);
    glDisable(GL_BLEND);
    glBlendFunc(GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA);
    glDisable(GL_CULL_FACE);

    // Binding
    // Bind geometry
    // Bind the screen vertex array
    glBindVertexArray(screenVAO);

    // Bind textures
    // Set active texture
    glActiveTexture(GL_TEXTURE0);
    // Bind the screen texture
    glBindTexture(GL_TEXTURE_2D_MULTISAMPLE, screenTBO);
    // Set active texture
    glActiveTexture(GL_TEXTURE1);
    // Bind the screen texture
    glBindTexture(GL_TEXTURE_2D_MULTISAMPLE, screenDSBO);

    // Bind screen related buffers
    // Bind the render buffer
    glBindRenderbuffer(GL_RENDERBUFFER, sceneRBO);
    // Bind the frame buffer
    glBindFramebuffer(GL_FRAMEBUFFER, 0);

    // Drawing
    // Set polygon mode to fill
    glPolygonMode(GL_FRONT_AND_BACK, GL_FILL);
    // Draw final result
    glDrawArrays(GL_TRIANGLES, 0, 6);
}