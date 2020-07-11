using System;
using ManagedWinapi.Windows;
using WindowMagic.Common.WinApiBridge;

namespace WindowMagic.Common.Models
{
    public class ApplicationDisplayMetrics
    {
        public IntPtr HWnd { get; set; }
        public uint ProcessId { get; set; }
        public string ApplicationName { get; set; }
        public RECT ScreenPosition { get; set; }
        public WindowPlacement WindowPlacement { get; set; }
        // try recover sudden WindowPlacement change when ScreenPosition remains the same
        public bool RecoverWindowPlacement { get; set; }

        public static string GetKey(IntPtr hWnd, string applicationName)
        {
            return string.Format("{0}-{1}", hWnd.ToString("X8"), applicationName);
        }

        public string Key
        {
            get { return GetKey(HWnd, ApplicationName); }
        }

        public bool EqualPlacement(ApplicationDisplayMetrics other)
        {
            return this.WindowPlacement.NormalPosition.Equals(other.WindowPlacement.NormalPosition) &&
                   this.WindowPlacement.ShowCmd == other.WindowPlacement.ShowCmd;
        }

        public override string ToString()
        {
            return string.Format("{0}.{1} {2}", ProcessId, HWnd.ToString("X8"), ApplicationName);
        }
    }
}
