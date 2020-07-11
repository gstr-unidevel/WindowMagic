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
        private const int delayedCaptureTime = 3500;

        // read and update this from a config file eventually
        private readonly Dictionary<string, SortedDictionary<string, ApplicationDisplayMetrics>> monitorApplications = null;

        private readonly object displayChangeLock = new object();

        private EventHandler displaySettingsChangingHandler;
        private EventHandler displaySettingsChangedHandler;
        private PowerModeChangedEventHandler powerModeChangedHandler;
        private SessionSwitchEventHandler sessionSwitchEventHandler;

        private readonly List<IntPtr> winEventHookHandles = new List<IntPtr>();
        private readonly User32.WinEventDelegate winEventsCaptureDelegate;
        private readonly IStateDetector _stateDetector;
        private readonly ILogger<PersistentWindowProcessor> _logger;
        
        private bool isSessionLocked = false;
        private bool isRestoring = false;

        private bool isCapturing = false;

        /// <summary>
        /// Force ignoring capture requests. Typically done between first point where restore needed and when restore completes.
        /// </summary>
        private bool ignoreCaptureRequests = false;

        /// <summary>
        /// Sets to true if a capture request occurs while we're currently capturing
        /// </summary>
        private bool pendingCaptureRequest = false;

        private Timer ignoreCaptureTimer;
        private Timer delayedCaptureTimer;

        public PersistentWindowProcessor(IStateDetector stateDetector, ILogger<PersistentWindowProcessor> logger)
        {
            _stateDetector = stateDetector ?? throw new ArgumentNullException(nameof(stateDetector));
            _logger = logger;

            monitorApplications = new Dictionary<string, SortedDictionary<string, ApplicationDisplayMetrics>>();
            this.createEventHandlers();
            this.winEventsCaptureDelegate = winEventProc;

            this.delayedCaptureTimer = new Timer(state =>
            {
                _logger?.LogTrace("Delayed capture timer triggered");
                this.beginCaptureApplicationsOnCurrentDisplays();
            });
        }

        public void Start()
        {
            captureApplicationsOnCurrentDisplays(initialCapture: true);

            _logger?.LogInformation("Attaching event handlers");
            SystemEvents.DisplaySettingsChanging += this.displaySettingsChangingHandler;
            SystemEvents.DisplaySettingsChanged += this.displaySettingsChangedHandler;
            SystemEvents.PowerModeChanged += this.powerModeChangedHandler;
            SystemEvents.SessionSwitch += this.sessionSwitchEventHandler;

            // Movement or resizing of a window has finished
            winEventHookHandles.Add(User32.SetWinEventHook(
                (uint)User32Events.EVENT_SYSTEM_MOVESIZEEND,
                (uint)User32Events.EVENT_SYSTEM_MOVESIZEEND,
                IntPtr.Zero,
                this.winEventsCaptureDelegate,
                0,
                0,
                (uint)User32Events.WINEVENT_OUTOFCONTEXT));

            // This seems to cover most moves involving snaps and minimize / restore
            winEventHookHandles.Add(User32.SetWinEventHook(
                (uint)User32Events.EVENT_SYSTEM_FOREGROUND,
                (uint)User32Events.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                this.winEventsCaptureDelegate,
                0,
                0,
                (uint)User32Events.WINEVENT_OUTOFCONTEXT));

            // Any movements around clicking / dragging (in case it's missed by the other events)
            winEventHookHandles.Add(User32.SetWinEventHook(
                (uint)User32Events.EVENT_SYSTEM_CAPTUREEND,
                (uint)User32Events.EVENT_SYSTEM_CAPTUREEND,
                IntPtr.Zero,
                this.winEventsCaptureDelegate,
                0,
                0,
                (uint)User32Events.WINEVENT_OUTOFCONTEXT));
        }

        /// <summary>
        /// For manual invocation
        /// </summary>
        public void ForceCaptureLayout()
        {
            lock (this.displayChangeLock)
            {
                monitorApplications.Clear();
            }

            beginCaptureApplicationsOnCurrentDisplays();
        }

        /// <summary>
        /// Create event handlers needed to detect various state changes that affect window capture.
        /// This is done explicitly with assigned fields to allow proper disposal.According to the
        /// documentation, if not properly disposed, there will be leaks(although I'm skeptical for
        /// this use case, since they live the lifetime of the process).       
        /// </summary>
        private void createEventHandlers()
        {
            displaySettingsChangingHandler = (s, e) =>
            {
                _logger?.LogTrace("Display settings changing handler invoked");
                this.ignoreCaptureRequests = true;
                cancelDelayedCapture(); // Throw away any pending captures
            };

            displaySettingsChangedHandler = (s, e) =>
            {
                _logger?.LogTrace("Display settings changed");
                _stateDetector.WaitForWindowStabilization(() =>
                {
                    // CancelDelayedCapture(); // Throw away any pending captures
                    beginRestoreApplicationsOnCurrentDisplays();
                });
            };

            powerModeChangedHandler = (s, e) =>
            {
                switch (e.Mode)
                {
                    case PowerModes.Suspend:
                        _logger?.LogInformation("System Suspending");
                        break;

                    case PowerModes.Resume:
                        _logger?.LogInformation("System Resuming");
                        ignoreCaptureRequests = true;
                        cancelDelayedCapture(); // Throw away any pending captures
                        beginRestoreApplicationsOnCurrentDisplays();
                        break;

                    default:
                        _logger?.LogTrace("Unhandled power mode change: {0}", nameof(e.Mode));
                        break;
                }
            };

            sessionSwitchEventHandler = (sender, args) =>
            {
                if (args.Reason == SessionSwitchReason.SessionLock)
                {
                    _logger?.LogTrace("Session locked");
                    this.isSessionLocked = true;
                } 
                else if (args.Reason == SessionSwitchReason.SessionUnlock)
                {
                    _logger?.LogTrace("Session unlocked");
                    this.isSessionLocked = false;
                }
            };
        }

        private bool isCaptureAllowed()
        {
            return !(this.isSessionLocked || this.ignoreCaptureRequests || this.isRestoring);
        }

        private void winEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            _logger?.LogTrace($"Capture triggered from WinEvent with eventType {eventType:x8}");
            restartDelayedCapture();
        }

        /// <summary>
        /// Primary method to begin a capture. Calling this multiple times will defer the capture, effectively
        /// preventing unnecessary processing.A "debouncing" technique.
        /// </summary>
        private void restartDelayedCapture()
        {
            if (this.ignoreCaptureRequests)
            {
                _logger?.LogTrace("Can't restart delayed capture. Currently ignoring capture requests.");
                return;
            }

            _logger?.LogTrace("Delayed capture timer restarted");
            this.delayedCaptureTimer.Change(delayedCaptureTime, Timeout.Infinite);
        }

        /// <summary>
        /// Under some circumstances (such as after display changes) we want to cancel any pending capture that
        /// may have triggered.This is most beneficial after display change to "throw away" any captures
        /// that were initiated by rogue events that happened before we are notified of the display settings change.
        /// </summary>
        private void cancelDelayedCapture()
        {
            _logger?.LogTrace("Cancelling delayed capture if pending");
            this.delayedCaptureTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void beginCaptureApplicationsOnCurrentDisplays()
        {
            if (!this.isCaptureAllowed())
            {
                _logger?.LogTrace("Ignoring capture request... IsCaptureAllowed() returned false");
                return;
            }

            this.isCapturing = true;

            var thread = new Thread(() =>
            {
                _stateDetector.WaitForWindowStabilization(() =>
                {
                    captureApplicationsOnCurrentDisplays();
                    this.isCapturing = false;
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
            lock(displayChangeLock)
            {
                if (displayKey == null)
                {
                    DesktopDisplayMetrics metrics = DesktopDisplayMetrics.AcquireMetrics();
                    displayKey = metrics.Key;
                }

                if (!monitorApplications.ContainsKey(displayKey))
                {
                    monitorApplications.Add(displayKey, new SortedDictionary<string, ApplicationDisplayMetrics>());
                }

                List<string> updateLogs = new List<string>();
                List<ApplicationDisplayMetrics> updateApps = new List<ApplicationDisplayMetrics>();
                var appWindows =  WindowHelper.CaptureWindowsOfInterest();
                
                foreach (var window in appWindows)
                {
                    ApplicationDisplayMetrics curDisplayMetrics = null;
                    if (hasWindowChanged(displayKey, window, out curDisplayMetrics))
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
                    if (!monitorApplications[displayKey].ContainsKey(curDisplayMetrics.Key))
                    {
                        monitorApplications[displayKey].Add(curDisplayMetrics.Key, curDisplayMetrics);
                    }
                    else
                    {
                        /*
                        // partially update Normal position part of WindowPlacement
                        WindowPlacement wp = monitorApplications[displayKey][curDisplayMetrics.Key].WindowPlacement;
                        wp.NormalPosition = curDisplayMetrics.WindowPlacement.NormalPosition;
                        monitorApplications[displayKey][curDisplayMetrics.Key].WindowPlacement = wp;
                        */
                        monitorApplications[displayKey][curDisplayMetrics.Key].WindowPlacement = curDisplayMetrics.WindowPlacement;
                        monitorApplications[displayKey][curDisplayMetrics.Key].ScreenPosition = curDisplayMetrics.ScreenPosition;
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

            uint processId = 0;
            uint threadId = User32.GetWindowThreadProcessId(window.HWnd, out processId);

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
            if (!monitorApplications[displayKey].ContainsKey(curDisplayMetrics.Key))
            {
                needUpdate = true;
            }
            else
            {
                ApplicationDisplayMetrics prevDisplayMetrics = monitorApplications[displayKey][curDisplayMetrics.Key];
                if (prevDisplayMetrics.ProcessId != curDisplayMetrics.ProcessId)
                {
                    // key collision between dead window and new window with the same hwnd
                    monitorApplications[displayKey].Remove(curDisplayMetrics.Key);
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
            if (isRestoring) return;
            
            isRestoring = true; // Prevent any accidental re-reading of layout while we attempt to restore layout

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
                this.isRestoring = false;
                this.ignoreCaptureRequests = false; // Resume handling of capture requests
                
                //this.BeginCaptureApplicationsOnCurrentDisplays();

            });
            //thread.IsBackground = true;
            thread.Name = "PersistentWindowProcessor.RestoreApplicationsOnCurrentDisplays()";
            thread.Start();
        }

        private void restoreApplicationsOnCurrentDisplays(string displayKey = null)
        {
            lock (displayChangeLock)
            {
                if (displayKey == null)
                {
                    DesktopDisplayMetrics metrics = DesktopDisplayMetrics.AcquireMetrics();
                    displayKey = metrics.Key;
                }

                if (!monitorApplications.ContainsKey(displayKey)
                    || monitorApplications[displayKey].Count == 0)
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

                    var prevDisplayMetrics = monitorApplications[displayKey][applicationKey];
                    var windowPlacement = prevDisplayMetrics.WindowPlacement;

                    if (monitorApplications[displayKey].ContainsKey(applicationKey))
                    {
                        ApplicationDisplayMetrics curDisplayMetrics = null;
                        if (!hasWindowChanged(displayKey, window, out curDisplayMetrics))
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

        private bool isDisposed = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!isDisposed)
            {
                if (disposing)
                {
                    if (this.displaySettingsChangedHandler != null)
                    {
                        SystemEvents.DisplaySettingsChanged -= this.displaySettingsChangedHandler;
                    }
                    if (this.powerModeChangedHandler != null)
                    {
                        SystemEvents.PowerModeChanged -= this.powerModeChangedHandler;
                    }
                }

                foreach (var handle in this.winEventHookHandles)
                {
                    User32.UnhookWinEvent(handle);
                }

                isDisposed = true;
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
