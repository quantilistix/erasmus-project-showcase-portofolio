#version 460 core
out vec4 FragColor;

in vec2 UVcoords;

uniform sampler2DMS screenTexture;
uniform sampler2DMS screenDepth;

uniform vec2 windowSize;

void main(){
	int pixelSize = 1;
	float convolution[9] = {
		0, 0, 0,
		0, 32, 0,
		0, 0, 0
	};
	int crosshair[25] = {
		0, 0, 1, 0, 0,
		0, 0, 1, 0, 0,
		1, 1, 1, 1, 1,
		0, 0, 1, 0, 0,
		0, 0, 1, 0, 0
	};
	float sum = 6;
	for(int i = 0; i < 9; i++){
		sum += convolution[i];
	}

	vec3 color = vec3(0.0f, 0.0f, 0.0f);

	int i = 0;
	for(float xx = -1; xx < 2; xx++){
		for(float yy = -1; yy < 2; yy++){
			color += texelFetch(screenTexture, ivec2(UVcoords.x * windowSize.x / pixelSize, UVcoords.y * windowSize.y / pixelSize) * pixelSize + ivec2(xx, yy), 1).rgb * convolution[i];
			i++;
		}
	}
	if (UVcoords.x - 2 / windowSize.x > 0.5 && UVcoords.y - 2 / windowSize.y > 0.5 && UVcoords.x + 2 / windowSize.x < 0.5 && UVcoords.y + 2 / windowSize.y < 0.5)
	{
		int index = int(floor(((UVcoords.x - 0.5) * windowSize.x + 2) + floor(((UVcoords.y - 0.5) * windowSize.y + 2) / 5) * 5));
		color.r = sum * crosshair[index];
	}
	FragColor = vec4(color / sum, 1.0f);
}