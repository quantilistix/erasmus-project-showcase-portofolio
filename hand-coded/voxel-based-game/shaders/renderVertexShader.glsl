#version 460 core
layout (location = 0) in vec3 aPos;   // the position vector has attribute position 0
layout (location = 1) in vec3 aNorm;  // the normal vector has attribute position 1
layout (location = 2) in vec2 aUV;    // the UV coords vector has attribute position 2
  
out vec2 UVcoords;   // output a color to the fragment shader
out vec3 norm;       // output the normal vector of the surface
out vec3 FragPos;    // output the position of the fragment
out float FragDepth; // output the fragment depth value

// Receive uniforms
// Rotation unifroms
uniform mat4 xRot;
uniform mat4 yRot;
uniform mat4 zRot;

// Transformation unifroms
uniform mat4 projection;
uniform mat4 translate;
uniform vec3 camPos;

// Main function
void main()
{
    // Calculate the position of vertices (camera position and rotation)
    vec4 vertex = projection * (translate * vec4(aPos, 1.0f) * xRot * yRot * zRot);
    FragDepth = vertex.w;

    // Sending the fragment position
    FragPos = aPos;

    // Send UV coords to the fragment shader
    UVcoords = aUV; // set ourColor to the input color we got from the vertex data
    // Send the normal to the fragment shader
    norm = aNorm;

    // Done!
    gl_Position = vertex;
}