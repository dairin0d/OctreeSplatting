// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System.Numerics;

namespace OctreeSplatting {
    public struct InstanceInfo {
        public Matrix4x4 Matrix;
        public Octree Octree;
        public uint RootAddress;
    }
}