// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

#if USE_OPENGL4
using OpenTK.Graphics.OpenGL4;
#else
using OpenTK.Graphics.OpenGL;
#endif

namespace OctreeSplatting.OpenTKDemo {
    public class Mesh {
        public readonly int vertexArray;
        public readonly int vertexBuffer;
        public readonly int elementBuffer;
        
        private float[] vertices;
        private uint[] indices;
        
        public Mesh() {
            vertexArray = GL.GenVertexArray();
            vertexBuffer = GL.GenBuffer();
            elementBuffer = GL.GenBuffer();
        }
        
        public Mesh(float[] vertices, uint[] indices) : this() {
            Use();
            SetVertices(vertices);
            SetIndices(indices);
        }
        
        public void SetVertices(float[] vertices) {
            this.vertices = vertices;
            GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);
        }
        
        public void SetIndices(uint[] indices) {
            this.indices = indices;
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, elementBuffer);
            GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * sizeof(uint), indices, BufferUsageHint.StaticDraw);
        }
        
        public void Use() {
            GL.BindVertexArray(vertexArray);
        }
        
        public void Draw() {
            GL.DrawElements(PrimitiveType.Triangles, indices.Length, DrawElementsType.UnsignedInt, 0);
        }
    }
}
