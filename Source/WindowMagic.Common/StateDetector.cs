using System;
using System.Collections.Generic;
using System.Threading;
using ManagedWinapi.Windows;
using WindowMagic.Common.Diagnostics;

namespace WindowMagic.Common
{
    internal static class StateDetector
    {
        private const int StabilizationWaitInterval = 2500; // with value of 500 restoration started to early on some setups

        public static void WaitForWindowStabilization(Action completeCallback, int additionalDelayInMs = 0)
        {
            var previousLocations = new Dictionary<IntPtr, RECT>();
            var currentLocations = new Dictionary<IntPtr, RECT>();

            if (previousLocations.Count == 0)
            {
                var windows = WindowHelper.CaptureWindowsOfInterest();
                GetWindowLocations(previousLocations, windows);
            }

            while (true)
            {
                Log.Trace("Windows not stable, waiting...");
                //await Delay(100);
                Thread.Sleep(StabilizationWaitInterval);
                var windows = WindowHelper.CaptureWindowsOfInterest();
                GetWindowLocations(currentLocations, windows);

                if (DoLocationsMatch(previousLocations, currentLocations))
                {
                    if (additionalDelayInMs > 0)
                    {
                        Thread.Sleep(additionalDelayInMs);
                    }
                    completeCallback();
                    break;
                }

                previousLocations.Clear();
                foreach (var currentLocation in currentLocations)
                {
                    previousLocations[currentLocation.Key] = currentLocation.Value;
                }
                currentLocations.Clear();
            }
        }

        private static void GetWindowLocations(Dictionary<IntPtr, RECT> placements, IEnumerable<SystemWindow> windows)
        {
            placements.Clear();
            foreach (var window in windows)
            {
                placements[window.HWnd] = window.Position;
            }
        }

        private static bool DoLocationsMatch(Dictionary<IntPtr, RECT> previous, Dictionary<IntPtr, RECT> current)
        {
            foreach (KeyValuePair<IntPtr, RECT> prevPlacement in previous)
            {
                if (!current.ContainsKey(prevPlacement.Key)) return false;
                var currentPlacement = current[prevPlacement.Key];
                if (!currentPlacement.Equals(prevPlacement.Value)) return false;
            }

            return true;
        }

    }
}
