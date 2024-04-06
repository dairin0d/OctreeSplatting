// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public class Renderbuffer {
        public const int DepthBits = 24;
        
        private int shiftX;
        private int sizeX, sizeY;
        private int[] depthData;
        private Color32[] colorData;
        private Color32[] finalPixels;
        
        public int ShiftX => shiftX;
        public int SizeX => sizeX;
        public int SizeY => sizeY;
        public int SizeZ => 1 << DepthBits;
        public int[] DepthData => depthData;
        public Color32[] ColorData => colorData;
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
            
            depthData = new int[(1 << shiftX) * sizeY];
            colorData = new Color32[(1 << shiftX) * sizeY];
            finalPixels = new Color32[sizeX * sizeY];
        }
        
        public unsafe void Begin(Color32 background) {
            if (colorData == null) return;
            
            var defaultDepth = SizeZ;
            var defaultColor = background;
            
            fixed (int* depthDataPtr = depthData)
            fixed (Color32* colorDataPtr = colorData)
            {
                for (int y = 0; y < sizeY; y++) {
                    int dataIndex = y << shiftX;
                    for (int x = 0; x < sizeX; x++, dataIndex++) {
                        depthDataPtr[dataIndex] = defaultDepth;
                        colorDataPtr[dataIndex] = defaultColor;
                    }
                }
            }
        }
        
        public unsafe void End() {
            if (colorData == null) return;
            
            int step = UseTemporalUpscaling ? 2 : 1;
            GetSamplingOffset(out int startX, out int startY);
            
            int subStepX = startX == 0 ? 1 : -1;
            int subStepY = startY == 0 ? sizeX : -sizeX;
            
            fixed (Color32* colorDataPtr = colorData)
            fixed (Color32* finalPtr = finalPixels)
            {
                for (int y = startY, yData = 0; y < sizeY; y += step, yData++) {
                    int dataIndex = yData << shiftX;
                    int colorIndex = startX + (y * sizeX);
                    for (int x = startX; x < sizeX; x += step, colorIndex += step, dataIndex++) {
                        var dataPixel = colorDataPtr + dataIndex;
                        var finalPixel = finalPtr + colorIndex;
                        *finalPixel = *dataPixel;
                    }
                }
            }
            
            FrameCount++;
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
