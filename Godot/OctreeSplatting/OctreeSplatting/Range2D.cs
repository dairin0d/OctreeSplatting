// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public struct Range2D {
        public int MinX, MinY, MaxX, MaxY;
        
        public int SizeX => MaxX - MinX;
        public int SizeY => MaxY - MinY;
        
        public Range2D Intersection(Range2D other) {
            return new Range2D {
                MinX = (MinX > other.MinX ? MinX : other.MinX),
                MinY = (MinY > other.MinY ? MinY : other.MinY),
                MaxX = (MaxX < other.MaxX ? MaxX : other.MaxX),
                MaxY = (MaxY < other.MaxY ? MaxY : other.MaxY),
            };
        }
    }
}
