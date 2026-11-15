// Dependencies
#include <pch.hpp>

// // Include standard libraries
// #include <iostream>
// #include <string>
// #include <filesystem>
// #include <fstream>

// // Include OpenGL libraries
// // #include <GLEW/glew.h>
// #include "glad/glad.h"
// #include <KHR/khrplatform.h>

// Load file function
#include "loadFile.hpp"
// Shader class
#include "shaderLoader.hpp"

Shader::Shader(char *vertexShaderPath, char *fragmentShaderPath, std::string name)
{
    // Load the vertex shader source code
    // Open the file
    std::cout << name << std::endl;
    std::ifstream vtxSdr(vertexShaderPath);

    // Check if open
    if (!vtxSdr.is_open())
    {
        std::cout << "Could not load vertex shader " << *vertexShaderPath << std::endl;
        return;
    }

    // Read all data and store it into vertexShaderSource
    std::ostringstream vs;
    vs << vtxSdr.rdbuf();
    // Close the file
    vtxSdr.close();
    // Store data
    std::string vsc = vs.str();
    const char *vertexShaderSource = vsc.c_str();

    // Load the fragment shader source code
    // Open the file
    std::ifstream frgSdr(fragmentShaderPath);

    // Check if open
    if (!frgSdr.is_open())
    {
        std::cout << "Could not load fragment shader " << *fragmentShaderPath << std::endl;
        return;
    }

    // Read all data and store it into fragmentShaderSource
    std::ostringstream fs;
    fs << frgSdr.rdbuf();
    // Close file
    frgSdr.close();
    // Store data
    std::string fsc = fs.str();
    const char *fragmentShaderSource = fsc.c_str();

    // Vertex shader compilation
    unsigned int vertexShader;
    vertexShader = glCreateShader(GL_VERTEX_SHADER);
    glShaderSource(vertexShader, 1, &vertexShaderSource, NULL);
    glCompileShader(vertexShader);

    // Checking vertex shader compilation
    int success;
    char infoLog[512];
    glGetShaderiv(vertexShader, GL_COMPILE_STATUS, &success);
    if (!success)
    {
        glGetShaderInfoLog(vertexShader, 512, NULL, infoLog);
        std::cout << "ERROR::SHADER::VERTEX::COMPILATION_FAILED: \n"
                  << infoLog << std::endl;
    }

    // Fragment shader compilation
    unsigned int fragmentShader;
    fragmentShader = glCreateShader(GL_FRAGMENT_SHADER);
    glShaderSource(fragmentShader, 1, &fragmentShaderSource, NULL);
    glCompileShader(fragmentShader);

    // Checking fragment shader compilation
    glGetShaderiv(fragmentShader, GL_COMPILE_STATUS, &success);
    if (!success)
    {
        glGetShaderInfoLog(fragmentShader, 512, NULL, infoLog);
        std::cout << "ERROR::SHADER::FRAGMENT::COMPILATION_FAILED: \n"
                  << infoLog << std::endl;
    }

    // Attaching compiled shaders to final shader
    shaderProgram = glCreateProgram();
    glAttachShader(shaderProgram, vertexShader);
    glAttachShader(shaderProgram, fragmentShader);
    glLinkProgram(shaderProgram);

    // Checking shaders links
    glGetProgramiv(shaderProgram, GL_LINK_STATUS, &success);
    if (!success)
    {
        glGetProgramInfoLog(shaderProgram, 512, NULL, infoLog);
        std::cout << "ERROR::SHADER_PROGRAM::LINK_FAILED\n"
                  << infoLog << std::endl;
    }

    // Deleting the shaders as they are already linked
    glDeleteShader(vertexShader);
    glDeleteShader(fragmentShader);
}

// Class deconstructor
Shader::~Shader()
{
}

// Use the shader program
void Shader::use()
{
    glUseProgram(shaderProgram);
}

// Uniforms
// Boolean
void Shader::setBool(const std::string name, bool value) const
{
    glUniform1i(glGetUniformLocation(shaderProgram, name.c_str()), (int)value);
}
// Integer
void Shader::setInt(const std::string name, int value) const
{
    glUniform1i(glGetUniformLocation(shaderProgram, name.c_str()), value);
}
// Float
void Shader::setFloat(const std::string name, float value) const
{
    glUniform1f(glGetUniformLocation(shaderProgram, name.c_str()), value);
}
// Vector 2
void Shader::setVec2(const std::string name, glm::vec2 value) const
{
    glUniform2fv(glGetUniformLocation(shaderProgram, name.c_str()), 1, &value[0]);
}
// Vector 3
void Shader::setVec3(const std::string name, glm::vec3 value) const
{
    glUniform3fv(glGetUniformLocation(shaderProgram, name.c_str()), 1, &value[0]);
}
// Vector 4
void Shader::setVec4(const std::string name, glm::vec4 value) const
{
    glUniform4fv(glGetUniformLocation(shaderProgram, name.c_str()), 1, &value[0]);
}
// Matrix 2
void Shader::setMat2(const std::string name, const glm::mat2 value) const
{
    glUniformMatrix2fv(glGetUniformLocation(shaderProgram, name.c_str()), 1, GL_FALSE, &value[0][0]);
}
// Matrix 3
void Shader::setMat3(const std::string name, const glm::mat3 value) const
{
    glUniformMatrix3fv(glGetUniformLocation(shaderProgram, name.c_str()), 1, GL_FALSE, &value[0][0]);
}
// Matrix 4
void Shader::setMat4(const std::string name, const glm::mat4 &value) const
{
    glUniformMatrix4fv(glGetUniformLocation(shaderProgram, name.c_str()), 1, GL_FALSE, &value[0][0]);
}