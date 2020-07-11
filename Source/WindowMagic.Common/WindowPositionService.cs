using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WindowMagic.Common.WinApiBridge;

namespace WindowMagic.Common
{
    /// <summary>
    /// Handles interaction with Windows Api's responsible for changing system state.
    /// </summary>
    public interface IWindowPositionService
    {
        event EventHandler WindowPositionsChanged;
    }

    public class WindowPositionService : IWindowPositionService, IDisposable
    {
        /// <summary>
        /// Stores list of events which will 
        /// </summary>
        private readonly IEnumerable<User32Events> windowPositionChangedUser32Events = new User32Events[]
        {
            User32Events.EVENT_SYSTEM_MOVESIZEEND, // Movement or resizing of a window has finished
            User32Events.EVENT_SYSTEM_FOREGROUND, // This seems to cover most moves involving snaps and minimize / restore
            User32Events.EVENT_SYSTEM_CAPTUREEND  // Any movements around clicking / dragging (in case it's missed by the other events)
        };

        public event EventHandler WindowPositionsChanged;

        public WindowPositionService(ILogger<WindowPositionService> logger)
        {
            _logger = logger;

            attachToSystemEvents();
        }

        ~WindowPositionService()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: false);
        }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // dispose managed state (managed objects)
                }

                // free unmanaged resources (unmanaged objects) and override finalizer

                detachFromSystemEvents();

                // set large fields to null

                disposedValue = true;
            }
        }

        private readonly List<IntPtr> monitoredWinEventsHookHandles = new List<IntPtr>();
        private readonly ILogger<WindowPositionService> _logger;
        private bool disposedValue;

        private void attachToSystemEvents()
        {
            foreach (var user32Event in windowPositionChangedUser32Events)
            {
                var winEventsHookHandle = User32.SetWinEventHook((uint)user32Event, (uint)user32Event, IntPtr.Zero, handleWindowPositionChanged, 0, 0, (uint)User32Events.WINEVENT_OUTOFCONTEXT);
                monitoredWinEventsHookHandles.Add(winEventsHookHandle);
            }

            // Add other User32Events here and use monitoredWinEventsHookHandles to store handles. Modification
            // of detachFromSystemEvents will be not needed.
        }

        private void detachFromSystemEvents()
        {
            foreach (var handle in monitoredWinEventsHookHandles)
            {
                User32.UnhookWinEvent(handle);
            }

            monitoredWinEventsHookHandles.Clear();
        }

        private void handleWindowPositionChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            try
            {
                _logger?.LogDebug($"Event {eventType:X2} receive started.");
                WindowPositionsChanged?.Invoke(this, EventArgs.Empty);
                _logger?.LogDebug($"Event {eventType:X2} receive completed.");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Event {eventType:X2} receive failed.");
            }
        }
    }
}
