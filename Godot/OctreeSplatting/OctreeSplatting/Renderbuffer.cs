// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public class Renderbuffer {
        public unsafe struct Pointers {
            public int Shift;
            public int SizeX;
            public int SizeY;
            public int SizeZ;
            public int* Depth;
            public Color32* Color;
        }
        
        public const int DepthBits = 24;
        
        private int shiftX;
        private int sizeX, sizeY;
        private int[] depthData;
        private Color32[] colorData;
        private UnsafeRef depthDataRef;
        private UnsafeRef colorDataRef;
        private Color32[] finalPixels;
        
        public int SizeX => sizeX;
        public int SizeY => sizeY;
        public int SizeZ => 1 << DepthBits;
        public Color32[] ColorPixels => finalPixels;
        
        public int DataSizeX => UseTemporalUpscaling ? sizeX >> 1 : sizeX;
        public int DataSizeY => UseTemporalUpscaling ? sizeY >> 1 : sizeY;
        
        public int FrameCount;
        
        public bool UseTemporalUpscaling;
        
        public void Resize(int width, int height) {
            if ((width <= 0) | (height <= 0)) return;
            if ((width == sizeX) & (height == sizeY)) return;
            
            sizeX = width;
            sizeY = height;
            
            for (shiftX = 0; (1 << shiftX) < sizeX; shiftX++);
            
            var dataSize = (1 << shiftX) * sizeY;
            depthData = new int[dataSize];
            colorData = new Color32[dataSize];
            
            finalPixels = new Color32[sizeX * sizeY];
        }
        
        public unsafe void Begin(Color32 background) {
            if (colorData == null) return;
            
            depthDataRef.Set(depthData);
            colorDataRef.Set(colorData);
            
            var buffers = GetBuffers();
            {
                for (int y = 0; y < buffers.SizeY; y++) {
                    int dataIndex = y << buffers.Shift;
                    for (int x = 0; x < buffers.SizeX; x++, dataIndex++) {
                        buffers.Depth[dataIndex] = buffers.SizeZ;
                        buffers.Color[dataIndex] = background;
                    }
                }
            }
        }
        
        public unsafe Pointers GetBuffers() {
            return new Pointers {
                Shift = shiftX,
                SizeX = sizeX,
                SizeY = sizeY,
                SizeZ = 1 << DepthBits,
                Depth = (int*)depthDataRef,
                Color = (Color32*)colorDataRef,
            };
        }
        
        public unsafe void End() {
            if (colorData == null) return;
            
            int step = UseTemporalUpscaling ? 2 : 1;
            GetSamplingOffset(out int startX, out int startY);
            
            var buffers = GetBuffers();
            fixed (Color32* finalPtr = finalPixels)
            {
                for (int y = startY, yData = 0; y < buffers.SizeY; y += step, yData++) {
                    int dataIndex = yData << buffers.Shift;
                    int colorIndex = startX + (y * buffers.SizeX);
                    for (int x = startX; x < buffers.SizeX; x += step, colorIndex += step, dataIndex++) {
                        var dataPixel = buffers.Color + dataIndex;
                        var finalPixel = finalPtr + colorIndex;
                        *finalPixel = *dataPixel;
                    }
                }
            }
            
            FrameCount++;
            
            depthDataRef.Clear();
            colorDataRef.Clear();
        }
        
        public void GetSamplingOffset(out float x, out float y) {
            GetSamplingOffset(out int ix, out int iy);
            x = (0.5f - ix) * 0.5f;
            y = (0.5f - iy) * 0.5f;
        }
        
        public void GetSamplingOffset(out int x, out int y) {
            x = 0; y = 0;
            
            if (!UseTemporalUpscaling) return;
            
            switch (FrameCount & 0b11) {
                case 0: x = 0; y = 0; return;
                case 1: x = 1; y = 1; return;
                case 2: x = 1; y = 0; return;
                case 3: x = 0; y = 1; return;
            }
        }
    }
}
