using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ManagedWinapi.Windows;
using WindowMagic.Common.WinApiBridge;

namespace WindowMagic.Common
{
    public interface IDisplayService
    {
        IEnumerable<Display> GetDisplays();
    }

    public class DisplayService : IDisplayService
    {
        public IEnumerable<Display> GetDisplays()
        {
            var displays = new List<Display>();

            User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                delegate (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
                {
                    var monitorInfo = new MonitorInfo();
                    monitorInfo.StructureSize = Marshal.SizeOf(monitorInfo);

                    if (User32.GetMonitorInfo(hMonitor, ref monitorInfo))
                    {
                        var display = new Display
                        {
                            ScreenWidth = monitorInfo.Monitor.Width,
                            ScreenHeight = monitorInfo.Monitor.Height,
                            Left = monitorInfo.Monitor.Left,
                            Top = monitorInfo.Monitor.Top,
                            Flags = monitorInfo.Flags,

                            //int pos = monitorInfo.DeviceName.LastIndexOf("\\") + 1;
                            //display.DeviceName = monitorInfo.DeviceName.Substring(pos, monitorInfo.DeviceName.Length - pos);
                            DeviceName = "Display"
                        };

                        displays.Add(display);
                    }

                    return true;
                }, IntPtr.Zero);

            return displays;
        }
    }

    public class Display
    {
        public int ScreenWidth { get; internal set; }
        public int ScreenHeight { get; internal set; }
        public int Left { get; internal set; }
        public int Top { get; internal set; }
        public uint Flags { get; internal set; }
        public String DeviceName { get; internal set; }
    }
}
