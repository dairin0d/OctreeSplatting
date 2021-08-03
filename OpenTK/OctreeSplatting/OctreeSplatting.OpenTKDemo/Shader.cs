// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System;
using System.IO;
using System.Collections.Generic;

using OpenTK.Mathematics;

#if USE_OPENGL4
using OpenTK.Graphics.OpenGL4;
#else
using OpenTK.Graphics.OpenGL;
#endif

namespace OctreeSplatting.OpenTKDemo {
    public class Shader {
        public readonly int ID;

        private Dictionary<string, int> uniforms;
        private Dictionary<string, int> attributes;

        public static Shader Load(string vertPath, string fragPath) {
            return new Shader(File.ReadAllText(vertPath), File.ReadAllText(fragPath));
        }

        public Shader(string vertSource, string fragSource) {
            var vertexShader = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vertexShader, vertSource);
            CompileShader(vertexShader);

            var fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fragmentShader, fragSource);
            CompileShader(fragmentShader);

            ID = GL.CreateProgram();
            GL.AttachShader(ID, vertexShader);
            GL.AttachShader(ID, fragmentShader);
            LinkProgram(ID);

            GL.DetachShader(ID, vertexShader);
            GL.DetachShader(ID, fragmentShader);
            GL.DeleteShader(fragmentShader);
            GL.DeleteShader(vertexShader);

            uniforms = new Dictionary<string, int>(EnumerateUniformLocations(ID));
            attributes = new Dictionary<string, int>(EnumerateAttributeLocations(ID));
        }

        public void Use() {
            GL.UseProgram(ID);
        }

        public void SetUniform(string name, int value) {
            GL.Uniform1(uniforms[name], value);
        }

        public void SetUniform(string name, float value) {
            GL.Uniform1(uniforms[name], value);
        }

        public void SetUniform(string name, Vector2 value) {
            GL.Uniform2(uniforms[name], ref value);
        }

        public void SetUniform(string name, Vector3 value) {
            GL.Uniform3(uniforms[name], ref value);
        }

        public void SetUniform(string name, Vector4 value) {
            GL.Uniform4(uniforms[name], ref value);
        }

        public void SetUniform(string name, Matrix4 value) {
            GL.UniformMatrix4(uniforms[name], true, ref value);
        }

        public void SetAttribute(string name, int count, VertexAttribPointerType type, bool normalized, int stride, int start) {
            var location = attributes[name];
            GL.EnableVertexAttribArray(location);
            GL.VertexAttribPointer(location, count, type, normalized, stride, start);
        }

        private static void CompileShader(int shader) {
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var result);
            if (result == (int)All.True) return;
            throw new Exception($"Couldn't compile shader {shader}:\n{GL.GetShaderInfoLog(shader)}");
        }

        private static void LinkProgram(int program) {
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var result);
            if (result == (int)All.True) return;
            throw new Exception($"Couldn't link shader program {program}:\n{GL.GetProgramInfoLog(program)}");
        }

        private static IEnumerable<KeyValuePair<string, int>> EnumerateUniformLocations(int program) {
            GL.GetProgram(program, GetProgramParameterName.ActiveUniforms, out var count);
            for (int index = 0; index < count; index++) {
                string name = GL.GetActiveUniform(program, index, out var size, out var type);
                int location = GL.GetUniformLocation(program, name);
                yield return new KeyValuePair<string, int>(name, location);
            }
        }

        private static IEnumerable<KeyValuePair<string, int>> EnumerateAttributeLocations(int program) {
            GL.GetProgram(program, GetProgramParameterName.ActiveAttributes, out var count);
            for (int index = 0; index < count; index++) {
                string name = GL.GetActiveAttrib(program, index, out var size, out var type);
                int location = GL.GetAttribLocation(program, name);
                yield return new KeyValuePair<string, int>(name, location);
            }
        }
    }
}
