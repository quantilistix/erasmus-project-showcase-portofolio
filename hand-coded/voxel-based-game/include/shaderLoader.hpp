#pragma once

// Dependencies
#include <pch.hpp>

// // Include OpenGL math library (GLM)
// #include "glm/glm.hpp"
// #include "glm/gtc/matrix_transform.hpp"
// #include "glm/gtc/type_ptr.hpp"

class Shader
{
public:
    // Class constructor
    Shader(char *vertexShaderPath, char *fragmentShaderPath, std::string name);

    // Class deconstructor
    ~Shader();

    // Use the shader program
    void use();

    // Uniforms
    // Boolean
    void setBool(const std::string name, bool value) const;
    // Integer
    void setInt(const std::string name, int value) const;
    // Float
    void setFloat(const std::string name, float value) const;
    // Vector 2
    void setVec2(const std::string name, glm::vec2 value) const;
    // Vector 3
    void setVec3(const std::string name, glm::vec3 value) const;
    // Vector 4
    void setVec4(const std::string name, glm::vec4 value) const;
    // Matrix 2
    void setMat2(const std::string name, const glm::mat2 value) const;
    // Matrix 3
    void setMat3(const std::string name, const glm::mat3 value) const;
    // Matrix 4
    void setMat4(const std::string name, const glm::mat4 &value) const;

private:
    // The shader program
    unsigned int shaderProgram;
};