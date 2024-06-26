// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public class Octree {
        public unsafe struct Pointers {
            public OctreeNode* Node;
            public uint* Addr;
            public byte* Mask;
            public Color24* Data;
        }
        
        public readonly bool IsPacked;
        public readonly int NodeCount;
        public readonly OctreeNode[] Nodes;
        public readonly byte[] Buffer;
        public int AddrSize;
        public int MaskSize;
        public int DataSize;
        
        private bool hasPointers;
        public Pointers pointers;
        private UnsafeRef nodesRef;
        private UnsafeRef bufferRef;
        
        public Octree(OctreeNode[] nodes, bool packed = false) {
            Nodes = nodes;
            
            NodeCount = Nodes.Length;
            AddrSize = NodeCount * 4;
            MaskSize = NodeCount;
            DataSize = NodeCount * 3;
            
            MaskSize = (MaskSize + 3) & ~3;
            DataSize = (DataSize + 3) & ~3;
            
            Buffer = new byte[AddrSize + MaskSize + DataSize];
            
            GetPointers();
            IsPacked = packed;
            if (IsPacked) {
                MakePackedBuffer();
            } else {
                MakeSparseBuffer();
            }
            FreePointers();
        }
        
        private unsafe void MakeSparseBuffer() {
            for (int i = 0; i < NodeCount; i++) {
                pointers.Addr[i] = pointers.Node[i].Address;
                pointers.Mask[i] = pointers.Node[i].Mask;
                pointers.Data[i] = pointers.Node[i].Data;
            }
        }
        
        private unsafe void MakePackedBuffer() {
            uint count = 1;
            pointers.Addr[0] = 0;
            
            for (uint index = 0; index < count; index++) {
                ref var node = ref pointers.Node[pointers.Addr[index]];
                pointers.Addr[index] = count;
                pointers.Mask[index] = node.Mask;
                pointers.Data[index] = node.Data;
                for (uint octant = 0; octant < 8; octant++) {
                    if ((node.Mask & (1 << (int)octant)) == 0) continue;
                    pointers.Addr[count] = node.Address + octant;
                    count++;
                    if (count > NodeCount) {
                        Godot.GD.PrintErr("Recursion detected!");
                        pointers.Addr[0] = 0;
                        pointers.Mask[0] = 0;
                        return;
                    }
                }
            }
            
            Godot.GD.Print($"Original: {NodeCount}, Packed: {count}");
        }
        
        public unsafe uint GetAddress(uint address) {
            if (!hasPointers) return 0;
            return pointers.Addr[address];
        }
        
        public unsafe byte GetMask(uint address) {
            if (!hasPointers) return 0;
            return pointers.Mask[address];
        }
        
        public unsafe Pointers GetPointers() {
            if (!hasPointers) {
                nodesRef.Set(Nodes);
                bufferRef.Set(Buffer);
                pointers = new Pointers {
                    Node = (OctreeNode*)nodesRef,
                };
                if (Buffer != null) {
                    pointers.Addr = (uint*)bufferRef;
                    pointers.Mask = ((byte*)pointers.Addr) + AddrSize;
                    pointers.Data = (Color24*)(pointers.Mask + MaskSize);
                }
                hasPointers = true;
            }
            return pointers;
        }
        
        public void FreePointers() {
            if (!hasPointers) return;
            pointers = default;
            nodesRef.Clear();
            bufferRef.Clear();
            hasPointers = false;
        }
    }
}
