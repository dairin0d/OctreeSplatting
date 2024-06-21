using System.Diagnostics;

namespace OctreeSplatting {
    public static class Timing {
        public static long Pixel;
        public static long Leaf;
        public static long Map;
        public static long Map8Pre;
        public static long Map8Loop;
        public static long Map8Select;
        public static long Map8Write;
        public static long Map8;
        public static long Occlusion;
        public static long Stack;
        
        public static Stopwatch stopwatch = new Stopwatch();
        
        public static string Report;
        
        public static void Start() {
            Pixel = 0;
            Leaf = 0;
            Map = 0;
            Map8Pre = 0;
            Map8Loop = 0;
            Map8Select = 0;
            Map8Write = 0;
            Map8 = 0;
            Occlusion = 0;
            Stack = 0;
            
            stopwatch.Restart();
        }
        
        public static void Stop() {
            stopwatch.Stop();
            var T = (double)(Pixel+Leaf+Map+Map8+Occlusion+Stack);
            var TP = Pixel / T;
            var TL = Leaf / T;
            var TM = Map / T;
            var TM8p = Map8Pre / T;
            var TM8l = Map8Loop / T;
            var TM8s = Map8Select / T;
            var TM8w = Map8Write / T;
            var TM8 = Map8 / T;
            var TO = Occlusion / T;
            var TS = Stack / T;
            var ms = stopwatch.ElapsedMilliseconds;
            // Report = $"{ms} ms, P:{TP:P1}, L:{TL:P1}, M:{TM:P1}, M8:{TM8:P1} (p:{TM8p:P1}, l:{TM8l:P1}, s:{TM8s:P1}, w:{TM8w:P1}), O:{TO:P1}, S:{TS:P1}";
            Report = string.Join('\n',
                $"Pixel: {Pixel/T:P1}",
                $"Leaf: {Leaf/T:P1}",
                $"Map: {Map/T:P1}",
                $"Map8: {Map8/T:P1}",
                $"* Map8Pre: {Map8Pre/T:P1}",
                $"* Map8Loop: {Map8Loop/T:P1}",
                $"* Map8Select: {Map8Select/T:P1}",
                $"* Map8Write: {Map8Write/T:P1}",
                $"Occlusion: {Occlusion/T:P1}",
                $"Stack: {Stack/T:P1}",
                $"{ms} ms"
            );
        }
    }
}
