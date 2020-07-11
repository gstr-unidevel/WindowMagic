using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ManagedWinapi.Windows;
using WindowMagic.Common.WinApiBridge;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace WindowMagic.Common
{
    public interface IDesktopService
    {
        string GetDesktopKey();
        IEnumerable<DesktopDisplay> GetDesktopDisplays();
    }

    public class DesktopService : IDesktopService
    {
        private readonly ILogger<DesktopService> _logger;

        public DesktopService(ILogger<DesktopService> logger)
        {
            _logger = logger;
        }

        public string GetDesktopKey()
        {
            var displayCodes = from d in GetDesktopDisplays()
                               orderby d.Left, d.Top
                               select $"{d.Left};{d.Top};{d.ScreenWidth};{d.ScreenHeight}";

            var desktopKey = String.Join("|", displayCodes);

            _logger?.LogDebug($"Current DesktopKey is '{desktopKey}'.");

            return desktopKey;
        }

        public IEnumerable<DesktopDisplay> GetDesktopDisplays()
        {
            var displays = new List<DesktopDisplay>();
            var order = 0;

            User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                delegate (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData)
                {
                    var monitorInfo = new MonitorInfo();
                    monitorInfo.StructureSize = Marshal.SizeOf(monitorInfo);

                    if (User32.GetMonitorInfo(hMonitor, ref monitorInfo))
                    {
                        var display = new DesktopDisplay
                        {
                            Order = order++,
                            ScreenWidth = monitorInfo.Monitor.Width,
                            ScreenHeight = monitorInfo.Monitor.Height,
                            Left = monitorInfo.Monitor.Left,
                            Top = monitorInfo.Monitor.Top,
                            Flags = monitorInfo.Flags,
                            DeviceName = monitorInfo.DeviceName
                        };

                        displays.Add(display);
                    }

                    return true;
                }, IntPtr.Zero);

            return displays;
        }
    }

    public class DesktopDisplay
    {
        public int Order { get; set; }
        public int ScreenWidth { get; set; }
        public int ScreenHeight { get; set; }
        public int Left { get; set; }
        public int Top { get; set; }
        public uint Flags { get; set; }
        public String DeviceName { get; set; }
    }
}
