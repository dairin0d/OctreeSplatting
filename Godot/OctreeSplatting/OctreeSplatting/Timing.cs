using System.Diagnostics;
using System.Text;

namespace OctreeSplatting {
    public static class Timing {
        public static long Pixel;
        public static long Leaf;
        public static long Map;
        public static long Map8Pre;
        public static long Map8Loop;
        public static long Map8;
        public static long Occlusion;
        public static long Write;
        public static long Stack;
        
        public static Stopwatch stopwatch = new Stopwatch();
        
        public static string[] Lines = {
            "Pixel: {0:F2}\n",
            "Leaf: {0:F2}\n",
            "Map: {0:F2}\n",
            "Map8: {0:F2}\n",
            "* Map8Pre: {0:F2}\n",
            "* Map8Loop: {0:F2}\n",
            "Occlusion: {0:F2}\n",
            "Stack: {0:F2}\n",
            "Write: {0:F2}\n",
            "Total: {0:F2}\n",
        };
        public static double[] Times = new double[Lines.Length];
        public static int AccumCount;
        public static bool Accumulate = true;
        
        public static string Report;
        
        private static StringBuilder stringBuilder = new StringBuilder();
        
        public static void Start() {
            Pixel = 0;
            Leaf = 0;
            Map = 0;
            Map8Pre = 0;
            Map8Loop = 0;
            Map8 = 0;
            Occlusion = 0;
            Stack = 0;
            Write = 0;
            
            stopwatch.Restart();
        }
        
        public static void Stop() {
            stopwatch.Stop();
            
            if (!Accumulate) AccumCount = 0;
            
            var ms = stopwatch.ElapsedMilliseconds;
            var scale = ms / (double)(Pixel+Leaf+Map+Map8+Occlusion+Stack+Write);
            UpdateValue(0, Pixel * scale);
            UpdateValue(1, Leaf * scale);
            UpdateValue(2, Map * scale);
            UpdateValue(3, Map8 * scale);
            UpdateValue(4, Map8Pre * scale);
            UpdateValue(5, Map8Loop * scale);
            UpdateValue(6, Occlusion * scale);
            UpdateValue(7, Stack * scale);
            UpdateValue(8, Write * scale);
            UpdateValue(9, ms);
            AccumCount++;
            
            stringBuilder.Clear();
            for (var i = 0; i < Lines.Length; i++) {
                stringBuilder.AppendFormat(Lines[i], Times[i]);
            }
            Report = stringBuilder.ToString();
        }
        
        private static void UpdateValue(int index, double newValue) {
            Times[index] = (newValue + AccumCount*Times[index]) / (AccumCount+1);
        }
    }
}
