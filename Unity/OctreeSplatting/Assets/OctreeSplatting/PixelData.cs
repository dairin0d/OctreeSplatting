// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System.Runtime.InteropServices;

namespace OctreeSplatting {
    [StructLayout(LayoutKind.Explicit)]
    public struct PixelData {
        [FieldOffset(0)] public int Depth;
        [FieldOffset(4)] public Color32 Color32;
        [FieldOffset(4)] public Color24 Color24;
    }
}
