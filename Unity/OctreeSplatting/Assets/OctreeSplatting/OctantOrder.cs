// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System.Numerics;

namespace OctreeSplatting {
    public static class OctantOrder {
        // Node traversal order and traversal state can be combined into a
        // bit-string "queue" of octant indices (can also take into account
        // different number of stored octants). When a node is "dequeued",
        // the bit-string shifts by 4 bits. 3 bits for octant index,
        // 1 bit for signifying a non-empty queue item.
        
        public struct Queue {
            public uint Octants;
            public uint Indices;
        }
        
        public const int XYZ = 0, XZY = 1, YXZ = 2, YZX = 3, ZXY = 4, ZYX = 5;
        
        public static readonly int[] Counts;
        public static readonly int[] OctantToIndex;
        public static readonly int[] IndexToOctant;
        public static readonly Queue[] SparseQueues;
        public static readonly Queue[] PackedQueues;
        
        public static int Key(in Matrix4x4 matrix) {
            return ((Order(in matrix) << 3) | Octant(in matrix)) << 8;
        }
        
        public static int Octant(in Matrix4x4 matrix) {
            // Here we check which side of YZ/XZ/XY planes the view vector belongs to.
            // Expected coordinate system: X right, Y up, Z forward.
            int bitX = (matrix.M22 * matrix.M31 <= matrix.M21 * matrix.M32 ? 0 : 1);
            int bitY = (matrix.M32 * matrix.M11 <= matrix.M31 * matrix.M12 ? 0 : 2);
            int bitZ = (matrix.M12 * matrix.M21 <= matrix.M11 * matrix.M22 ? 0 : 4);
            return bitX | bitY | bitZ;
        }
        
        public static int Order(in Matrix4x4 matrix) {
            return Order(matrix.M13, matrix.M23, matrix.M33);
        }
        public static int Order(float XZ, float YZ, float ZZ) {
            if (XZ < 0f) XZ = -XZ;
            if (YZ < 0f) YZ = -YZ;
            if (ZZ < 0f) ZZ = -ZZ;
            return (XZ <= YZ
                ? (XZ <= ZZ ? (YZ <= ZZ ? XYZ : XZY) : ZXY)
                : (YZ <= ZZ ? (XZ <= ZZ ? YXZ : YZX) : ZYX));
        }
        
        static OctantOrder() {
            Counts = MakeCounts();
            (OctantToIndex, IndexToOctant) = MakeMaps();
            (PackedQueues, SparseQueues) = MakeQueues();
        }
        
        private static int[] MakeCounts() {
            var counts = new int[256];
            
            for (int mask = 0; mask < counts.Length; mask++) {
                int count = 0;
                for (int bits = mask; bits != 0; bits >>= 1) {
                    if ((bits & 1) != 0) count++;
                }
                counts[mask] = count;
            }
            
            return counts;
        }
        
        private static (int[] ToIndex, int[] ToOctant) MakeMaps() {
            var octantToIndex = new int[256*8];
            var indexToOctant = new int[256*8];
            
            for (int mask = 0; mask < 256; mask++) {
                int maskKey = mask << 3;
                int maxIndex = -1;
                for (int octant = 0; octant < 8; octant++) {
                    int index = GetOctantIndex(octant, mask);
                    octantToIndex[maskKey|octant] = index;
                    if (index > maxIndex) maxIndex = index;
                    if (index < 0) continue;
                    indexToOctant[maskKey|index] = octant;
                }
                for (int index = maxIndex+1; index < 8; index++) {
                    indexToOctant[maskKey|index] = -1;
                }
            }
            
            return (octantToIndex, indexToOctant);
        }
        
        private static int GetOctantIndex(int octant, int mask) {
            int index = -1;
            for (; (mask != 0) & (octant >= 0); mask >>= 1, octant--) {
                if ((mask & 1) != 0) index++;
            }
            return index;
        }
        
        private static (Queue[] Packed, Queue[] Sparse) MakeQueues() {
            var packedQueues = new Queue[6*8*256];
            for (int order = 0; order < 6; order++) {
                for (int octant = 0; octant < 8; octant++) {
                    for (int mask = 0; mask < 256; mask++) {
                        packedQueues[(((order << 3) | octant) << 8) | mask] = MakeQueue(octant, order, mask, true);
                    }
                }
            }
            
            var sparseQueues = new Queue[packedQueues.Length];
            for (int i = 0; i < sparseQueues.Length; i++) {
                sparseQueues[i].Octants = sparseQueues[i].Indices = packedQueues[i].Octants;
            }
            
            return (packedQueues, sparseQueues);
        }
        
        private static Queue MakeQueue(int start, int order, int mask, bool packed = false) {
            int uShift = 0, vShift = 0, wShift = 0;
            switch (order) {
            case XYZ: uShift = 0; vShift = 1; wShift = 2; break;
            case XZY: uShift = 0; vShift = 2; wShift = 1; break;
            case YXZ: uShift = 1; vShift = 0; wShift = 2; break;
            case YZX: uShift = 1; vShift = 2; wShift = 0; break;
            case ZXY: uShift = 2; vShift = 0; wShift = 1; break;
            case ZYX: uShift = 2; vShift = 1; wShift = 0; break;
            }
            
            var map = OctantToIndex;
            
            Queue queue = default;
            int shift = 0;
            for (int w = 0; w <= 1; w++) {
                for (int v = 0; v <= 1; v++) {
                    for (int u = 0; u <= 1; u++) {
                        int flip = (u << uShift) | (v << vShift) | (w << wShift);
                        int octant = (start ^ flip);
                        if ((mask & (1 << octant)) == 0) continue;
                        int index = packed ? map[(mask << 3)|octant] : octant;
                        queue.Octants |= (uint)((octant|8) << shift);
                        queue.Indices |= (uint)((index|8) << shift);
                        shift += 4;
                    }
                }
            }
            
            return queue;
        }
    }
}
