// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <iostream>
// #include <cmath>

// // Include OpenGL libraries
// #include <GLFW/glfw3.h>

// Globals
#include "globals.hpp"
// Game engine
#include "engine.hpp"

// Window related variables
int windowWidth = 1920;
int windowHeight = 1080;

// PLayer related variables
// Player position
// double playerX = 2'147'483'647;
// double playerX = 57'483'647;
double playerX = 0;
double playerZ = 150;
double playerY = 0;
// Player velocity
double playerXV = 0;
double playerZV = 0;
double playerYV = 0;
// Player orientation
double playerXR = 135;
double playerYR = -45;
float sprintEffect = 0;
// Player field of view
float playerFOV = 110;

// Eror checking function
void checkGLerror(std::string location)
{
    GLenum err = glGetError();
    if (err != GL_NO_ERROR)
    {
        std::cerr << "GL error: " << err << "; at location: " << location << std::endl;
    }
}