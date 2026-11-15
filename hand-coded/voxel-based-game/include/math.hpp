#pragma once

// Quake inverse suqare root
float Q_rsqrt(float number);

// Returns the signant value of ```x```
int sig(double x);

// Returns the mixed power of x and y
double mPow(double x, double y, double e);

// Perlin noise
float perlin(float seed, float x, float y);

// Random number generator
float rand(float *seed);