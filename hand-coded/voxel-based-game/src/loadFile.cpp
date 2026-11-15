// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <iostream>
// #include <string>
// #include <fstream>

bool loadFile(const std::string &filename, std::string *buffer, unsigned long *size)
{
    // Open the file in binary mode
    std::ifstream file(filename, std::ios::binary | std::ios::ate);

    // Check if the file was opened successfully
    if (!file.is_open())
    {
        std::cerr << "Error opening file: " << filename << std::endl;
        return false;
    }

    // Get the size of the file
    *size = static_cast<int>(file.tellg());

    // Allocate memory for the file contents and catch any error
    char *workBuffer;
    try
    {
        workBuffer = new char[*size];
    }
    catch (std::bad_alloc &)
    {
        std::cerr << "Error allocating memory:" << filename << " File may be too big or other unexpected issue." << std::endl;
        delete[] buffer;
        buffer = nullptr;
        *size = 0;
        return false;
    }

    // Move the file pointer back to the beginning of the file
    file.seekg(0, std::ios::beg);

    // Read the file contents into the buffer
    file.read(workBuffer, *size);

    // Close the file
    file.close();

    // Return the content
    *buffer = buffer->assign(workBuffer);

    return true;
}