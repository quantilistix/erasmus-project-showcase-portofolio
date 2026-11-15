#version 450
layout(location=0) in vec4 positionSize;
layout(location=1) in vec4 uvLayerSize;
layout(location=2) in float dustShade;
layout(location=0) out vec4 pointUv;
layout(location=1) out float pointSize;
layout(location=2) out float pointShade;
void main()
{
    gl_Position=vec4(positionSize.xyz,1);
    pointUv=uvLayerSize; pointSize=positionSize.w; pointShade=dustShade;
}
