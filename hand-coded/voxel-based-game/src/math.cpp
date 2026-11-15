#include <pch.hpp>
#include <math.hpp>

// Quake's legendary inverse suqare root algorithm
float Q_rsqrt(float number)
{
    long i;
    float x2, y;
    const float threehalfs = 1.5F;

    x2 = number * 0.5F;
    y = number;
    i = *(long *)&y;           // evil floating point bit level hacking
    i = 0x5f3759df - (i >> 1); // what the fuck?
    y = *(float *)&i;
    y = y * (threehalfs - (x2 * y * y)); // 1st iteration
    y = y * (threehalfs - (x2 * y * y)); // 2nd iteration, this can be removed
    // y = y * (threehalfs - (x2 * y * y)); // 3rd iteration, for fun

    return y;
}

// Returns the signant value of ```x```
int sig(double x)
{
    // Brute force all combinations
    if (x > 0)
    {
        return 1;
    }
    if (x == 0)
    {
        return 0;
    }
    if (x < 0)
    {
        return -1;
    }
    return 0;
}

// Returns the mixed power of x and y
double mPow(double x, double y, double e)
{
    // Compute the generalized exponentiation
    return x * pow(y, e - 1);
}

float perlin(float seed, float x, float y)
{
    // Fade function for smoothing
    auto fade = [](float t)
    {
        return (float)(t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f)); // Smoothstep
    };

    // Linear interpolation
    auto lerp = [](float a, float b, float t)
    {
        return a + t * (b - a);
    };

    // Hash to generate pseudo-random gradients
    auto gradient = [](unsigned int hash)
    {
        // Create unit vectors at 45-degree intervals
        const std::pair<float, float> gradients[] = {
            {1.0f, 1.0f}, {-1.0f, 1.0f}, {1.0f, -1.0f}, {-1.0f, -1.0f}, {1.0f, 0.0f}, {-1.0f, 0.0f}, {0.0f, 1.0f}, {0.0f, -1.0f}};
        // Normalize the hash to the size of our gradients array
        hash = hash & 7;
        return gradients[hash];
    };

    // Dot product of gradient and distance vectors
    auto dot = [](std::pair<float, float> grad, float dx, float dy)
    {
        return (float)(grad.first * dx + grad.second * dy);
    };

    // Grid cell coordinates
    int x0 = (int)(floor(x));
    int y0 = (int)(floor(y));
    int x1 = x0 + 1;
    int y1 = y0 + 1;

    // Local coordinates within the grid cell
    float dx = x - (float)x0;
    float dy = y - (float)y0;

    // Fade curves for dx, dy
    float u = fade(dx);
    float v = fade(dy);

    // Hash function using bit manipulation
    auto hash = [&](int x, int y)
    {
        unsigned int h = (unsigned int)(x * 1619 + y * 31337 + seed * 1013);
        h = ((h << 13) ^ h) * 6553;
        h = ((h << 17) ^ h) * 2377;
        return (unsigned int)(h ^ (h >> 16));
    };

    // Hash values to determine gradients
    unsigned int hash00 = hash(x0, y0);
    unsigned int hash01 = hash(x0, y1);
    unsigned int hash10 = hash(x1, y0);
    unsigned int hash11 = hash(x1, y1);

    // Gradients
    std::pair<float, float> grad00 = gradient(hash00);
    std::pair<float, float> grad01 = gradient(hash01);
    std::pair<float, float> grad10 = gradient(hash10);
    std::pair<float, float> grad11 = gradient(hash11);

    // Dot products
    float dot00 = dot(grad00, dx, dy);
    float dot10 = dot(grad10, dx - 1, dy);
    float dot01 = dot(grad01, dx, dy - 1);
    float dot11 = dot(grad11, dx - 1, dy - 1);

    // Interpolation
    float lerpX1 = lerp(dot00, dot10, u);
    float lerpX2 = lerp(dot01, dot11, u);
    float value = lerp(lerpX1, lerpX2, v);

    // The value should naturally be in roughly [-1, 1] range now
    return value;
}

// Random number generator
float rand(float *seed)
{
    const long two = pow(2.0f, 31.0f);
    *seed = ((long)floor(1103515245.0f * *seed) + 12345) % two;
    return *seed / (const float)two;
}