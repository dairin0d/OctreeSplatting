// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;

namespace OctreeSplatting.OpenTKDemo {
    public static class Program {
        private static void Main() {
            var nativeWindowSettings = new NativeWindowSettings() {
                Size = new Vector2i(640, 480),
                Title = "CPU Octree Splatting",
            };

            using (var window = new Window(GameWindowSettings.Default, nativeWindowSettings)) {
                window.Run();
            }
        }
    }
}
