#version 460 core
layout (location = 0) in vec2 aPos;   // the position vector has attribute position 0
layout (location = 1) in vec2 aUV;    // the UV coords vector has attribute position 1
  
out vec2 UVcoords; // output a color to the fragment shader

// Main function
void main()
{
	gl_Position = vec4(aPos.xy, 0.0f, 1.0f);
	UVcoords = aUV;
}