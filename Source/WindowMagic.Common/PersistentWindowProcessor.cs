using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ManagedWinapi.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WindowMagic.Common.Models;
using WindowMagic.Common.WinApiBridge;

namespace WindowMagic.Common
{
    public class PersistentWindowProcessor : IDisposable
    {
        private const int DELAYED_CAPTURE_TIME = 3500;

        /// <summary>
        /// Read and update this from a config file eventually
        /// </summary>
        private readonly Dictionary<string, SortedDictionary<string, ApplicationDisplayMetrics>> _monitorApplications = new Dictionary<string, SortedDictionary<string, ApplicationDisplayMetrics>>();

        private readonly object _displayChangeLock = new object();
        private readonly IDesktopDisplayMetricsService _desktopDisplayMetricsService;
        private readonly IStateDetector _stateDetector;
        private readonly IWindowPositionService _windowPositionService;
        private readonly ILogger<PersistentWindowProcessor> _logger;

        private readonly Timer _delayedCaptureTimer;

        private bool _isSessionLocked = false;
        private bool _isRestoring = false;

        /// <summary>
        /// Force ignoring capture requests. Typically done between first point where restore needed and when restore completes.
        /// </summary>
        private bool _ignoreCaptureRequests = false;

        public PersistentWindowProcessor(IDesktopDisplayMetricsService desktopDisplayMetricsService, IStateDetector stateDetector, IWindowPositionService windowPositionService, ILogger<PersistentWindowProcessor> logger)
        {
            _desktopDisplayMetricsService = desktopDisplayMetricsService ?? throw new ArgumentNullException(nameof(desktopDisplayMetricsService));
            _stateDetector = stateDetector ?? throw new ArgumentNullException(nameof(stateDetector));
            _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
            _logger = logger;

            _delayedCaptureTimer = new Timer(state =>
            {
                _logger?.LogTrace("Delayed capture timer triggered");
                beginCaptureApplicationsOnCurrentDisplays();
            });
        }

        public void Start()
        {
            captureApplicationsOnCurrentDisplays(initialCapture: true);

            attachEventHandlers();
        }

