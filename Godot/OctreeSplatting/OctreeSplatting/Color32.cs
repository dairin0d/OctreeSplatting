// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System.Runtime.InteropServices;

namespace OctreeSplatting {
    [StructLayout(LayoutKind.Explicit)]
    public struct Color32 {
        [FieldOffset(0)] public byte R;
        [FieldOffset(1)] public byte G;
        [FieldOffset(2)] public byte B;
        [FieldOffset(3)] public byte A;
        [FieldOffset(0)] public Color24 RGB;
    }
}
