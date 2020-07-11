using ManagedWinapi.Windows;
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
    public interface IWindowService
    {
        event EventHandler WindowPositionsChanged;

        SystemWindow[] CaptureWindowsOfInterest();
    }

    public class WindowService : IWindowService, IDisposable
    {
        /// <summary>
        /// Stores list of events which will 
        /// </summary>
        private readonly IEnumerable<User32Events> _windowPositionChangedUser32Events = new User32Events[]
        {
            User32Events.EVENT_SYSTEM_MOVESIZEEND, // Movement or resizing of a window has finished
            User32Events.EVENT_SYSTEM_FOREGROUND, // This seems to cover most moves involving snaps and minimize / restore
            User32Events.EVENT_SYSTEM_CAPTUREEND  // Any movements around clicking / dragging (in case it's missed by the other events)
        };

        public event EventHandler WindowPositionsChanged;

        public WindowService(ILogger<WindowService> logger)
        {
            _logger = logger;

            attachToSystemEvents();
        }

        ~WindowService()
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
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // dispose managed state (managed objects)
                }

                // free unmanaged resources (unmanaged objects) and override finalizer

                detachFromSystemEvents();

                // set large fields to null

                _disposedValue = true;
            }
        }

        public SystemWindow[] CaptureWindowsOfInterest()
        {
            return SystemWindow.AllToplevelWindows
                .Where(row =>
                {
                    var success = DwmApi.DwmGetWindowAttribute(row.HWnd, (int)DwmApi.DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, out IntPtr result, sizeof(int));

                    var resFlag = (DwmApi.DWM_WINDOW_ATTR_CLOAKED_REASON)result.ToInt32();
                    bool isCloaked = resFlag.HasFlag(DwmApi.DWM_WINDOW_ATTR_CLOAKED_REASON.DWM_CLOAKED_APP)
                                     || resFlag.HasFlag(DwmApi.DWM_WINDOW_ATTR_CLOAKED_REASON.DWM_CLOAKED_INHERITED)
                                     //|| resFlag.HasFlag(DwmApi.DWM_WINDOW_ATTR_CLOAKED_REASON.DWM_CLOAKED_SHELL) // otherwise windows on other virtual desktops are not restored
                                     ;

                    return row.Parent.HWnd.ToInt64() == 0
                           && !string.IsNullOrEmpty(row.Title)
                           && !isCloaked
                           // && !row.Title.Equals("Program Manager")
                           //&& !row.Title.Contains("Task Manager")
                           && row.Visible;
                }).ToArray();
        }

        private readonly List<IntPtr> _monitoredWinEventsHookHandles = new List<IntPtr>();
        private readonly ILogger<WindowService> _logger;
        private bool _disposedValue;

        private User32.WinEventDelegate _handleWindowPositionChangedDelegate;

        private void attachToSystemEvents()
        {
            _handleWindowPositionChangedDelegate = handleWindowPositionChanged;

            foreach (var user32Event in _windowPositionChangedUser32Events)
            {
                var winEventsHookHandle = User32.SetWinEventHook((uint)user32Event, (uint)user32Event, IntPtr.Zero, _handleWindowPositionChangedDelegate, 0, 0, (uint)User32Events.WINEVENT_OUTOFCONTEXT);
                _monitoredWinEventsHookHandles.Add(winEventsHookHandle);
            }

            // Add other User32Events here and use monitoredWinEventsHookHandles to store handles. Modification
            // of detachFromSystemEvents will be not needed.
        }

        private void detachFromSystemEvents()
        {
            foreach (var handle in _monitoredWinEventsHookHandles)
            {
                User32.UnhookWinEvent(handle);
            }

            _monitoredWinEventsHookHandles.Clear();
            _handleWindowPositionChangedDelegate = null;
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
