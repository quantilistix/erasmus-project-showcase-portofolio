#pragma once

// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <iostream>
// #include <string>
// #include <thread>
// #include <vector>
// #include <set>
// #include <unordered_map>
// #include <atomic>

// // Include OpenGL libraries
// // #include <GLEW/glew.h>
// #include <GLFW/glfw3.h>

// // Include OpenGL math library (GLM)
// #include "glm/glm.hpp"
// #include "glm/gtc/matrix_transform.hpp"
// #include "glm/gtc/type_ptr.hpp"

// Globals
#include "globals.hpp"
// Load file function
#include "loadFile.hpp"
// Shader class
#include "shaderLoader.hpp"

// Block data structure to store different types of blocks
struct BlockData
{
    // The block data
    std::string name;
    std::string tehnical_name;
    bool breakable;
    float durability;
    std::string *tools;
    std::string model;
    void (*func)();
    int craft_xSize;
    int craft_ySize;
    std::string crafting[9];
    std::string smelting;
    std::string *drops;
    unsigned int drops_probabilities[2];
    unsigned int drops_per_tool[2];
    std::string *syntax;
    std::string full_definition;
    std::string *textures;
    size_t size[4];
};

// Block functions
struct BlockFunc
{
    static void none();
    static void grass_block();
    static void door();
    static void trapdoor();
    static void tnt();
    static void gate();
    static void bed();
    static void workbench();
    static void furnace();
};

// Voxel map with all links to thei geometry parts
struct Chunk
{
    // The dimension it's in
    char dim;
    // The block ID
    int ID[chunk_xSize][chunk_zSize][chunk_ySize];
    // The actuall properties of the block
    std::string properties[chunk_xSize][chunk_zSize][chunk_ySize];
    // Links to block geometry
    int vtxTag[chunk_xSize][chunk_zSize][chunk_ySize];
    int facTag[chunk_xSize][chunk_zSize][chunk_ySize];
};

// World geometry structure to store all vertices and faces
struct Mesh
{
    float *vertices;
    unsigned int *faces;
    unsigned int vtx_count;
    unsigned int fac_count;
};

struct PairHash
{
    template <class T1, class T2>
    unsigned long operator()(const std::pair<T1, T2> &pair) const
    {
        auto h1 = std::hash<T1>{}(pair.first);
        auto h2 = std::hash<T2>{}(pair.second);
        return h1 ^ (h2 << 1); // or use h1 * 31 + h2
    }
};

class Engine
{
public:
    // The engine constructor
    Engine(GLFWwindow *aWindow, char *renderVertexShader, char *renderFragmentShader, char *postVertexShader, char *postFragmentShader, int samples);

    // Player input
    // This function checks for user inputs
    void processInput(GLFWwindow *window, double dt);

    // Block data
    // This function loads all the block data
    void load_blockData(char *metaPath);
    // This function loads all the block textures
    void load_blockTextures(char *texturePath);

    // World generation
    // Generates the terrain
    void genChunkMap(int &&xxxx, int &&yyyy);
    // Calculates chunk geometry size
    void calChunkGeometrySize(int &&xxxx, int &&yyyy);
    // Generates geometry
    void genChunkGeometry(int &&xxxx, int &&yyyy);
    // Generates the OpenGL buffers
    void genChunkGLBuffers(int &&xxxx, int &&yyyy);

    // World deletion
    // Deletes the terrain
    void delChunkMap(int &&xxxx, int &&yyyy);
    // Deletes geometry
    void delChunkGeometry(int &&xxxx, int &&yyyy);
    // Deletes buffers
    void delChunkGLBuffers(int &&xxxx, int &&yyyy);

    // Chunk management
    // This function manages the world chunks.
    void manageChunks();

    // World renderer
    // This function renders the screen once (so use it in a loop)
    const void render(double x, double y, double z, double xr, double yr, double zr, float FOV); //{

    // This is the engine deconstructor (where everything gets deleted)
    ~Engine();

    // Private (mostly variables and buffers)
private:
    // How many block types exist
    unsigned long blockCount;
    // Block metadata data base
    BlockData *blockData;
    // The world voxel map
    std::unordered_map<std::pair<int, int>, Chunk, PairHash> worldChunkMap;
    // The geometry of the world
    std::unordered_map<std::pair<int, int>, Mesh, PairHash> worldChunkGeometry;
    // The chunk state
    std::unordered_map<std::pair<int, int>, std::atomic<signed char>, PairHash> worldChunkState;

    // Create new shader
    Shader renderShader;
    Shader postProcessingShader;

    // Scene vertex buffer object
    std::unordered_map<std::pair<int, int>, unsigned int, PairHash> worldChunkVBO;
    // Scene vertex array object
    std::unordered_map<std::pair<int, int>, unsigned int, PairHash> worldChunkVAO;
    // Scene element buffer object
    std::unordered_map<std::pair<int, int>, unsigned int, PairHash> worldChunkEBO;
    // Scene render buffer object
    unsigned int sceneRBO;
    // Scene depth stencil buffer object
    unsigned int screenDSBO;
    // Texture1 buffer
    unsigned int textureAtlas;
    // Screen texture buffer object
    unsigned int screenTBO;
    // Screen frame buffer object
    unsigned int screenFBO;
    // Screen vertex buffer object
    unsigned int screenVBO;
    // Screen vertex array object
    unsigned int screenVAO;

    // Block texture X size
    unsigned int blockTexture_xSize = 16;
    // Block texture Y size
    unsigned int blockTexture_ySize = 16;
    // Block padded texture X size
    unsigned int blockPaddedTexture_xSize = 48;
    // Block padded texture Y size
    unsigned int blockPaddedTexture_ySize = 48;
    // Collage X size
    unsigned int blockCollage_xSize = 24;
    // Collage Y size
    unsigned int blockCollage_ySize = 24;
    // Collage image buffer
    unsigned char *blockCollage_buffer = nullptr;
    // Collage image names
    std::unordered_map<std::string, int> blockCollage_imageNames;

    // Init the engine's window
    GLFWwindow *window;
};