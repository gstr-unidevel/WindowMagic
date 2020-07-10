using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using ManagedWinapi.Windows;
using Microsoft.Win32;
using WindowMagic.Common.Diagnostics;
using WindowMagic.Common.Models;
using WindowMagic.Common.WinApiBridge;

namespace WindowMagic.Common
{
    public class PersistentWindowProcessor : IDisposable
    {
        private const int DelayedCaptureTime = 3500;

        // read and update this from a config file eventually
        private readonly Dictionary<string, SortedDictionary<string, ApplicationDisplayMetrics>> monitorApplications = null;

        private readonly object displayChangeLock = new object();

        private EventHandler displaySettingsChangingHandler;
        private EventHandler displaySettingsChangedHandler;
        private PowerModeChangedEventHandler powerModeChangedHandler;
        private SessionSwitchEventHandler sessionSwitchEventHandler;

        private readonly List<IntPtr> winEventHookHandles = new List<IntPtr>();
        private readonly User32.WinEventDelegate winEventsCaptureDelegate;

        private bool isSessionLocked = false;
        private bool isRestoring = false;

        private bool isCapturing = false;

        // Force ignoring capture requests. Typically done between first point where restore needed and when restore completes.
        private bool ignoreCaptureRequests = false;

        // Sets to true if a capture request occurs while we're currently capturing
        private bool pendingCaptureRequest = false;

        private Timer ignoreCaptureTimer;
        private Timer delayedCaptureTimer;

        public PersistentWindowProcessor()
        {
            monitorApplications = new Dictionary<string, SortedDictionary<string, ApplicationDisplayMetrics>>();
            this.CreateEventHandlers();
            this.winEventsCaptureDelegate = WinEventProc;
            // this.winEventDebugDelegate = WinEventProcDebug;

            //this.ignoreCaptureTimer = new Timer(state =>
            //{
            //    Log.Trace("ignoreCaptureTimer done. Re-enabling capture requests.");
            //    this.ignoreCaptureRequests = false;
            //});

            this.delayedCaptureTimer = new Timer(state =>
            {
                Log.Trace("Delayed capture timer triggered");
                this.BeginCaptureApplicationsOnCurrentDisplays();
            });
        }

        /**
         * Create event handlers needed to detect various state changes that affect window capture.
         * This is done explicitly with assigned fields to allow proper disposal. According to the
         * documentation, if not properly disposed, there will be leaks (although I'm skeptical for
         * this use case, since they live the lifetime of the process).
         */
        private void CreateEventHandlers()
        {
            this.displaySettingsChangingHandler = (s, e) =>
            {
                Log.Trace("Display settings changing handler invoked");
                this.ignoreCaptureRequests = true;
                CancelDelayedCapture(); // Throw away any pending captures
            };

            this.displaySettingsChangedHandler = (s, e) =>
            {
                Log.Trace("Display settings changed");
                StateDetector.WaitForWindowStabilization(() =>
                {
                    // CancelDelayedCapture(); // Throw away any pending captures
                    BeginRestoreApplicationsOnCurrentDisplays();
                });
            };

            this.powerModeChangedHandler = (s, e) =>
            {
                switch (e.Mode)
                {
                    case PowerModes.Suspend:
                        Log.Info("System Suspending");
                        break;

                    case PowerModes.Resume:
                        Log.Info("System Resuming");
                        this.ignoreCaptureRequests = true;
                        CancelDelayedCapture(); // Throw away any pending captures
                        BeginRestoreApplicationsOnCurrentDisplays();
                        break;

                    default:
                        Log.Trace("Unhandled power mode change: {0}", nameof(e.Mode));
                        break;
                }
            };

            this.sessionSwitchEventHandler = (sender, args) =>
            {
                if (args.Reason == SessionSwitchReason.SessionLock)
                {
                    Log.Trace("Session locked");
                    this.isSessionLocked = true;
                } 
                else if (args.Reason == SessionSwitchReason.SessionUnlock)
                {
                    Log.Trace("Session unlocked");
                    this.isSessionLocked = false;
                }
            };
        }

