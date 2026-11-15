#pragma once

// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <string>

// All global variables
// Window related variables
extern int windowWidth;
extern int windowHeight;

// World size variables
constexpr int chunk_xSize = 16;
constexpr int chunk_zSize = 256;
constexpr int chunk_ySize = 16;
constexpr int renderDistance = 8;
constexpr int worldSize = 0;
constexpr int worldMid = worldSize / 2;

// PLayer related variables
// Player position
extern double playerX;
extern double playerZ;
extern double playerY;
// Player velocity
extern double playerXV;
extern double playerZV;
extern double playerYV;
// Player orientation
extern double playerXR;
extern double playerYR;
extern float sprintEffect;
// Player field of view
extern float playerFOV;

// Define mathematicall constants and formulas
#define PI 3.14159265358979323846
#define toRad *PI / 180
#define toDeg *180 / PI

// Eror checking function
void checkGLerror(std::string location);