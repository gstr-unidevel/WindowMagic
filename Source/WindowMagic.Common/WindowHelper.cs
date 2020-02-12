using System.Collections.Generic;
using System.Linq;
using ManagedWinapi.Windows;

namespace WindowMagic.Common
{
    static class WindowHelper
    {
        
        public static IEnumerable<SystemWindow> CaptureWindowsOfInterest()
        {
            return SystemWindow.AllToplevelWindows
                .Where(row => row.Parent.HWnd.ToInt64() == 0
                              && !string.IsNullOrEmpty(row.Title)
                              //&& !row.Title.Equals("Program Manager")
                              //&& !row.Title.Contains("Task Manager")
                              && row.Visible
                );
        }
    }
}
