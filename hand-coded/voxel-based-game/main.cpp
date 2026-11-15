// Define the game
#define NUMJRI
// Define wheter debugging or not
#ifdef NUMJRI

// Dependecies
#include <pch.hpp>
// #include <iostream>

// // Include OpenGL libraries
// // #include <GLEW/glew.h>
// #include "glad/glad.h"
// #include <KHR/khrplatform.h>
// #include <GLFW/glfw3.h>

// Globals
#include <globals.hpp>
// Image library
#define STB_IMAGE_IMPLEMENTATION
#include <stb_image.h>
// Load file function
#include <loadFile.hpp>
// Shader class
#include <shaderLoader.hpp>
// Game engine
#include <engine.hpp>
// GLinit
#include <glinit.hpp>

// Main function
int main(int argc, char *argv[])
{
    std::cout << "Running main function" << std::endl;

    GLFWwindow *window;
    GLFWmonitor *monitor;
    GLFWvidmode *mode;
    int max_width;
    int max_height;

    GLinit(window, monitor, mode, &windowWidth, &windowHeight, &max_width, &max_height);

    // Creating the world rendering engine with apropriate shaders
    std::cout << "Creating the engine" << std::endl;
    Engine Numjri(window,
                  (char *)"shaders/renderVertexShader.glsl", (char *)"shaders/renderFragmentShader.glsl",
                  (char *)"shaders/postVertexShader.glsl", (char *)"shaders/postFragmentShader.glsl", 8);

    // Generate terrain
    // Map generation
    std::cout << "Generating map." << std::endl;
    // Setting all values to 0 for the beginning
    std::cout << "Emptying world..." << std::endl;

    // Preparing for rendering
    // Hide the cursor
    glfwSetInputMode(window, GLFW_CURSOR, GLFW_CURSOR_DISABLED); /* not recommended for debug */

    // Move cursor to center for first frame
    glfwSetCursorPos(window, windowWidth / 2, windowHeight / 2);

    // Initialize delta time calculation variables
    double deltaTime = (double)glfwGetTime();
    double time = glfwGetTime();

    // Loop until the user closes the window
    while (!glfwWindowShouldClose(window))
    {
        // Delta time calculation
        deltaTime = glfwGetTime() - time;
        time = glfwGetTime();

        // Process window input
        Numjri.processInput(window, deltaTime);

        // Manage all chunks
        try
        {
            Numjri.manageChunks();
        }
        catch (const std::exception &e)
        {
            std::cerr << "Exception while managing chunks" << e.what() << std::endl;
        }

        // Use the engine to render the scene
        Numjri.render(playerX, playerZ, playerY, playerXR, playerYR, 0.0f, playerFOV + sprintEffect);

        // End of render
        // Swap front and back buffers
        glfwSwapBuffers(window);
        // Poll for and process events
        glfwPollEvents();
    }

    // When window is closing...
    // Delete everything
    std::cout << "Closing window..." << std::endl;

    // Delete engine
    // Numjri.~Engine();

    // Terminate GLFW and return 0
    glfwTerminate();

    return 0;
}
#endif