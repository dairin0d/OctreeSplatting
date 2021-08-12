// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public class CageSubdivider<T> {
        public class State {
            public ProjectedVertex[] Grid;
            public int[] Indices;
            public T ParentData, Data;
            public int Level;
            public uint Octant;
        }
        
        private struct StackItem {
            public ProjectedVertex[] Grid;
            public T Data;
            public uint Order;
            public uint Queue;
            public byte Mask;
        }
        
        // 32-bit float screen-space coordinates can be halved
        // at most 128 times before they become subpixel
        public const int MaxSubdivisions = 128;
        
        private const int GridVertexCount = 3*3*3;
        private const int GridCornersCount = 2*2*2;
        private const int GridNonCornersCount = GridVertexCount - GridCornersCount;
        
        private static readonly int[] GridCornerIndices = new int[] {
            (int)GridVertex.MinMinMin,
            (int)GridVertex.MaxMinMin,
            (int)GridVertex.MinMaxMin,
            (int)GridVertex.MaxMaxMin,
            (int)GridVertex.MinMinMax,
            (int)GridVertex.MaxMinMax,
            (int)GridVertex.MinMaxMax,
            (int)GridVertex.MaxMaxMax,
        };
        
        // source vertex A, source vertex B, target vertex
        private static readonly int[] GridSubdivisionIndices = new int[] {
            // X edges
            (int)GridVertex.MinMinMin, (int)GridVertex.MaxMinMin, (int)GridVertex.MidMinMin,
            (int)GridVertex.MinMaxMin, (int)GridVertex.MaxMaxMin, (int)GridVertex.MidMaxMin,
            (int)GridVertex.MinMinMax, (int)GridVertex.MaxMinMax, (int)GridVertex.MidMinMax,
            (int)GridVertex.MinMaxMax, (int)GridVertex.MaxMaxMax, (int)GridVertex.MidMaxMax,
            
            // Y edges
            (int)GridVertex.MinMinMin, (int)GridVertex.MinMaxMin, (int)GridVertex.MinMidMin,
            (int)GridVertex.MaxMinMin, (int)GridVertex.MaxMaxMin, (int)GridVertex.MaxMidMin,
            (int)GridVertex.MinMinMax, (int)GridVertex.MinMaxMax, (int)GridVertex.MinMidMax,
            (int)GridVertex.MaxMinMax, (int)GridVertex.MaxMaxMax, (int)GridVertex.MaxMidMax,
            
            // Z edges
            (int)GridVertex.MinMinMin, (int)GridVertex.MinMinMax, (int)GridVertex.MinMinMid,
            (int)GridVertex.MaxMinMin, (int)GridVertex.MaxMinMax, (int)GridVertex.MaxMinMid,
            (int)GridVertex.MinMaxMin, (int)GridVertex.MinMaxMax, (int)GridVertex.MinMaxMid,
            (int)GridVertex.MaxMaxMin, (int)GridVertex.MaxMaxMax, (int)GridVertex.MaxMaxMid,
            
            // Faces
            (int)GridVertex.MinMidMin, (int)GridVertex.MaxMidMin, (int)GridVertex.MidMidMin,
            (int)GridVertex.MinMinMid, (int)GridVertex.MaxMinMid, (int)GridVertex.MidMinMid,
            (int)GridVertex.MinMinMid, (int)GridVertex.MinMaxMid, (int)GridVertex.MinMidMid,
            (int)GridVertex.MaxMinMid, (int)GridVertex.MaxMaxMid, (int)GridVertex.MaxMidMid,
            (int)GridVertex.MinMaxMid, (int)GridVertex.MaxMaxMid, (int)GridVertex.MidMaxMid,
            (int)GridVertex.MinMidMax, (int)GridVertex.MaxMidMax, (int)GridVertex.MidMidMax,
            
            // Center
            (int)GridVertex.MinMidMid, (int)GridVertex.MaxMidMid, (int)GridVertex.MidMidMid,
        };
        
        private static readonly int[][] SubgridCornerIndices = new int[][] {
            new int[] {
                (int)GridVertex.MinMinMin, (int)GridVertex.MidMinMin, (int)GridVertex.MinMidMin, (int)GridVertex.MidMidMin,
                (int)GridVertex.MinMinMid, (int)GridVertex.MidMinMid, (int)GridVertex.MinMidMid, (int)GridVertex.MidMidMid,
            },
            new int[] {
                (int)GridVertex.MidMinMin, (int)GridVertex.MaxMinMin, (int)GridVertex.MidMidMin, (int)GridVertex.MaxMidMin,
                (int)GridVertex.MidMinMid, (int)GridVertex.MaxMinMid, (int)GridVertex.MidMidMid, (int)GridVertex.MaxMidMid,
            },
            new int[] {
                (int)GridVertex.MinMidMin, (int)GridVertex.MidMidMin, (int)GridVertex.MinMaxMin, (int)GridVertex.MidMaxMin,
                (int)GridVertex.MinMidMid, (int)GridVertex.MidMidMid, (int)GridVertex.MinMaxMid, (int)GridVertex.MidMaxMid,
            },
            new int[] {
                (int)GridVertex.MidMidMin, (int)GridVertex.MaxMidMin, (int)GridVertex.MidMaxMin, (int)GridVertex.MaxMaxMin,
                (int)GridVertex.MidMidMid, (int)GridVertex.MaxMidMid, (int)GridVertex.MidMaxMid, (int)GridVertex.MaxMaxMid,
            },
            new int[] {
                (int)GridVertex.MinMinMid, (int)GridVertex.MidMinMid, (int)GridVertex.MinMidMid, (int)GridVertex.MidMidMid,
                (int)GridVertex.MinMinMax, (int)GridVertex.MidMinMax, (int)GridVertex.MinMidMax, (int)GridVertex.MidMidMax,
            },
            new int[] {
                (int)GridVertex.MidMinMid, (int)GridVertex.MaxMinMid, (int)GridVertex.MidMidMid, (int)GridVertex.MaxMidMid,
                (int)GridVertex.MidMinMax, (int)GridVertex.MaxMinMax, (int)GridVertex.MidMidMax, (int)GridVertex.MaxMidMax,
            },
            new int[] {
                (int)GridVertex.MinMidMid, (int)GridVertex.MidMidMid, (int)GridVertex.MinMaxMid, (int)GridVertex.MidMaxMid,
                (int)GridVertex.MinMidMax, (int)GridVertex.MidMidMax, (int)GridVertex.MinMaxMax, (int)GridVertex.MidMaxMax,
            },
            new int[] {
                (int)GridVertex.MidMidMid, (int)GridVertex.MaxMidMid, (int)GridVertex.MidMaxMid, (int)GridVertex.MaxMaxMid,
                (int)GridVertex.MidMidMax, (int)GridVertex.MaxMidMax, (int)GridVertex.MidMaxMax, (int)GridVertex.MaxMaxMax,
            },
        };
        
        public float ZIntercept = 1;
        public float ZSlope = 0;
        
        private State state = new State();
        private StackItem[] stack;
        
        public CageSubdivider() {
            state = new State();
            stack = new StackItem[MaxSubdivisions];
            
            for (int level = 0; level < MaxSubdivisions; level++) {
                stack[level].Grid = new ProjectedVertex[GridVertexCount];
            }
        }
        
        public void Subdivide(ProjectedVertex[] cage, T rootData, byte mask, System.Func<State, byte> callback) {
            int level = 0;
            ref var stackItem = ref stack[level];
            stackItem.Mask = mask;
            stackItem.Data = rootData;
            stackItem.Order = OctantOrder.SparseQueues[255].Octants;
            for (int i = 0; i < 8; i++) {
                stackItem.Grid[GridCornerIndices[i]] = cage[i];
            }
            Sort(ref stackItem);
            stackItem.Queue = stackItem.Order;
            Subdivide(stackItem.Grid, ZIntercept, ZSlope);
            
            while (level >= 0) {
                stackItem = ref stack[level];
                
                while (stackItem.Queue != 0) {
                    uint octant = stackItem.Queue & 7;
                    stackItem.Queue >>= 4;
                    
                    if ((stackItem.Mask & (1 << (int)octant)) == 0) continue;
                    
                    // This is a 0th level of subdivision, but 1st level of octree
                    state.Level = level + 1;
                    state.Octant = octant;
                    state.ParentData = stackItem.Data;
                    state.Data = default;
                    state.Grid = stackItem.Grid;
                    state.Indices = SubgridCornerIndices[octant];
                    
                    mask = callback(state);
                    
                    if ((mask != 0) & (level < (MaxSubdivisions - 1))) {
                        var order = stackItem.Order;
                        level++;
                        stackItem = ref stack[level];
                        stackItem.Mask = mask;
                        stackItem.Data = state.Data;
                        stackItem.Order = order;
                        for (int i = 0; i < 8; i++) {
                            stackItem.Grid[GridCornerIndices[i]] = state.Grid[state.Indices[i]];
                        }
                        Sort(ref stackItem);
                        stackItem.Queue = stackItem.Order;
                        Subdivide(stackItem.Grid, ZIntercept, ZSlope);
                    }
                }
                
                level--;
            }
        }
        
        private static void Sort(ref StackItem stackItem) {
            // Insertion sort
            for (int i = 4; i < 32; i += 4) {
                var queueItem = stackItem.Order >> i;
                var z = stackItem.Grid[GridCornerIndices[queueItem & 7]].Position.Z;
                
                int iNew = i;
                for (int i2 = i - 4; i2 >= 0; i2 -= 4) {
                    var queueItem2 = stackItem.Order >> i2;
                    var z2 = stackItem.Grid[GridCornerIndices[queueItem2 & 7]].Position.Z;
                    if (z > z2) break;
                    iNew -= 4;
                }
                
                if (iNew != i) {
                    int offset = i - iNew;
                    var affectedBits = (uint.MaxValue >> (28 - i)) & (uint.MaxValue << iNew);
                    var movedBits = affectedBits & (affectedBits >> 4);
                    stackItem.Order = (stackItem.Order & ~affectedBits) |
                        ((stackItem.Order & movedBits) << 4) | ((queueItem & 15) << iNew);
                }
            }
        }
        
        private static void Subdivide(ProjectedVertex[] grid, float zIntercept, float zSlope) {
            const int SubdivisionIndicesCount = GridNonCornersCount * 3;
            
            for (int i = 0; i < SubdivisionIndicesCount; i += 3) {
                ref var vertex0 = ref grid[GridSubdivisionIndices[i+0]];
                ref var vertex1 = ref grid[GridSubdivisionIndices[i+1]];
                ref var midpoint = ref grid[GridSubdivisionIndices[i+2]];
                
                midpoint.Position.X = (vertex0.Position.X + vertex1.Position.X) * 0.5f;
                midpoint.Position.Y = (vertex0.Position.Y + vertex1.Position.Y) * 0.5f;
                midpoint.Position.Z = (vertex0.Position.Z + vertex1.Position.Z) * 0.5f;
                
                float scale = 1f / (zIntercept + zSlope * midpoint.Position.Z);
                midpoint.Projection.X = midpoint.Position.X * scale;
                midpoint.Projection.Y = midpoint.Position.Y * scale;
            }
        }
    }
}