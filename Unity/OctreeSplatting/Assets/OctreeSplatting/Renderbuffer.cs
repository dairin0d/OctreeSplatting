// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public class Renderbuffer {
        public const int DepthBits = 24;
        
        private int sizeX, sizeY;
        private PixelData[] dataPixels;
        private Color32[] colorPixels;
        
        public int SizeX => sizeX;
        public int SizeY => sizeY;
        public int SizeZ => 1 << DepthBits;
        public PixelData[] DataPixels => dataPixels;
        public Color32[] ColorPixels => colorPixels;
        
        public void Resize(int width, int height) {
            if ((width <= 0) | (height <= 0)) return;
            if ((width == sizeX) & (height == sizeY)) return;
            
            sizeX = width;
            sizeY = height;
            
            dataPixels = new PixelData[sizeX * sizeY];
            colorPixels = new Color32[sizeX * sizeY];
        }
        
        public void Begin(Color32 background) {
            if (dataPixels == null) return;
            
            var defaultValue = default(PixelData);
            defaultValue.Depth = int.MaxValue;
            defaultValue.Color32 = background;
            
            for (int y = 0; y < sizeY; y++) {
                int index = y * sizeX;
                for (int x = 0; x < sizeX; x++, index++) {
                    dataPixels[index] = defaultValue;
                }
            }
        }
        
        public void End() {
            for (int y = 0; y < sizeY; y++) {
                int index = y * sizeX;
                for (int x = 0; x < sizeX; x++, index++) {
                    colorPixels[index] = dataPixels[index].Color32;
                }
            }
        }
    }
}
