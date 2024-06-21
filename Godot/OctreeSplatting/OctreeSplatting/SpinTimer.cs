// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2021 dairin0d https://github.com/dairin0d

using System.Threading;

namespace OctreeSplatting {
    public class SpinTimer {
        public volatile uint Ticks;
        private bool running;
        private object lockObject = new object();

        public void StartCounter() {
            running = true;
            var thread = new Thread(() => {
                while (running) {
                    Ticks++;
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        public void Reset() {
            lock (lockObject) {
                Ticks = 0;
            }
        }

        public void StopCounter() {
            lock (lockObject) {
                running = false;
            }
        }
    }
}
