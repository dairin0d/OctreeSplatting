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
        
        public Octree(OctreeNode[] nodes) {
            Nodes = nodes;
            
            NodeCount = Nodes.Length;
            AddrSize = NodeCount * 4;
            MaskSize = NodeCount;
            DataSize = NodeCount * 3;
            
            MaskSize = (MaskSize + 3) & ~3;
            DataSize = (DataSize + 3) & ~3;
            
            Buffer = new byte[AddrSize + MaskSize + DataSize];
            
            MakeBuffer();
        }
        
        private unsafe void MakeBuffer() {
            GetPointers();
            
            for (int i = 0; i < NodeCount; i++) {
                pointers.Addr[i] = pointers.Node[i].Address;
                pointers.Mask[i] = pointers.Node[i].Mask;
                pointers.Data[i] = pointers.Node[i].Data;
            }
            
            FreePointers();
        }
        
        public unsafe uint GetAddress(uint address) {
            if (!hasPointers) return 0;
            return pointers.Node[address].Address;
        }
        
        public unsafe byte GetMask(uint address) {
            if (!hasPointers) return 0;
            return pointers.Node[address].Mask;
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
