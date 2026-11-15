// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <iostream>
// #include <cmath>
// #include <vector>
// #include <set>
// #include <unordered_map>
// #include <atomic>
// #include <filesystem>
// #include <string>

// Globals
#include "globals.hpp"
// Game engine
#include "engine.hpp"
// Load file function
#include "loadFile.hpp"
// Image library
#include "stb_image.h"

void Engine::load_blockTextures(char *texturePath)
{

    // Load textures
    int width, height, nrChannels;
    // Make image upside down
    stbi_set_flip_vertically_on_load(true);

    // Get all filenames
    std::vector<std::string> image_paths;
    for (const auto &entry : std::filesystem::directory_iterator(texturePath))
    {
        if (entry.path().extension() == ".png")
        {
            std::cout << entry.path() << std::endl;
            image_paths.push_back(entry.path().string());
        }
    }

    // The temporary variable to store each individual image.
    unsigned char *blockTexture_buffer;

    blockCollage_buffer = new unsigned char[blockCollage_xSize * blockCollage_ySize * blockPaddedTexture_xSize * blockPaddedTexture_ySize * 4];

    // Load all images using the filenames
    if (image_paths.size() > 0)
    {
        for (unsigned int i = 0; i < image_paths.size(); i++)
        {
            // Load the image
            blockTexture_buffer = stbi_load(image_paths[i].c_str(), &width, &height, &nrChannels, 0);
            blockCollage_imageNames[std::filesystem::path(image_paths[i]).filename().string()] = i;
            std::cout << blockCollage_imageNames[std::filesystem::path(image_paths[i]).filename().string()] << std::filesystem::path(image_paths[i]).filename().string() << std::endl;
            int xPadding = (blockPaddedTexture_xSize - blockTexture_xSize) / 2;
            int yPadding = (blockPaddedTexture_ySize - blockTexture_xSize) / 2;
            // Put the image into a collage
            for (unsigned int x = 0; x < blockPaddedTexture_xSize; x++)
            {
                int xt = x - xPadding;
                if (xt <= 0)
                {
                    xt = 0;
                }
                else if (xt >= (int)blockTexture_xSize - 1)
                {
                    xt = blockTexture_xSize - 1;
                }
                for (unsigned int y = 0; y < blockPaddedTexture_ySize; y++)
                {
                    int yt = y - yPadding;
                    if (yt <= 0)
                    {
                        yt = 0;
                    }
                    else if (yt >= (int)blockTexture_ySize - 1)
                    {
                        yt = blockTexture_ySize - 1;
                    }
                    blockCollage_buffer[((x + (i % blockCollage_xSize) * blockPaddedTexture_xSize) + y * blockCollage_xSize * blockPaddedTexture_xSize) * 4 + 3] = (unsigned char)0xFF;
                    for (unsigned int ch = 0; ch < (unsigned int)nrChannels; ch++)
                    {

                        blockCollage_buffer[(int)(((x + (i % blockCollage_xSize) * blockPaddedTexture_xSize) + (y + floor(i / blockCollage_xSize) * blockPaddedTexture_xSize) * blockCollage_xSize * blockPaddedTexture_xSize) * 4 + ch)] =
                            blockTexture_buffer[(xt + yt * blockTexture_ySize) * nrChannels + ch];
                    }
                }
            }
            // Delete the little image as we don't need it anymore
            stbi_image_free(blockTexture_buffer);
        }
    }
}