        public void Start()
        {
            CaptureApplicationsOnCurrentDisplays(initialCapture: true);

            Log.Info("Attaching event handlers");
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


            //this.winEventsHookCaptureEnd = User32.SetWinEventHook(
            //    (uint)User32Events.EVENT_MIN,
            //    (uint)User32Events.EVENT_MAX,
            //    IntPtr.Zero,
            //    this.winEventDebugDelegate,
            //    0,
            //    0,
            //    (uint)User32Events.WINEVENT_OUTOFCONTEXT);
        }

        private bool IsCaptureAllowed()
        {
            return !(this.isSessionLocked || this.ignoreCaptureRequests || this.isRestoring);
        }

        /**
         * For manual invocation
         */
        public void ForceCaptureLayout()
        {
            lock (this.displayChangeLock)
            {
                monitorApplications.Clear();
            }
            
            BeginCaptureApplicationsOnCurrentDisplays();
        }

        private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            // Log.Trace($"Capture triggered from WinEvent with eventType {eventType:x8}");
            RestartDelayedCapture();
        }

        //private void WinEventProcDebug(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild,
        //    uint dwEventThread, uint dwmsEventTime)
        //{
        //    if (eventType == 0x0000800b || // EVENT_OBJECT_LOCATIONCHANGE
        //        eventType == 0x0000800c || // EVENT_OBJECT_NAMECHANGE
        //        eventType == 0x00008004 || // EVENT_OBJECT_REORDER
        //        eventType == 0x0000800e ||
        //        eventType >= 0x7500 && eventType <= 0x75FF) return;

        //    Console.WriteLine("WinEvent received. Type: {0:x8}, Window: {1:x8}", eventType, hwnd.ToInt32());
        //}

        //private void IgnoreCaptureUntilWindowsStable()
        //{
        //    Log.Trace("Ignoring captures until windows stabilize...");
        //    this.ignoreCaptureRequests = true;
        //    StateDetector.WaitForWindowStabilization(() =>
        //    {
        //        Log.Trace("Windows stabilized. Resuming allow capture of window positions.");
        //        this.ignoreCaptureRequests = false;
        //    });
        //}

        /**
         * Primary method to begin a capture. Calling this multiple times will defer the capture, effectively
         * preventing unnecessary processing. A "debouncing" technique.
         */
        private void RestartDelayedCapture()
        {
            if (this.ignoreCaptureRequests)
            {
                Log.Trace("Can't restart delayed capture. Currently ignoring capture requests.");
                return;
            }

            // Log.Trace("Delayed capture timer restarted");
            this.delayedCaptureTimer.Change(DelayedCaptureTime, Timeout.Infinite);
        }

