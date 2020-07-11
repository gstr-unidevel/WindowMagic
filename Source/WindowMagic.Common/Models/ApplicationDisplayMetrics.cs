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
            return $"{processToString()} {windowPlacementToString()} {screenPositionToString()}";
        }

        private string processToString() => $"{ProcessId}:{ProcessName}:{HWnd.ToString("X8")}";
        private string windowPlacementToString() => $"Window Placement showcmd [{WindowPlacement.ShowCmd.ToString()}] normal [{WindowPlacement.NormalPosition.Left}x{WindowPlacement.NormalPosition.Top} size {WindowPlacement.NormalPosition.Width}x{WindowPlacement.NormalPosition.Height}] minpos [{WindowPlacement.MinPosition.X}x{WindowPlacement.MinPosition.Y}] maxpos [{WindowPlacement.MaxPosition.X}x{WindowPlacement.MaxPosition.Y}] flags [{WindowPlacement.Flags.ToString()}]";
        private string screenPositionToString() => $"Screen Position [{ScreenPosition.Left}x{ScreenPosition.Top} size {ScreenPosition.Width}x{ScreenPosition.Height}]";
    }
}
