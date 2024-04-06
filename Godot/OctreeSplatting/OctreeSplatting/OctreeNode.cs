// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

namespace OctreeSplatting {
    public struct OctreeNode {
        public uint Address;
        public byte Mask;
        public Color24 Data; // our payload
    }
}
