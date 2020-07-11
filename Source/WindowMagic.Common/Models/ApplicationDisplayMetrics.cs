using System;
using ManagedWinapi.Windows;
using WindowMagic.Common.WinApiBridge;

namespace WindowMagic.Common.Models
{
    public class ApplicationDisplayMetrics
    {
        public static string GetKey(IntPtr hWnd, string applicationName) => string.Format("{0}-{1}", hWnd.ToString("X8"), applicationName);

        public IntPtr HWnd { get; set; }
        public uint ProcessId { get; set; }
        public string ProcessName { get; set; }
        public RECT ScreenPosition { get; set; }
        public WindowPlacement WindowPlacement { get; set; }


        public string Key
        {
            get { return GetKey(HWnd, ProcessName); }
        }

        public bool EqualPlacement(ApplicationDisplayMetrics other)
        {
            return WindowPlacement.NormalPosition.Equals(other.WindowPlacement.NormalPosition) &&
                   WindowPlacement.ShowCmd == other.WindowPlacement.ShowCmd;
        }

        public override string ToString()
        {
            return string.Format("{0}.{1} {2}", ProcessId, HWnd.ToString("X8"), ProcessName);
        }
    }
}
