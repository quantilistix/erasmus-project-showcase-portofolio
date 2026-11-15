#version 450

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;

layout(set = 0, binding = 0) uniform Transform
{
    mat4 model;
    mat4 view;
    mat4 projection;
};

layout(location = 0) out vec3 fragPosition;
layout(location = 1) out vec3 fragNormal;

void main()
{
    vec4 worldPos = model * vec4(inPosition, 1.0);
    fragPosition = worldPos.xyz;
    fragNormal = mat3(transpose(inverse(model))) * inNormal;
    gl_Position = projection * view * worldPos;
}
