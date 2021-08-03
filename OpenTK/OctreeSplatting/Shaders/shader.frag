#version 330

out vec4 fragmentColor;

in vec2 vertexUV;

uniform sampler2D texture0;

void main() {
    fragmentColor = texture(texture0, vertexUV);
}