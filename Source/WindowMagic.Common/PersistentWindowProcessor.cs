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
        private const int DelayedCaptureTime = 3000;

        // read and update this from a config file eventually
        private readonly Dictionary<string, SortedDictionary<string, ApplicationDisplayMetrics>> monitorApplications = null;

        private object displayChangeLock = null;

        EventHandler displaySettingsChangingHandler;
        EventHandler displaySettingsChangedHandler;
        PowerModeChangedEventHandler powerModeChangedHandler;

        readonly List<IntPtr> winEventHookHandles = new List<IntPtr>();
        private readonly User32.WinEventDelegate winEventsCaptureDelegate;
        // private User32.WinEventDelegate winEventDebugDelegate;
        
        private bool isRestoring = false;

        private bool isCapturing = false;

        private bool ignoreCaptureRequests = false;

        // Sets to true if a capture request occurs while we're currently capturing
        private bool pendingCaptureRequest = false;

        private Timer ignoreCaptureTimer;
        private Timer delayedCaptureTimer;

        public PersistentWindowProcessor()
        {
            monitorApplications = new Dictionary<string, SortedDictionary<string, ApplicationDisplayMetrics>>();
            displayChangeLock = new object();
            this.CreateEventHandlers();
            this.winEventsCaptureDelegate = WinEventProc;
            // this.winEventDebugDelegate = WinEventProcDebug;

            this.ignoreCaptureTimer = new Timer(state =>
            {
                Log.Trace("ignoreCaptureTimer done. Re-enabling capture requests.");
                this.ignoreCaptureRequests = false;
            });

            this.delayedCaptureTimer = new Timer(state =>
            {
                this.CaptureApplicationsOnCurrentDisplays();
            });
        }

        public void Start()
        {
            CaptureApplicationsOnCurrentDisplays(initialCapture: true);

            Log.Info("Attaching event handlers");
            SystemEvents.DisplaySettingsChanging += this.displaySettingsChangingHandler;
            SystemEvents.DisplaySettingsChanged += this.displaySettingsChangedHandler;
            SystemEvents.PowerModeChanged += this.powerModeChangedHandler;

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

        /**
         * For manual invocation
         */
        public void CaptureLayout()
        {
            BeginCaptureApplicationsOnCurrentDisplays();
        }

        private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            Log.Trace($"Capture triggered from WinEvent with eventType {eventType:x8}");
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

        private void CreateEventHandlers()
        {
            this.displaySettingsChangingHandler = (s, e) =>
            {
                Log.Trace("Display settings changing handler invoked");
                BeginIgnoringCaptureRequests(5000); // Ignore capture requests for this period
                CancelDelayedCapture();
                
            };

            this.displaySettingsChangedHandler = (s, e) =>
            {
                Log.Trace("Display settings changed");
                BeginRestoreApplicationsOnCurrentDisplays();
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
                        BeginRestoreApplicationsOnCurrentDisplays();
                        break;

                    default:
                        Log.Trace("Unhandled power mode change: {0}", nameof(e.Mode));
                        break;
                }
            };
        }

        private void BeginIgnoringCaptureRequests(int timeoutMs)
        {
            Log.Trace("Ignoring capture requests...");
            this.ignoreCaptureRequests = true;
            this.ignoreCaptureTimer.Change(timeoutMs, Timeout.Infinite);
        }

        /**
         * Primary method to begin a capture. Calling this multiple times will defer the capture, effectively
         * preventing unnecessary processing. A "debouncing" technique.
         */
        private void RestartDelayedCapture()
        {
            Log.Trace("Beginning delayed capture...");
            this.delayedCaptureTimer.Change(DelayedCaptureTime, Timeout.Infinite);
        }

        /**
         * Under some circumstances (such as after display changes) we want to cancel any pending capture that
         * may have triggered. This is most beneficial after display change to "throw away" any captures
         * that were initiated by rogue events that happened before we are notified of the display settings change.
         */
        private void CancelDelayedCapture()
        {
            Log.Trace("Cancelling delayed capture");
            this.delayedCaptureTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        private void BeginCaptureApplicationsOnCurrentDisplays()
        {
            if (this.ignoreCaptureRequests)
            {
                Log.Trace("Ignoring capture request... 'ignoreCaptureRequests' flag set");
                return;
            }

            if (this.isRestoring)
            {
                Log.Trace("Currently restoring windows.... ignored capture request..");
                return;
            }

            if (this.isCapturing)
            {
                pendingCaptureRequest = true;
                return;
            }

            this.isCapturing = true;

            var thread = new Thread(() =>
            {
                CaptureApplicationsOnCurrentDisplays();
                this.isCapturing = false;

                if (this.pendingCaptureRequest)
                {
                    // If we had more changes during capture, this should be set to true, so we perform a follow-up capture.
                    this.pendingCaptureRequest = false;
                    this.BeginCaptureApplicationsOnCurrentDisplays();
                }
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
                var appWindows = CaptureWindowsOfInterest();
                foreach (var window in appWindows)
                {
                    ApplicationDisplayMetrics curDisplayMetrics = null;
                    if (NeedUpdateWindow(displayKey, window, out curDisplayMetrics))
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

        private IEnumerable<SystemWindow> CaptureWindowsOfInterest()
        {
            return SystemWindow.AllToplevelWindows
                                .Where(row => row.Parent.HWnd.ToInt64() == 0
                                    && !string.IsNullOrEmpty(row.Title)
                                    //&& !row.Title.Equals("Program Manager")
                                    //&& !row.Title.Contains("Task Manager")
                                    && row.Visible
                                    );
        }

        private bool NeedUpdateWindow(string displayKey, SystemWindow window, out ApplicationDisplayMetrics curDisplayMetrics)
        {
            WindowPlacement windowPlacement = new WindowPlacement();
            User32.GetWindowPlacement(window.HWnd, ref windowPlacement);

            // compensate for GetWindowPlacement() failure to get real coordinate of snapped window
            RECT screenPosition = new RECT();
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
                }
                else if (!prevDisplayMetrics.EqualPlacement(curDisplayMetrics))
                {
                    Log.Trace("Unexpected WindowPlacement.NormalPosition change if ScreenPosition keep same {0} {1} {2}",
                        window.Process.ProcessName, processId, window.HWnd.ToString("X8"));

                    string log = string.Format("prev WindowPlacement ({0}, {1}) of size {2} x {3}",
                        prevDisplayMetrics.WindowPlacement.NormalPosition.Left,
                        prevDisplayMetrics.WindowPlacement.NormalPosition.Top,
                        prevDisplayMetrics.WindowPlacement.NormalPosition.Width,
                        prevDisplayMetrics.WindowPlacement.NormalPosition.Height
                        );

                    string log2 = string.Format("\ncur  WindowPlacement ({0}, {1}) of size {2} x {3}",
                        curDisplayMetrics.WindowPlacement.NormalPosition.Left,
                        curDisplayMetrics.WindowPlacement.NormalPosition.Top,
                        curDisplayMetrics.WindowPlacement.NormalPosition.Width,
                        curDisplayMetrics.WindowPlacement.NormalPosition.Height
                        );
                    Log.Trace("{0}", log + log2);

                    if (monitorApplications[displayKey][curDisplayMetrics.Key].RecoverWindowPlacement)
                    {
                        // try recover previous placement first
                        WindowPlacement prevWP = prevDisplayMetrics.WindowPlacement;
                        User32.SetWindowPlacement(curDisplayMetrics.HWnd, ref prevWP);
                        RECT rect = prevDisplayMetrics.ScreenPosition;
                        User32.MoveWindow(curDisplayMetrics.HWnd, rect.Left, rect.Top, rect.Width, rect.Height, true);
                        monitorApplications[displayKey][curDisplayMetrics.Key].RecoverWindowPlacement = false;
                    }
                    else
                    {
                        Log.Trace("Fail to recover NormalPosition {0} {1} {2}",
                            window.Process.ProcessName, processId, window.HWnd.ToString("X8"));
                        // needUpdate = true;
                        // immediately update WindowPlacement with current value
                        monitorApplications[displayKey][curDisplayMetrics.Key].WindowPlacement = curDisplayMetrics.WindowPlacement;
                        monitorApplications[displayKey][curDisplayMetrics.Key].RecoverWindowPlacement = true;
                    }
                }
            }

            return needUpdate;
        }

        private void BeginRestoreApplicationsOnCurrentDisplays()
        {
            var thread = new Thread(() => 
            {
                if (this.isRestoring) return;
                this.isRestoring = true; // Prevent any accidental re-reading of layout while we attempt to restore layout

                Thread.Sleep(1000); // Must have time for built-in arrangement to settle.. wish there was a way to detect this..
                try
                {
                    lock (displayChangeLock)
                    {
                        RestoreApplicationsOnCurrentDisplays();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                }
                this.isRestoring = false;
                // Once restored, update our capture
                this.BeginCaptureApplicationsOnCurrentDisplays();

            });
            thread.IsBackground = true;
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
                foreach (var window in CaptureWindowsOfInterest())
                {
                    var proc_name = window.Process.ProcessName;
                    if (proc_name.Contains("CodeSetup"))
                    {
                        // prevent hang in SetWindowPlacement()
                        continue;
                    }

                    string applicationKey = ApplicationDisplayMetrics.GetKey(window.HWnd, window.Process.ProcessName);

                    if (monitorApplications[displayKey].ContainsKey(applicationKey))
                    {
                        ApplicationDisplayMetrics prevDisplayMetrics = monitorApplications[displayKey][applicationKey];
                        // looks like the window is still here for us to restore
                        WindowPlacement windowPlacement = prevDisplayMetrics.WindowPlacement;
                        IntPtr hwnd = prevDisplayMetrics.HWnd;

                        ApplicationDisplayMetrics curDisplayMetrics = null;
                        if (!NeedUpdateWindow(displayKey, window, out curDisplayMetrics))
                        {
                            // window position has no change
                            continue;
                        }

                        bool success;
                        // recover NormalPosition (the workspace position prior to snap)
                        success = User32.SetWindowPlacement(hwnd, ref windowPlacement);
                        Log.Info("SetWindowPlacement({0} [{1}x{2}]-[{3}x{4}]) - {5}",
                            window.Process.ProcessName,
                            windowPlacement.NormalPosition.Left,
                            windowPlacement.NormalPosition.Top,
                            windowPlacement.NormalPosition.Width,
                            windowPlacement.NormalPosition.Height,
                            success);

                        // recover current screen position
                        RECT rect = prevDisplayMetrics.ScreenPosition;
                        success |= User32.MoveWindow(hwnd, rect.Left, rect.Top, rect.Width, rect.Height, true);
                        Log.Info("MoveWindow({0} [{1}x{2}]-[{3}x{4}]) - {5}",
                            window.Process.ProcessName,
                            rect.Left,
                            rect.Top,
                            rect.Width,
                            rect.Height,
                            success);

                        if (!success)
                        {
                            string error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                            Log.Error(error);
                        }
                    }
                }
                Log.Trace("Restored windows position for display setting {0}", displayKey);
            }
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
