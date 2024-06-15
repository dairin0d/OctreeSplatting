// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System;
using System.Runtime.InteropServices;

namespace OctreeSplatting {
    public struct UnsafeRef {
        public object Source;
        public GCHandle Handle;
        public IntPtr Pointer;
        
        public static unsafe implicit operator void*(UnsafeRef r) => r.Pointer.ToPointer();
        
        public void Set(object source) {
            if (Source == source) return;
            Clear();
            if (source == null) return;
            Source = source;
            Handle = GCHandle.Alloc(source, GCHandleType.Pinned);
            Pointer = Handle.AddrOfPinnedObject();
        }
        
        public void Set(IntPtr pointer) {
            Clear();
            Pointer = pointer;
        }
        
        public void Clear() {
            if (Handle.IsAllocated) Handle.Free();
            Source = null;
            Handle = default;
            Pointer = default;
        }
    }
}
