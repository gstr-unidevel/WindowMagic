using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using System.Text;
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
        private const int DELAYED_CAPTURE_TIME = 4500;

        /// <summary>
        /// Read and update this from a config file eventually
        /// </summary>
        private readonly Dictionary<string, SortedDictionary<string, ApplicationDisplayMetrics>> _desktopApplications = new Dictionary<string, SortedDictionary<string, ApplicationDisplayMetrics>>();

        private readonly object _displayChangeLock = new object();
        private readonly IDesktopService _desktopService;
        private readonly IStateDetector _stateDetector;
        private readonly IWindowService _windowService;
        private readonly ILogger<PersistentWindowProcessor> _logger;

        private readonly Timer _delayedCaptureTimer;

        private bool _isSessionLocked = false;
        private bool _isRestorePending = false;

        /// <summary>
        /// Force ignoring capture requests. Typically done between first point where restore needed and when restore completes.
        /// </summary>
        private bool _ignoreCaptureRequests = false;

        public PersistentWindowProcessor(IDesktopService desktopService, IStateDetector stateDetector, IWindowService windowPositionService, ILogger<PersistentWindowProcessor> logger)
        {
            _desktopService = desktopService ?? throw new ArgumentNullException(nameof(desktopService));
            _stateDetector = stateDetector ?? throw new ArgumentNullException(nameof(stateDetector));
            _windowService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
            _logger = logger;

            _delayedCaptureTimer = new Timer(state =>
            {
                _logger?.LogTrace("Delayed capture timer triggered");
                beginCaptureApplicationsOnCurrentDisplays();
            });
        }

        ~PersistentWindowProcessor()
        {
            Dispose(false);
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
            _windowService.WindowPositionsChanged += windowPositionChangedHandler;

            _logger?.LogInformation("Event handlers attach completed.");
        }

        private void detachEventHandlers()
        {
            _logger?.LogInformation("Event handlers detach started.");

            SystemEvents.DisplaySettingsChanging -= displaySettingsChangingHandler;
            SystemEvents.DisplaySettingsChanged -= displaySettingsChangedHandler;
            SystemEvents.PowerModeChanged -= powerModeChangedHandler;
            SystemEvents.SessionSwitch -= sessionSwitchEventHandler;
            _windowService.WindowPositionsChanged -= windowPositionChangedHandler;

            _logger?.LogInformation("Event handlers detach completed.");
        }

        /// <summary>
        /// For manual invocation
        /// </summary>
        public void ForceCaptureLayout()
        {
            lock (_displayChangeLock)
            {
                _desktopApplications.Clear();
            }

            beginCaptureApplicationsOnCurrentDisplays();
        }

        private void displaySettingsChangingHandler(object sender, EventArgs args)
        {
            _logger?.LogTrace("Display settings changing handler invoked");
            _ignoreCaptureRequests = true;

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
            return !(_isSessionLocked || _ignoreCaptureRequests || _isRestorePending);
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
                _isSessionLocked = true;
            }
            else if (args.Reason == SessionSwitchReason.SessionUnlock)
            {
                _logger?.LogTrace("Session unlocked");
                _isSessionLocked = false;
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
            if (_ignoreCaptureRequests)
            {
                _logger?.LogTrace("Can't restart delayed capture. Currently ignoring capture requests.");
                return;
            }

            _logger?.LogTrace("Delayed capture timer restarted");
            _delayedCaptureTimer.Change(DELAYED_CAPTURE_TIME, Timeout.Infinite);
        }

        /// <summary>
        /// Under some circumstances (such as after display changes) we want to cancel any pending capture that
        /// may have triggered.This is most beneficial after display change to "throw away" any captures
        /// that were initiated by rogue events that happened before we are notified of the display settings change.
        /// </summary>
        private void cancelDelayedCapture()
        {
            _logger?.LogTrace("Cancelling delayed capture if pending");
            _delayedCaptureTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void beginCaptureApplicationsOnCurrentDisplays()
        {
            if (!isCaptureAllowed())
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

        private void captureApplicationsOnCurrentDisplays(bool initialCapture = false)
        {
            lock (_displayChangeLock)
            {
                var desktopKey = _desktopService.GetDesktopKey();

                _logger?.LogInformation($"Capture applications for desktop '{desktopKey}' started.");

                try
                {

                    if (!_desktopApplications.TryGetValue(desktopKey, out var applications))
                    {
                        _logger.LogInformation($"New desktop with DesktopKey '{desktopKey}' has been identified.");
                        applications = new SortedDictionary<string, ApplicationDisplayMetrics>();
                        _desktopApplications.Add(desktopKey, applications);
                    }

                    var updateLogs = new List<string>();
                    var updateApps = new List<ApplicationDisplayMetrics>();
                    var appWindows = _windowService.CaptureWindowsOfInterest();

                    foreach (var window in appWindows)
                    {
                        if (hasWindowChanged(applications, window, out ApplicationDisplayMetrics curDisplayMetrics))
                        {
                            updateApps.Add(curDisplayMetrics);                            
                            _logger.LogTrace($"[{window.Title}][{(window.Visible ? "V" : "H")}] {curDisplayMetrics.ToString()}");
                        }
                    }

                    for (var i = 0; i < updateApps.Count; i++)
                    {
                        var curDisplayMetrics = updateApps[i];

                        if (applications.TryGetValue(curDisplayMetrics.Key, out var appMetrics))
                        {
                            appMetrics.WindowPlacement = curDisplayMetrics.WindowPlacement;
                            appMetrics.ScreenPosition = curDisplayMetrics.ScreenPosition;
                        }
                        else
                        {
                            applications.Add(curDisplayMetrics.Key, curDisplayMetrics);
                        }
                    }

                    _logger?.LogInformation($"Capture applications for desktop '{desktopKey}' completed.");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Capture applications for desktop '{desktopKey}' failed.");
                }
            }
        }

        private bool hasWindowChanged(SortedDictionary<string, ApplicationDisplayMetrics> prevDisplayMetricsCollection, SystemWindow window, out ApplicationDisplayMetrics curDisplayMetrics)
        {
            var windowPlacement = new WindowPlacement();
            User32.GetWindowPlacement(window.HWnd, ref windowPlacement);

            // Need to get the "real" screen position that takes into account the snapped or maximized state. "NormalPosition" is used when a restore occurs
            // or when the user drags the window out of the snapped sate to 'restore' it back to what it was before. (It's a feature!)
            var screenPosition = new RECT();
            User32.GetWindowRect(window.HWnd, ref screenPosition);

            User32.GetWindowThreadProcessId(window.HWnd, out uint processId);

            curDisplayMetrics = new ApplicationDisplayMetrics
            {
                HWnd = window.HWnd,
                ProcessId = processId,
                ProcessName = window.Process.ProcessName,
                WindowPlacement = windowPlacement,
                ScreenPosition = screenPosition
            };

            bool needUpdate;

            if (prevDisplayMetricsCollection.TryGetValue(curDisplayMetrics.Key, out var prevDisplayMetrics))
            {
                if (prevDisplayMetrics.ProcessId != curDisplayMetrics.ProcessId)
                {
                    // key collision between dead window and new window with the same hwnd

                    _logger?.LogWarning($"Window ProcessId has changed from {prevDisplayMetrics.ProcessId} to {curDisplayMetrics.ProcessId}. Removing from known positions collection.");

                    prevDisplayMetricsCollection.Remove(curDisplayMetrics.Key);
                    needUpdate = true;
                }
                else if (!prevDisplayMetrics.ScreenPosition.Equals(curDisplayMetrics.ScreenPosition))
                {
                    needUpdate = true;

                    _logger?.LogTrace("Window position changed for: {0} {1} {2}.", window.Process.ProcessName, processId, window.HWnd.ToString("X8"));
                }
                else if (!prevDisplayMetrics.EqualPlacement(curDisplayMetrics))
                {
                    needUpdate = true;

                    _logger?.LogTrace("Window placement changed for: {0} {1} {2}.", window.Process.ProcessName, processId, window.HWnd.ToString("X8"));
                }
                else
                {
                    needUpdate = false;

                    _logger?.LogTrace("Window position and placement not changed for: {0} {1} {2}.", window.Process.ProcessName, processId, window.HWnd.ToString("X8"));
                }
            }
            else
            {
                _logger?.LogTrace("Window is new for: {0} {1} {2}.", window.Process.ProcessName, processId, window.HWnd.ToString("X8"));
                needUpdate = true;
            }

            return needUpdate;
        }

        private void beginRestoreApplicationsOnCurrentDisplays()
        {
            if (_isRestorePending) return;

            _isRestorePending = true; // Prevent any accidental re-reading of layout while we attempt to restore layout

            var thread = new Thread(() =>
            {
                try
                {
                    _logger?.LogInformation("Restore started.");
                    _stateDetector.WaitForWindowStabilization(() => restoreApplicationsOnCurrentDisplays());
                    _logger?.LogInformation("Restore completed.");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Restore failed.");
                }
                finally
                {
                    _isRestorePending = false;
                    _ignoreCaptureRequests = false; // Resume handling of capture requests
                }
            })
            {
                Name = "PersistentWindowProcessor.RestoreApplicationsOnCurrentDisplays()"
            };

            thread.Start();
        }

        private void restoreApplicationsOnCurrentDisplays()
        {
            lock (_displayChangeLock)
            {
                var desktopKey = _desktopService.GetDesktopKey();

                _logger?.LogInformation($"Restore applications for desktop '{desktopKey}' started.");

                if (!_desktopApplications.TryGetValue(desktopKey, out var applications))
                {
                    // the display setting has not been captured yet
                    _logger?.LogWarning($"Restore applications for desktop '{desktopKey}' completed with warning (no capture data).");
                    return;
                }
                else
                {
                    if (applications.Count == 0)
                    {
                        _logger?.LogWarning($"Restore applications for desktop '{desktopKey}' completed with warning (capture data empty).");
                        return;
                    }
                }

                try
                {
                    var windowsOfInterest = _windowService.CaptureWindowsOfInterest();

                    _logger?.LogInformation($"Found {windowsOfInterest.Length} windows in WindowMagic interest.");

                    foreach (var window in windowsOfInterest)
                    {
                        var procName = window.Process.ProcessName;
                        if (procName.Contains("CodeSetup")) continue; // prevent hang in SetWindowPlacement() (SFA: What's this about??? seems almost too specific!)

                        var applicationKey = ApplicationDisplayMetrics.GetKey(window.HWnd, procName);

                        if (applications.TryGetValue(applicationKey, out var prevDisplayMetrics))
                        {
                            try
                            {
                                _logger?.LogInformation($"Restore position for '{applicationKey}' started.");

                                if (hasWindowChanged(applications, window, out var _))
                                {
                                    var windowPlacement = prevDisplayMetrics.WindowPlacement;

                                    bool success;

                                    // SetWindowPlacement will "place" the window on the correct screen based on its normal position.
                                    // If the state isn't "normal/restored" the window will appear not to actually move. To solve this
                                    // either a quick switch from 'restore' to whatever the target state is, or using another API
                                    // to position the window.

                                    if (windowPlacement.ShowCmd != ShowWindowCommands.Normal)
                                    {
                                        var prevCmd = windowPlacement.ShowCmd;

                                        windowPlacement.ShowCmd = ShowWindowCommands.Normal;
                                        success = checkWin32Error(User32.SetWindowPlacement(window.HWnd, ref windowPlacement));
                                        windowPlacement.ShowCmd = prevCmd;

                                        _logger?.LogTrace("Toggling to normal window state for: ({0}/{6} [{1}x{2}]-[{3}x{4}]) - {5}",
                                            window.Process.ProcessName,
                                            windowPlacement.NormalPosition.Left,
                                            windowPlacement.NormalPosition.Top,
                                            windowPlacement.NormalPosition.Width,
                                            windowPlacement.NormalPosition.Height,
                                            success, windowPlacement.ShowCmd.ToString());
                                    }

                                    // Set final window placement data - sets "normal" position for all windows (used for de-snapping and screen ID'ing)
                                    success = checkWin32Error(User32.SetWindowPlacement(window.HWnd, ref windowPlacement));

                                    _logger?.LogTrace("SetWindowPlacement({0}/{6} [{1}x{2}]-[{3}x{4}]) - {5}",
                                        window.Process.ProcessName,
                                        windowPlacement.NormalPosition.Left,
                                        windowPlacement.NormalPosition.Top,
                                        windowPlacement.NormalPosition.Width,
                                        windowPlacement.NormalPosition.Height,
                                        success, windowPlacement.ShowCmd.ToString());

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
                                            (uint)(SetWindowPosFlags.IgnoreZOrder | SetWindowPosFlags.AsynchronousWindowPosition));

                                        _logger?.LogTrace("Restoring position of non maximized/minimized window: SetWindowPos({0}/{6} [{1}x{2}]-[{3}x{4}]) - {5}",
                                            window.Process.ProcessName,
                                            rect.Left,
                                            rect.Top,
                                            rect.Width,
                                            rect.Height,
                                            success, windowPlacement.ShowCmd.ToString());

                                        checkWin32Error(success);
                                    }

                                    _logger?.LogInformation($"Restore position for '{applicationKey}' completed (position changed).");
                                }
                                else
                                {
                                    _logger?.LogInformation($"Restore position for '{applicationKey}' completed (position change not needed).");
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning(ex, $"Restore position for '{applicationKey}' failed.");
                            }
                        }
                        else
                        {
                            _logger?.LogInformation($"Restore position for '{applicationKey}' ignored, previous location not found.");
                        }
                    }

                    _logger?.LogInformation($"Restore applications for desktop '{desktopKey}' completed.");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"Restore applications for desktop '{desktopKey}' failed.");
                    throw;
                }
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

        void IDisposable.Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
