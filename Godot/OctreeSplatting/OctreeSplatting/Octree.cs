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
        
        public OctreeNode[] Nodes;
        public byte[] Buffer;
        public int AddrCount;
        public int MaskCount;
        public int DataCount;
        
        private bool hasPointers;
        private Pointers pointers;
        private UnsafeRef nodesRef;
        private UnsafeRef bufferRef;
        
        public Octree(OctreeNode[] nodes) {
            Nodes = nodes;
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
                    pointers.Mask = ((byte*)pointers.Addr) + AddrCount*4;
                    pointers.Data = (Color24*)(pointers.Mask + MaskCount);
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