        /**
         * Under some circumstances (such as after display changes) we want to cancel any pending capture that
         * may have triggered. This is most beneficial after display change to "throw away" any captures
         * that were initiated by rogue events that happened before we are notified of the display settings change.
         */
        private void CancelDelayedCapture()
        {
            Log.Trace("Cancelling delayed capture if pending");
            this.delayedCaptureTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void BeginCaptureApplicationsOnCurrentDisplays()
        {
            if (!this.IsCaptureAllowed())
            {
                Log.Trace("Ignoring capture request... IsCaptureAllowed() returned false");
                return;
            }

            this.isCapturing = true;

            var thread = new Thread(() =>
            {
                StateDetector.WaitForWindowStabilization(() =>
                {
                    CaptureApplicationsOnCurrentDisplays();
                    this.isCapturing = false;

                    //if (this.pendingCaptureRequest)
                    //{
                    //    // If we had more changes during capture, this should be set to true, so we perform a follow-up capture.
                    //    this.pendingCaptureRequest = false;
                    //    this.BeginCaptureApplicationsOnCurrentDisplays();
                    //}
                });
            })
            {
                IsBackground = true,
                Name = "PersistentWindowProcessor.BeginCaptureApplicationsOnCurrentDisplays()"
            };
            thread.Start();

        }

        private void CaptureApplicationsOnCurrentDisplays(string displayKey = null, bool initialCapture = false)
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
                    if (HasWindowChanged(displayKey, window, out curDisplayMetrics))
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

                Log.Trace("{0}Capturing windows for display setting {1}", initialCapture ? "Initial " : "", displayKey);

                List<string> commitUpdateLog = new List<string>();
                //for (int i = 0; i < maxUpdateCnt; i++)
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
                Log.Trace("{0}{1}{2} windows captured", string.Join(Environment.NewLine, commitUpdateLog), Environment.NewLine, commitUpdateLog.Count);
            }
        }

        private bool HasWindowChanged(string displayKey, SystemWindow window, out ApplicationDisplayMetrics curDisplayMetrics)
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

                    Log.Trace("Window position changed for: {0} {1} {2}.",
                        window.Process.ProcessName, processId, window.HWnd.ToString("X8"));
                }
                else if (!prevDisplayMetrics.EqualPlacement(curDisplayMetrics))
                {
                    needUpdate = true;

                    Log.Trace("Window placement changed for: {0} {1} {2}.",
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
                    //Log.Trace("{0}", log + log2);
                }
            }

            return needUpdate;
        }

        private void BeginRestoreApplicationsOnCurrentDisplays()
        {
            if (this.isRestoring) return;
            this.isRestoring = true; // Prevent any accidental re-reading of layout while we attempt to restore layout

            var thread = new Thread(() => 
            {
                try
                {
                    StateDetector.WaitForWindowStabilization(() =>
                    {
                        RestoreApplicationsOnCurrentDisplays();
                    });
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                }
                this.isRestoring = false;
                this.ignoreCaptureRequests = false; // Resume handling of capture requests
                
                //this.BeginCaptureApplicationsOnCurrentDisplays();

            });
            //thread.IsBackground = true;
            thread.Name = "PersistentWindowProcessor.RestoreApplicationsOnCurrentDisplays()";
            thread.Start();
        }

        private void RestoreApplicationsOnCurrentDisplays(string displayKey = null)
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
                    Log.Trace("Unknown display setting {0}", displayKey);
                    return;
                }

                Log.Info("Restoring applications for {0}", displayKey);
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
                        if (!HasWindowChanged(displayKey, window, out curDisplayMetrics))
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
                            success = CheckWin32Error(User32.SetWindowPlacement(window.HWnd, ref windowPlacement));
                            windowPlacement.ShowCmd = prevCmd;

                            Log.Trace("Toggling to normal window state for: ({0} [{1}x{2}]-[{3}x{4}]) - {5}",
                                window.Process.ProcessName,
                                windowPlacement.NormalPosition.Left,
                                windowPlacement.NormalPosition.Top,
                                windowPlacement.NormalPosition.Width,
                                windowPlacement.NormalPosition.Height,
                                success);
                        }

                        // Set final window placement data - sets "normal" position for all windows (used for de-snapping and screen ID'ing)
                        success = CheckWin32Error(User32.SetWindowPlacement(window.HWnd, ref windowPlacement));

                        Log.Trace("SetWindowPlacement({0} [{1}x{2}]-[{3}x{4}]) - {5}",
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

                            Log.Trace("Restoring position of non maximized/minimized window: SetWindowPos({0} [{1}x{2}]-[{3}x{4}]) - {5}",
                                window.Process.ProcessName,
                                rect.Left,
                                rect.Top,
                                rect.Width,
                                rect.Height,
                                success);
                            CheckWin32Error(success);
                        }
                    }
                }
                Log.Trace("Restored windows position for display setting {0}", displayKey);
            }
        }

        private static bool CheckWin32Error(bool success)
        {
            if (!success)
            {
                string error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                Log.Error(error);
            }

            return success;
        }

        #region IDisposable
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
        #endregion

    }

}
