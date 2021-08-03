// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public struct Range2D {
        public int MinX, MinY, MaxX, MaxY;
        
        public int SizeX => MaxX - MinX;
        public int SizeY => MaxY - MinY;
        
        public void Intersect(Range2D other) {
            if (MinX < other.MinX) MinX = other.MinX;
            if (MinY < other.MinY) MinY = other.MinY;
            if (MaxX > other.MaxX) MaxX = other.MaxX;
            if (MaxY > other.MaxY) MaxY = other.MaxY;
        }
        
        public Range2D Intersection(Range2D other) {
            var copy = this;
            copy.Intersect(other);
            return copy;
        }
    }
}
