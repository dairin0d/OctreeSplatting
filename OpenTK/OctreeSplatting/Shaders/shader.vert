#version 330 core

in vec3 attribPos;
in vec2 attribUV;

out vec2 vertexUV;

uniform mat4 mvpMatrix;

void main(void) {
    vertexUV = attribUV;
    gl_Position = vec4(attribPos, 1.0) * mvpMatrix;
}
