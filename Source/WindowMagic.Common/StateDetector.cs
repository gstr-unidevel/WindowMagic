using System;
using System.Collections.Generic;
using System.Threading;
using ManagedWinapi.Windows;
using Microsoft.Extensions.Logging;

namespace WindowMagic.Common
{
    public interface IStateDetector
    {
        void WaitForWindowStabilization(Action completeCallback, int additionalDelayInMs = 0);
    }

    public class StateDetector : IStateDetector
    {
        private const int STABILIZATION_WAIT_INTERVAL = 1000; 
        
        private readonly IWindowService _windowService;
        private readonly ILogger<StateDetector> _logger;

        public StateDetector(IWindowService windowService, ILogger<StateDetector> logger)
        {
            _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
            _logger = logger;
        }

        public void WaitForWindowStabilization(Action completeCallback, int additionalDelayInMs = 0)
        {
            var previousLocations = new Dictionary<IntPtr, RECT>();
            var currentLocations = new Dictionary<IntPtr, RECT>();

            if (previousLocations.Count == 0)
            {
                var windows = _windowService.CaptureWindowsOfInterest();
                getWindowLocations(previousLocations, windows);
            }

            while (true)
            {
                _logger?.LogTrace("Windows not stable, waiting...");
                //await Delay(100);
                Thread.Sleep(STABILIZATION_WAIT_INTERVAL);
                var windows = _windowService.CaptureWindowsOfInterest();
                getWindowLocations(currentLocations, windows);

                if (doLocationsMatch(previousLocations, currentLocations))
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

        private void getWindowLocations(Dictionary<IntPtr, RECT> placements, IEnumerable<SystemWindow> windows)
        {
            placements.Clear();
            foreach (var window in windows)
            {
                placements[window.HWnd] = window.Position;
            }
        }

        private bool doLocationsMatch(Dictionary<IntPtr, RECT> previous, Dictionary<IntPtr, RECT> current)
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
