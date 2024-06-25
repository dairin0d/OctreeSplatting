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
            bool isPerspective = (ZSlope > 1e-16f) | (ZSlope < -1e-16f);
            var eyeZ = -ZIntercept / ZSlope;
            float minZ = 0;
            
            int level = 0;
            ref var stackItem = ref stack[level];
            stackItem.Mask = mask;
            stackItem.Data = rootData;
            minZ = float.MaxValue;
            for (int i = 0; i < 8; i++) {
                var corner = GridCornerIndices[i];
                stackItem.Grid[corner] = cage[i];
                if (stackItem.Grid[corner].Position.Z < minZ) {
                    minZ = stackItem.Grid[corner].Position.Z;
                }
            }
            Subdivide(stackItem.Grid, ZIntercept, ZSlope);
            stackItem.Queue = CalculateQueue(stackItem.Grid, isPerspective, eyeZ, minZ);
            
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
                        level++;
                        stackItem = ref stack[level];
                        stackItem.Mask = mask;
                        stackItem.Data = state.Data;
                        minZ = float.MaxValue;
                        for (int i = 0; i < 8; i++) {
                            var corner = GridCornerIndices[i];
                            stackItem.Grid[corner] = state.Grid[state.Indices[i]];
                            if (stackItem.Grid[corner].Position.Z < minZ) {
                                minZ = stackItem.Grid[corner].Position.Z;
                            }
                        }
                        Subdivide(stackItem.Grid, ZIntercept, ZSlope);
                        stackItem.Queue = CalculateQueue(stackItem.Grid, isPerspective, eyeZ, minZ);
                    }
                }
                
                level--;
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
        
        private static uint CalculateQueue(ProjectedVertex[] grid, bool isPerspective, float eyeZ, float minZ) {
            var startingOctant = (isPerspective & (minZ < 0))
                ? CalculateStartingOctantPerspective(grid, eyeZ)
                : CalculateStartingOctant(grid);
            return OctantOrder.SparseQueues[(startingOctant << 8) | 255].Octants;
        }
        
        private static int CalculateStartingOctant(ProjectedVertex[] grid) {
            float xx = grid[2*1+1*3+1*9].Projection.X - grid[1*1+1*3+1*9].Projection.X;
            float xy = grid[2*1+1*3+1*9].Projection.Y - grid[1*1+1*3+1*9].Projection.Y;
            float yx = grid[1*1+2*3+1*9].Projection.X - grid[1*1+1*3+1*9].Projection.X;
            float yy = grid[1*1+2*3+1*9].Projection.Y - grid[1*1+1*3+1*9].Projection.Y;
            float zx = grid[1*1+1*3+2*9].Projection.X - grid[1*1+1*3+1*9].Projection.X;
            float zy = grid[1*1+1*3+2*9].Projection.Y - grid[1*1+1*3+1*9].Projection.Y;
            int bitX = (yy * zx <= yx * zy ? 0 : 1);
            int bitY = (zy * xx <= zx * xy ? 0 : 2);
            int bitZ = (xy * yx <= xx * yy ? 0 : 4);
            return bitX | bitY | bitZ;
        }
        
        private static int CalculateStartingOctantPerspective(ProjectedVertex[] grid, float eyeZ) {
            float px = 0 - grid[1*1+1*3+1*9].Position.X;
            float py = 0 - grid[1*1+1*3+1*9].Position.Y;
            float pz = eyeZ - grid[1*1+1*3+1*9].Position.Z;
            float xx = grid[2*1+1*3+1*9].Position.X - grid[1*1+1*3+1*9].Position.X;
            float xy = grid[2*1+1*3+1*9].Position.Y - grid[1*1+1*3+1*9].Position.Y;
            float xz = grid[2*1+1*3+1*9].Position.Z - grid[1*1+1*3+1*9].Position.Z;
            float yx = grid[1*1+2*3+1*9].Position.X - grid[1*1+1*3+1*9].Position.X;
            float yy = grid[1*1+2*3+1*9].Position.Y - grid[1*1+1*3+1*9].Position.Y;
            float yz = grid[1*1+2*3+1*9].Position.Z - grid[1*1+1*3+1*9].Position.Z;
            float zx = grid[1*1+1*3+2*9].Position.X - grid[1*1+1*3+1*9].Position.X;
            float zy = grid[1*1+1*3+2*9].Position.Y - grid[1*1+1*3+1*9].Position.Y;
            float zz = grid[1*1+1*3+2*9].Position.Z - grid[1*1+1*3+1*9].Position.Z;
            float dotX = (yy*zz - yz*zy)*px + (yz*zx - yx*zz)*py + (yx*zy - yy*zx)*pz;
            float dotY = (zy*xz - zz*xy)*px + (zz*xx - zx*xz)*py + (zx*xy - zy*xx)*pz;
            float dotZ = (xy*yz - xz*yy)*px + (xz*yx - xx*yz)*py + (xx*yy - xy*yx)*pz;
            int bitX = (dotX <= 0 ? 0 : 1);
            int bitY = (dotY <= 0 ? 0 : 2);
            int bitZ = (dotZ <= 0 ? 0 : 4);
            return bitX | bitY | bitZ;
        }
        
    }
}