// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <iostream>
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

void Engine::load_blockData(char *metaPath)
{

    // Empty the sctrucure before using it
    for (unsigned int i = 0; i < blockCount; i++)
    {
        blockData[i].name = "";
        blockData[i].tehnical_name = "";
        blockData[i].breakable = false;
        blockData[i].durability = 0;
        blockData[i].tools = nullptr;
        blockData[i].model = "";
        blockData[i].func = nullptr;
        blockData[i].craft_xSize = 0;
        blockData[i].craft_ySize = 0;
        blockData[i].crafting[0] = "";
        blockData[i].crafting[1] = "";
        blockData[i].crafting[2] = "";
        blockData[i].crafting[3] = "";
        blockData[i].crafting[4] = "";
        blockData[i].crafting[5] = "";
        blockData[i].crafting[6] = "";
        blockData[i].crafting[7] = "";
        blockData[i].crafting[8] = "";
        blockData[i].smelting = "";
        blockData[i].drops = nullptr;
        blockData[i].drops_probabilities[0] = 0;
        blockData[i].drops_probabilities[1] = 0;
        blockData[i].drops_per_tool[0] = 0;
        blockData[i].drops_per_tool[1] = 0;
        blockData[i].syntax = nullptr;
        blockData[i].full_definition = "";
        blockData[i].textures = nullptr;
        blockData[i].size[0] = 0;
        blockData[i].size[1] = 0;
        blockData[i].size[2] = 0;
        blockData[i].size[3] = 0;
    }

    std::unordered_map<std::string, int> block_order;
    block_order["air.dat"] = 0;
    block_order["grass_block.dat"] = 1;
    block_order["dirt.dat"] = 2;
    block_order["stone.dat"] = 3;
    block_order["bedrock.dat"] = 4;
    block_order["cobblestone.dat"] = 5;
    block_order["acacia_log.dat"] = 6;
    block_order["acacia_planks.dat"] = 7;
    block_order["tnt.dat"] = 8;
    block_order["oak_leaves.dat"] = 9;
    block_order["water.dat"] = 10;
    block_order["sand.dat"] = 11;

    std::cout << "Loading block data" << std::endl;
    // Get all filenames
    std::vector<std::string> meta_paths;
    for (const auto &entry : std::filesystem::directory_iterator(metaPath))
    {
        if (entry.path().extension() == ".dat")
        {
            // std::cout << entry.path() << std::endl;
            meta_paths.push_back(entry.path().string());
        }
    }

    // How many types of blocks exist
    blockCount = meta_paths.size();

    blockData = new BlockData[blockCount];

    // Use filenames to load data.
    if (meta_paths.size() > 0)
    {
        for (unsigned long i = 0; i < meta_paths.size() && i < blockCount; i++)
        {
            // std::cout << meta_paths[i] << std::endl ;

            unsigned long size;
            if (!loadFile(meta_paths[i], &blockData[block_order[std::filesystem::path(meta_paths[i]).filename().string()]].full_definition, &size))
            {
                // Catch error
                std::cerr << "One metadata file could not load. Loading aborted." << std::endl;
                return;
            }
        }
    }
    else
    {
        // The folder is empty
        std::cout << "Metadata folder is empty." << std::endl;
        return;
    }

    // Process the metadata
    for (unsigned long i = 0; i < blockCount; i++)
    {
        // This is the process buffer in which the processing of the string will be done
        std::string processBuffer = "";
        // This flag is used to determine the behavior/step of the program
        char readFlag = 0x00;
        // This variable is supposed to store the variable's type
        std::string type = "";
        // This variable is supposed to store the variable's name
        std::string name = "";
        // This variable is supposed to store the variable's value(s)
        std::string value = "";
        // This variable is supposed to store the quote depth
        // bool quoteDepth = false;
        // This variable is supposed to store the bracket depth
        // int bracketDepth = 0;
        // This variable stores the amount of elements inside an array.
        int elementCount = 1;
        // Initializing counter for appending
        unsigned int counter = 0;

        // Processing
        for (unsigned int c = 0; c < blockData[i].full_definition.size(); c++)
        {
            // Append character to the process buffer
            processBuffer += blockData[i].full_definition.substr(c, 1);
            // Increment counter
            counter++;

            // Select if either it's reading the var type/name/value.
            switch (readFlag)
            {
            case 0x00:
                // Type reading
                if (counter == 3)
                {
                    // Get the var type
                    type = processBuffer.substr(0, 3);
                    // Change the flag to "name", clear process buffer and reset counter
                    readFlag = 0x01;
                    processBuffer.clear();
                    counter = 0;
                }
                // Exit case
                break;
            case 0x01:
                // Name reading
                if (processBuffer.substr(counter - 1, 1) == "=")
                {
                    // Get the var name
                    name = processBuffer.substr(1, counter - 2);
                    // Change flag to "value", clear process buffer and reset counter
                    readFlag = 0x02;
                    processBuffer.clear();
                    counter = 0;
                }
                // Exit case
                break;
            case 0x02:
                // Value reading
                if (type == "arr" || type == "drp")
                {
                    if (processBuffer.substr(counter - 1, 1) == ";")
                    {
                        elementCount++;
                    }
                }

                if (processBuffer.substr(counter - 1, 1) == "\n")
                {
                    // Get the var valeue
                    value = processBuffer.substr(0, counter - 1);
                    // Change the flag to "type", clear the process buffer and reset counter
                    readFlag = 0x00;
                    processBuffer.clear();
                    counter = 0;

                    // Handle array type value
                    if (type == "arr")
                    {
                        // The value with real correct type
                        std::string *prValue = new std::string[elementCount];
                        // Where the value begins
                        int initPoint = 1;
                        // Index of array var
                        int elementIndex = 0;
                        for (unsigned int arrayProcess = 1; arrayProcess < value.size() - 1; arrayProcess++)
                        {
                            if (value.substr(arrayProcess, 1) == ";")
                            {
                                prValue[elementIndex] = value.substr(initPoint + 1, arrayProcess - initPoint - 2);
                                initPoint = arrayProcess + 1;
                                elementIndex++;
                            }
                        }
                        if (name == "tool")
                        {
                            // The value with real correct type
                            blockData[i].tools = new std::string[elementCount];
                            for (int j = 0; j < elementCount; j++)
                            {
                                blockData[i].tools[j] = prValue[j];
                                blockData[i].size[0] = elementCount;
                            }
                        }
                        if (name == "textures")
                        {
                            // The value with real correct type
                            blockData[i].textures = new std::string[elementCount];
                            for (int j = 0; j < elementCount; j++)
                            {
                                std::cout << "Is array working?: " << prValue[j] << std::endl;
                                blockData[i].textures[j] = prValue[j];
                                blockData[i].size[1] = elementCount;
                            }
                        }
                        delete[] prValue;
                    }
                    // Handle text/model type value
                    else if (type == "txt" || type == "mod")
                    {
                        // The value with real correct type
                        std::string prValue = value.substr(1, value.size() - 2);
                        if (name == "name")
                        {
                            blockData[i].name = prValue;
                        }
                        else if (name == "tehnical_name")
                        {
                            blockData[i].tehnical_name = prValue;
                        }
                        else if (name == "model")
                        {
                            blockData[i].model = prValue;
                        }
                        else if (name == "smelting")
                        {
                            blockData[i].smelting = prValue;
                        }
                    }
                    // Handle boolean type value
                    else if (type == "bln")
                    {
                        // The value with real correct type
                        bool prValue = value == "true";
                        if (name == "breakable")
                        {
                            blockData[i].breakable = prValue;
                        }
                    }
                    // Handle float type value
                    else if (type == "flt")
                    {
                        // The value with real correct type
                        std::cout << value << std::endl;
                        float prValue = std::stof(value);
                        if (name == "durability")
                        {
                            blockData[i].durability = prValue;
                        }
                    }
                    // Handle function type value
                    else if (type == "fun")
                    {
                        // The value with real correct type
                        void (*prValue)() = nullptr;
                        if (value.substr(1, value.size() - 2) == "none")
                        {
                            prValue = BlockFunc::none;
                        }
                        else if (value.substr(1, value.size() - 2) == "door")
                        {
                            prValue = BlockFunc::door;
                        }
                        else if (value.substr(1, value.size() - 2) == "trapdoor")
                        {
                            prValue = BlockFunc::trapdoor;
                        }
                        else if (value.substr(1, value.size() - 2) == "gate")
                        {
                            prValue = BlockFunc::gate;
                        }
                        else if (value.substr(1, value.size() - 2) == "bed")
                        {
                            prValue = BlockFunc::bed;
                        }
                        else if (value.substr(1, value.size() - 2) == "workbench")
                        {
                            prValue = BlockFunc::workbench;
                        }
                        else if (value.substr(1, value.size() - 2) == "furnace")
                        {
                            prValue = BlockFunc::furnace;
                        }
                        else if (value.substr(1, value.size() - 2) == "tnt")
                        {
                            prValue = BlockFunc::tnt;
                        }
                        else if (value.substr(1, value.size() - 2) == "grass_block")
                        {
                            prValue = BlockFunc::grass_block;
                        }
                        else
                        {
                            prValue = BlockFunc::none;
                        }
                        if (name == "function")
                        {
                            blockData[i].func = prValue;
                        }
                    }
                }
                // Exit case
                break;
            }
        }
    }
}