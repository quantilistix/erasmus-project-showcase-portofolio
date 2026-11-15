// Dependencies
#include <pch.hpp>
// // Include standard libraries
// #include <cmath>

// // Include OpenGL libraries
// #include <GLFW/glfw3.h>

// Globals
#include <globals.hpp>
// Game engine
#include <engine.hpp>
// Math
#include <math.hpp>

// Input processing function
void Engine::processInput(GLFWwindow *window, double dt)
{
    // Escape escapes the program
    if (glfwGetKey(window, GLFW_KEY_ESCAPE) == GLFW_PRESS)
    {
        glfwSetWindowShouldClose(window, true);
    }

    // Player movement
    // Sprint variable
    double sprint = 32.0f;

    // Pre-calculate sin and cos values of movement
    const double cs = cos(playerXR toRad) * dt;
    const double sn = sin(playerXR toRad) * dt;
    // Move forwards
    if (glfwGetKey(window, GLFW_KEY_W) == GLFW_PRESS)
    {
        // If sprint
        if (glfwGetKey(window, GLFW_KEY_LEFT_CONTROL) == GLFW_PRESS)
        {
            sprint = 64.0f;
        }
        playerYV -= (const double)sprint * cs;
        playerXV += (const double)sprint * sn;
    }
    // Move backwards
    if (glfwGetKey(window, GLFW_KEY_S) == GLFW_PRESS)
    {
        playerYV += (const double)sprint * cs;
        playerXV -= (const double)sprint * sn;
    }
    // Move leftwards
    if (glfwGetKey(window, GLFW_KEY_A) == GLFW_PRESS)
    {
        playerXV -= (const double)sprint * cs;
        playerYV -= (const double)sprint * sn;
    }
    // Move rightwards
    if (glfwGetKey(window, GLFW_KEY_D) == GLFW_PRESS)
    {
        playerXV += (const double)sprint * cs;
        playerYV += (const double)sprint * sn;
    }
    // Move upwards
    if (glfwGetKey(window, GLFW_KEY_SPACE) == GLFW_PRESS)
    {
        playerZV += (const double)256 * dt;
    }
    // Move downwards
    if (glfwGetKey(window, GLFW_KEY_LEFT_SHIFT) == GLFW_PRESS)
    {
        playerZV -= (const double)256 * dt;
    }

    // Player FOV control
    // Zoom out
    if (glfwGetKey(window, GLFW_KEY_DOWN) == GLFW_PRESS)
    {
        playerFOV += (const double)8 * dt;
        if (playerFOV > (const double)170)
        {
            playerFOV = (const double)170;
        }
    }
    // Zoom in
    if (glfwGetKey(window, GLFW_KEY_UP) == GLFW_PRESS)
    {
        playerFOV -= (const double)8 * dt;
        if (playerFOV < (const double)30)
        {
            playerFOV = (const double)30;
        }
    }

    // Mechanical orientation
    // Look left
    if (glfwGetKey(window, GLFW_KEY_LEFT) == GLFW_PRESS)
    {
        playerXR -= (const double)128 * dt;
    }
    // Look right
    if (glfwGetKey(window, GLFW_KEY_RIGHT) == GLFW_PRESS)
    {
        playerXR += (const double)128 * dt;
    }
    // Look left
    if (glfwGetKey(window, GLFW_KEY_O) == GLFW_PRESS)
    {
        playerYR += (const double)128 * dt;
    }
    // Look right
    if (glfwGetKey(window, GLFW_KEY_K) == GLFW_PRESS)
    {
        playerYR -= (const double)128 * dt;
    }

    // Initialize 2 doubles for mouse movement
    double xm;
    double ym;
    // Get cursor position and reset mouse position
    glfwGetCursorPos(window, &xm, &ym);
    glfwSetCursorPos(window, windowWidth / 2, windowHeight / 2);

    // Calculate sensivity due to FOV
    const double ptan = tan(playerFOV toRad / 2);
    // Calculate deltaCursorPos to move mouse
    playerXR += (const double)(xm - windowWidth / 2) * ptan / (const double)6;
    playerYR -= (const double)(ym - windowHeight / 2) * ptan / (const double)6;

    // Check for any value being too high (looking too high or down; FOV too big or too small)
    if (playerYR < (const double)-90)
    {
        playerYR = (const double)-90;
    }
    if (playerYR > (const double)90)
    {
        playerYR = (const double)90;
    }

    // Add air resistance to player
    // Calculate player speed (2D to avoid vertical velocity problems when flying)
    int playerXVsignantValue = sig(playerXV);
    int playerYVsignantValue = sig(playerZV);
    int playerZVsignantValue = sig(playerYV);
    double speed = sqrt(playerXV * playerXV + playerYV * playerYV) + (const double)24;
    // Calculate resistance and apply it
    playerXV -= ((speed * abs(playerXV)) * (double)sig(playerXV)) * dt / (const double)16;
    playerZV -= ((abs(playerZV) * abs(playerZV) + (const double)1) * (double)sig(playerZV)) * dt * (const double)3;
    playerYV -= ((speed * abs(playerYV)) * (double)sig(playerYV)) * dt / (const double)16;
    if (playerXVsignantValue != sig(playerXV))
    {
        playerXV = 0;
    }
    if (playerYVsignantValue != sig(playerZV))
    {
        playerZV = 0;
    }
    if (playerZVsignantValue != sig(playerYV))
    {
        playerYV = 0;
    }

    // Update player position due to velocity
    playerX += playerXV * dt;
    playerZ += playerZV * dt;
    playerY += playerYV * dt;
}