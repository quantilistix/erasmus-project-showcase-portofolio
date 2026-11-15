#version 460 core
// output the fragment color
out vec4 FragColor;

// input data from vertex shader
in vec2 UVcoords;
in vec3 FragPos;
in vec3 norm;
in float FragDepth;

// uniform texture, ambient light color, camera position and depth
uniform sampler2D texture1;
uniform vec4 ambientLight;
uniform float density = 0.01f;
uniform vec3 camPos;
//uniform image2DMS depthTexture;
layout(binding = 1, rg32f) uniform readonly image2DMS depthTexture;



// Function that calculates light color using Phong lighting system
vec4 shade(vec4 color, vec3 lightPos, vec3 viewPos, vec3 normal, vec3 pixelPos)
{
    float specularStrength = 0.1;

    vec3 lightDir = normalize(lightPos - pixelPos);
    vec3 viewDir = normalize(viewPos - pixelPos);
    vec3 reflectDir = reflect(-lightDir, norm);


    float diff = max(dot(normal, lightDir), 0.0f);
    float spec = pow(max(dot(viewDir, reflectDir), 0.0), 8) * specularStrength;
    

    vec3 rectDist = pixelPos - lightPos;
    float dist = sqrt(dot(rectDist, rectDist));
    return (vec4(color.rgb * color.a, 1.0f) * diff + spec) / dist / dist;
}

// Function that calculates light color using Phong lighting system (paralel rays)
vec4 sunShade(vec4 color, vec3 lightPos, vec3 viewPos, vec3 normal, vec3 pixelPos)
{
    float specularStrength = 0.1;
    
    vec3 lightDir = normalize(lightPos);
    vec3 viewDir = normalize(viewPos - pixelPos);
    vec3 reflectDir = reflect(-lightDir, norm);


    float diff = max(dot(normal, lightDir), 0.0f);
    float spec = pow(max(dot(viewDir, reflectDir), 0.0), 8) * specularStrength;
    

    return vec4(color.rgb * color.a, 1.0f) * diff + spec;
}

// Lerp implmenetation
float lerp(float a, float b, float t)
{
    return a + t * (b - a);
}

vec2 lerp(vec2 a, vec2 b, float t)
{
    return a + t * (b - a);
}

vec3 lerp(vec3 a, vec3 b, float t)
{
    return a + t * (b - a);
}

vec4 lerp(vec4 a, vec4 b, float t)
{
    return a + t * (b - a);
}

// Main function
void main()
{
    // Stencil routed A-buffer
    //imageStore(depthTexture, ivec2(gl_FragCoord), 0, vec4(imageLoad(depthTexture, ivec2(gl_FragCoord), 0).rgb, imageLoad(depthTexture, ivec2(gl_FragCoord), 0).a));
//    float previousDepth = texture(previousDepthTexture, UVcoords).r;
//	  if (FragDepth <= previousDepth){
//        discard;
//    }

    // Normalize vector
    vec3 normal = normalize(norm);
    // Add all lights
    vec4 light = ambientLight +
    //shade(vec4(1.0f, 0.5f, 0.2f, 32.0f), camPos, camPos, normal, FragPos) +
    sunShade(vec4(0.67f, 0.5f, 0.25f, 1.0f), vec3(3, 4, 2), camPos, normal, FragPos) +
    sunShade(vec4(0.2f, 0.5f, 1.0f, 0.5f), vec3(-3, -4, -2), camPos, normal, FragPos);
    float dist = length(FragPos - camPos);
    FragColor = lerp(ambientLight, texture(texture1, UVcoords) * light, pow(1 - density, dist));
}
// f = 1/e^((d * density)^2)