        public void Stop()
        {
            detachEventHandlers();

            _delayedCaptureTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void attachEventHandlers()
        {
            _logger?.LogInformation("Event handlers attach started.");

            SystemEvents.DisplaySettingsChanging += displaySettingsChangingHandler;
            SystemEvents.DisplaySettingsChanged += displaySettingsChangedHandler;
            SystemEvents.PowerModeChanged += powerModeChangedHandler;
            SystemEvents.SessionSwitch += sessionSwitchEventHandler;
            _windowPositionService.WindowPositionsChanged += windowPositionChangedHandler;

            _logger?.LogInformation("Event handlers attach completed.");
        }

        private void detachEventHandlers()
        {
            _logger?.LogInformation("Event handlers detach started.");

            SystemEvents.DisplaySettingsChanging -= displaySettingsChangingHandler;
            SystemEvents.DisplaySettingsChanged -= displaySettingsChangedHandler;
            SystemEvents.PowerModeChanged -= powerModeChangedHandler;
            SystemEvents.SessionSwitch -= sessionSwitchEventHandler;
            _windowPositionService.WindowPositionsChanged -= windowPositionChangedHandler;

            _logger?.LogInformation("Event handlers detach completed.");
        }

        /// <summary>
        /// For manual invocation
        /// </summary>
        public void ForceCaptureLayout()
        {
            lock (this._displayChangeLock)
            {
                _monitorApplications.Clear();
            }

            beginCaptureApplicationsOnCurrentDisplays();
        }

        private void displaySettingsChangingHandler(object sender, EventArgs args)
        {
                _logger?.LogTrace("Display settings changing handler invoked");
                this._ignoreCaptureRequests = true;
                cancelDelayedCapture(); // Throw away any pending captures
        }

        private void displaySettingsChangedHandler(object sender, EventArgs args)
        {
            _logger?.LogTrace("Display settings changed");
            _stateDetector.WaitForWindowStabilization(() =>
            {
                // CancelDelayedCapture(); // Throw away any pending captures
                beginRestoreApplicationsOnCurrentDisplays();
            });
        }

        private bool isCaptureAllowed()
        {
            return !(this._isSessionLocked || this._ignoreCaptureRequests || this._isRestoring);
        }

        private void powerModeChangedHandler(object sender, PowerModeChangedEventArgs e)
        {
            switch (e.Mode)
            {
                case PowerModes.Suspend:
                    _logger?.LogInformation("System Suspending");
                    break;

                case PowerModes.Resume:
                    _logger?.LogInformation("System Resuming");
                    _ignoreCaptureRequests = true;
                    cancelDelayedCapture(); // Throw away any pending captures
                    beginRestoreApplicationsOnCurrentDisplays();
                    break;

                default:
                    _logger?.LogTrace("Unhandled power mode change: {0}", nameof(e.Mode));
                    break;
            }
        }

        private void sessionSwitchEventHandler(object sender, SessionSwitchEventArgs args)
        {
            if (args.Reason == SessionSwitchReason.SessionLock)
            {
                _logger?.LogTrace("Session locked");
                this._isSessionLocked = true;
            }
            else if (args.Reason == SessionSwitchReason.SessionUnlock)
            {
                _logger?.LogTrace("Session unlocked");
                this._isSessionLocked = false;
            }
        }

        private void windowPositionChangedHandler(object sender, EventArgs args)
        {
            restartDelayedCapture();
        }

        /// <summary>
        /// Primary method to begin a capture. Calling this multiple times will defer the capture, effectively
        /// preventing unnecessary processing.A "debouncing" technique.
        /// </summary>
        private void restartDelayedCapture()
        {
            if (this._ignoreCaptureRequests)
            {
                _logger?.LogTrace("Can't restart delayed capture. Currently ignoring capture requests.");
                return;
            }

            _logger?.LogTrace("Delayed capture timer restarted");
            this._delayedCaptureTimer.Change(DELAYED_CAPTURE_TIME, Timeout.Infinite);
        }

        /// <summary>
        /// Under some circumstances (such as after display changes) we want to cancel any pending capture that
        /// may have triggered.This is most beneficial after display change to "throw away" any captures
        /// that were initiated by rogue events that happened before we are notified of the display settings change.
        /// </summary>
        private void cancelDelayedCapture()
        {
            _logger?.LogTrace("Cancelling delayed capture if pending");
            this._delayedCaptureTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void beginCaptureApplicationsOnCurrentDisplays()
        {
            if (!this.isCaptureAllowed())
            {
                _logger?.LogTrace("Ignoring capture request... IsCaptureAllowed() returned false");
                return;
            }

            var thread = new Thread(() =>
            {
                _stateDetector.WaitForWindowStabilization(() =>
                {
                    captureApplicationsOnCurrentDisplays();
                });
            })
            {
                IsBackground = true,
                Name = "PersistentWindowProcessor.BeginCaptureApplicationsOnCurrentDisplays()"
            };
            thread.Start();

        }

        private void captureApplicationsOnCurrentDisplays(string displayKey = null, bool initialCapture = false)
        {            
            lock(_displayChangeLock)
            {
                if (displayKey == null)
                {
                    var metrics = _desktopDisplayMetricsService.AcquireMetrics();
                    displayKey = metrics.Key;
                }

                if (!_monitorApplications.ContainsKey(displayKey))
                {
                    _monitorApplications.Add(displayKey, new SortedDictionary<string, ApplicationDisplayMetrics>());
                }

                List<string> updateLogs = new List<string>();
                List<ApplicationDisplayMetrics> updateApps = new List<ApplicationDisplayMetrics>();
                var appWindows =  WindowHelper.CaptureWindowsOfInterest();
                
                foreach (var window in appWindows)
                {
                    if (hasWindowChanged(displayKey, window, out ApplicationDisplayMetrics curDisplayMetrics))
                    {
                        updateApps.Add(curDisplayMetrics);
                        string log = string.Format("Captured {0,-8} at ({1}, {2}) of size {3} x {4} V:{5} {6} ",
                            curDisplayMetrics,
                            curDisplayMetrics.ScreenPosition.Left,
                            curDisplayMetrics.ScreenPosition.Top,
                            curDisplayMetrics.ScreenPosition.Width,
                            curDisplayMetrics.ScreenPosition.Height,
                            window.Visible,
                            window.Title
                            );
                        string log2 = string.Format("\n    WindowPlacement.NormalPosition at ({0}, {1}) of size {2} x {3}",
                            curDisplayMetrics.WindowPlacement.NormalPosition.Left,
                            curDisplayMetrics.WindowPlacement.NormalPosition.Top,
                            curDisplayMetrics.WindowPlacement.NormalPosition.Width,
                            curDisplayMetrics.WindowPlacement.NormalPosition.Height
                            );
                        updateLogs.Add(log + log2);
                    }
                }

                _logger?.LogTrace("{0}Capturing windows for display setting {1}", initialCapture ? "Initial " : "", displayKey);

                List<string> commitUpdateLog = new List<string>();
                
                for (int i = 0; i < updateApps.Count; i++)
                {
                    ApplicationDisplayMetrics curDisplayMetrics = updateApps[i];
                    commitUpdateLog.Add(updateLogs[i]);
                    if (!_monitorApplications[displayKey].ContainsKey(curDisplayMetrics.Key))
                    {
                        _monitorApplications[displayKey].Add(curDisplayMetrics.Key, curDisplayMetrics);
                    }
                    else
                    {
                        /*
                        // partially update Normal position part of WindowPlacement
                        WindowPlacement wp = monitorApplications[displayKey][curDisplayMetrics.Key].WindowPlacement;
                        wp.NormalPosition = curDisplayMetrics.WindowPlacement.NormalPosition;
                        monitorApplications[displayKey][curDisplayMetrics.Key].WindowPlacement = wp;
                        */
                        _monitorApplications[displayKey][curDisplayMetrics.Key].WindowPlacement = curDisplayMetrics.WindowPlacement;
                        _monitorApplications[displayKey][curDisplayMetrics.Key].ScreenPosition = curDisplayMetrics.ScreenPosition;
                    }
                }

                //commitUpdateLog.Sort();
                _logger?.LogTrace("{0}{1}{2} windows captured", string.Join(Environment.NewLine, commitUpdateLog), Environment.NewLine, commitUpdateLog.Count);
            }
        }

        private bool hasWindowChanged(string displayKey, SystemWindow window, out ApplicationDisplayMetrics curDisplayMetrics)
        {
            var windowPlacement = new WindowPlacement();
            User32.GetWindowPlacement(window.HWnd, ref windowPlacement);

            // Need to get the "real" screen position that takes into account the snapped or maximized state. "NormalPosition" is used when a restore occurs
            // or when the user drags the window out of the snapped sate to 'restore' it back to what it was before. (It's a feature!)
            var screenPosition = new RECT();
            User32.GetWindowRect(window.HWnd, ref screenPosition);

            uint threadId = User32.GetWindowThreadProcessId(window.HWnd, out uint processId);

            curDisplayMetrics = new ApplicationDisplayMetrics
            {
                HWnd = window.HWnd,
#if DEBUG
                // these function calls are very cpu-intensive
                ApplicationName = window.Process.ProcessName,
#else
                ApplicationName = "",
#endif
                ProcessId = processId,

                WindowPlacement = windowPlacement,
                RecoverWindowPlacement = true,
                ScreenPosition = screenPosition
            };

            bool needUpdate = false;
            if (!_monitorApplications[displayKey].ContainsKey(curDisplayMetrics.Key))
            {
                needUpdate = true;
            }
            else
            {
                ApplicationDisplayMetrics prevDisplayMetrics = _monitorApplications[displayKey][curDisplayMetrics.Key];
                if (prevDisplayMetrics.ProcessId != curDisplayMetrics.ProcessId)
                {
                    // key collision between dead window and new window with the same hwnd
                    _monitorApplications[displayKey].Remove(curDisplayMetrics.Key);
                    needUpdate = true;
                }
                else if (!prevDisplayMetrics.ScreenPosition.Equals(curDisplayMetrics.ScreenPosition))
                {
                    needUpdate = true;

                    _logger?.LogTrace("Window position changed for: {0} {1} {2}.",
                        window.Process.ProcessName, processId, window.HWnd.ToString("X8"));
                }
                else if (!prevDisplayMetrics.EqualPlacement(curDisplayMetrics))
                {
                    needUpdate = true;

                    _logger?.LogTrace("Window placement changed for: {0} {1} {2}.",
                        window.Process.ProcessName, processId, window.HWnd.ToString("X8"));

                    //string log = string.Format("prev WindowPlacement ({0}, {1}) of size {2} x {3}",
                    //    prevDisplayMetrics.WindowPlacement.NormalPosition.Left,
                    //    prevDisplayMetrics.WindowPlacement.NormalPosition.Top,
                    //    prevDisplayMetrics.WindowPlacement.NormalPosition.Width,
                    //    prevDisplayMetrics.WindowPlacement.NormalPosition.Height
                    //    );

                    //string log2 = string.Format("\ncur  WindowPlacement ({0}, {1}) of size {2} x {3}",
                    //    curDisplayMetrics.WindowPlacement.NormalPosition.Left,
                    //    curDisplayMetrics.WindowPlacement.NormalPosition.Top,
                    //    curDisplayMetrics.WindowPlacement.NormalPosition.Width,
                    //    curDisplayMetrics.WindowPlacement.NormalPosition.Height
                    //    );
                    //_logger?.LogTrace("{0}", log + log2);
                }
            }

            return needUpdate;
        }

        private void beginRestoreApplicationsOnCurrentDisplays()
        {
            if (_isRestoring) return;
            
            _isRestoring = true; // Prevent any accidental re-reading of layout while we attempt to restore layout

            var thread = new Thread(() =>
            {
                try
                {
                    _stateDetector.WaitForWindowStabilization(() =>
                    {
                        restoreApplicationsOnCurrentDisplays();
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex.ToString());
                }
                this._isRestoring = false;
                this._ignoreCaptureRequests = false; // Resume handling of capture requests

                //this.BeginCaptureApplicationsOnCurrentDisplays();

            })
            {
                //thread.IsBackground = true;
                Name = "PersistentWindowProcessor.RestoreApplicationsOnCurrentDisplays()"
            };
            thread.Start();
        }

        private void restoreApplicationsOnCurrentDisplays(string displayKey = null)
        {
            lock (_displayChangeLock)
            {
                if (displayKey == null)
                {
                    var metrics = _desktopDisplayMetricsService.AcquireMetrics();
                    displayKey = metrics.Key;
                }

                if (!_monitorApplications.ContainsKey(displayKey)
                    || _monitorApplications[displayKey].Count == 0)
                {
                    // the display setting has not been captured yet
                    _logger?.LogTrace("Unknown display setting {0}", displayKey);
                    return;
                }

                _logger?.LogInformation("Restoring applications for {0}", displayKey);
                foreach (var window in WindowHelper.CaptureWindowsOfInterest())
                {
                    var procName = window.Process.ProcessName;
                    if (procName.Contains("CodeSetup")) // SFA: What's this about??? seems almost too specific!
                    {
                        // prevent hang in SetWindowPlacement()
                        continue;
                    }

                    var applicationKey = ApplicationDisplayMetrics.GetKey(window.HWnd, window.Process.ProcessName);

                    var prevDisplayMetrics = _monitorApplications[displayKey][applicationKey];
                    var windowPlacement = prevDisplayMetrics.WindowPlacement;

                    if (_monitorApplications[displayKey].ContainsKey(applicationKey))
                    {
                        if (!hasWindowChanged(displayKey, window, out ApplicationDisplayMetrics curDisplayMetrics))
                        {
                            continue;
                        }

                        // SetWindowPlacement will "place" the window on the correct screen based on its normal position.
                        // If the state isn't "normal/restored" the window will appear not to actually move. To solve this
                        // either a quick switch from 'restore' to whatever the target state is, or using another API
                        // to position the window.
                        bool success;
                        if (windowPlacement.ShowCmd != ShowWindowCommands.Normal)
                        {
                            var prevCmd = windowPlacement.ShowCmd;
                            windowPlacement.ShowCmd = ShowWindowCommands.Normal;
                            success = checkWin32Error(User32.SetWindowPlacement(window.HWnd, ref windowPlacement));
                            windowPlacement.ShowCmd = prevCmd;

                            _logger?.LogTrace("Toggling to normal window state for: ({0} [{1}x{2}]-[{3}x{4}]) - {5}",
                                window.Process.ProcessName,
                                windowPlacement.NormalPosition.Left,
                                windowPlacement.NormalPosition.Top,
                                windowPlacement.NormalPosition.Width,
                                windowPlacement.NormalPosition.Height,
                                success);
                        }

                        // Set final window placement data - sets "normal" position for all windows (used for de-snapping and screen ID'ing)
                        success = checkWin32Error(User32.SetWindowPlacement(window.HWnd, ref windowPlacement));

                        _logger?.LogTrace("SetWindowPlacement({0} [{1}x{2}]-[{3}x{4}]) - {5}",
                            window.Process.ProcessName,
                            windowPlacement.NormalPosition.Left,
                            windowPlacement.NormalPosition.Top,
                            windowPlacement.NormalPosition.Width,
                            windowPlacement.NormalPosition.Height,
                            success);
                        
                        // For any windows not maximized or minimized, they might be snapped. This will place them back in their current snapped positions.
                        // (Remember: NormalPosition is used when the user wants to *restore* from the snapped position when dragging)
                        if (windowPlacement.ShowCmd != ShowWindowCommands.ShowMinimized &&
                            windowPlacement.ShowCmd != ShowWindowCommands.ShowMaximized)
                        {
                            var rect = prevDisplayMetrics.ScreenPosition;
                            success = User32.SetWindowPos(
                                window.HWnd,
                                IntPtr.Zero,
                                rect.Left,
                                rect.Top,
                                rect.Width,
                                rect.Height,
                                (uint)(SetWindowPosFlags.IgnoreZOrder
                                       | SetWindowPosFlags.AsynchronousWindowPosition));

                            _logger?.LogTrace("Restoring position of non maximized/minimized window: SetWindowPos({0} [{1}x{2}]-[{3}x{4}]) - {5}",
                                window.Process.ProcessName,
                                rect.Left,
                                rect.Top,
                                rect.Width,
                                rect.Height,
                                success);
                            checkWin32Error(success);
                        }
                    }
                }
                _logger?.LogTrace("Restored windows position for display setting {0}", displayKey);
            }
        }

        private bool checkWin32Error(bool success)
        {
            if (!success)
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                _logger?.LogError(error);
            }

            return success;
        }

        private bool _isDisposed = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                detachEventHandlers();

                _isDisposed = true;
            }
        }

        ~PersistentWindowProcessor()
        {
            Dispose(false);
        }

        void IDisposable.Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
