// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System;

#if USE_OPENGL4
using OpenTK.Graphics.OpenGL4;
#else
using OpenTK.Graphics.OpenGL;
#endif

namespace OctreeSplatting.OpenTKDemo {
    public class Texture {
        public readonly int ID;

        public int Width {get; private set;}
        public int Height {get; private set;}

        public Texture() : this(GL.GenTexture()) {
        }

        public Texture(int glHandle) {
            ID = glHandle;
        }

        public void Use(TextureUnit unit) {
            GL.ActiveTexture(unit);
            GL.BindTexture(TextureTarget.Texture2D, ID);
        }
        
        public void SetPixels(PixelInternalFormat internalFormat, int width, int height,
            PixelFormat pixelFormat, PixelType pixelType, IntPtr data, bool mipmap = false)
        {
            Use(TextureUnit.Texture0);

            GL.TexImage2D(TextureTarget.Texture2D, 0, internalFormat, width, height, 0, pixelFormat, pixelType, data);

            Width = width;
            Height = height;
            
            if (mipmap) GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        }
        
        public void SetPixels<T>(PixelInternalFormat internalFormat, int width, int height,
            PixelFormat pixelFormat, PixelType pixelType, T[] data, bool mipmap = false) where T : struct
        {
            Use(TextureUnit.Texture0);

            GL.TexImage2D<T>(TextureTarget.Texture2D, 0, internalFormat, width, height, 0, pixelFormat, pixelType, data);

            Width = width;
            Height = height;
            
            if (mipmap) GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        }
        
        public void SetFilter(bool filter) {
            var minFilter = filter ? TextureMinFilter.Linear : TextureMinFilter.Nearest;
            var magFilter = filter ? TextureMagFilter.Linear : TextureMagFilter.Nearest;
            SetFilter(minFilter, magFilter);
        }
        public void SetFilter(TextureMinFilter minFilter, TextureMagFilter magFilter) {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)minFilter);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)magFilter);
        }

        public void SetWrap(TextureWrapMode wrap) {
            SetWrap(wrap, wrap);
        }
        public void SetWrap(TextureWrapMode wrapS, TextureWrapMode wrapT) {
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrapS);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrapT);
        }
    }
}
