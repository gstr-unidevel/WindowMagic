using System;
using System.Collections.Generic;
using System.Linq;
using ManagedWinapi.Windows;
using WindowMagic.Common.WinApiBridge;

namespace WindowMagic.Common
{
    static class WindowHelper
    {     
        public static IEnumerable<SystemWindow> CaptureWindowsOfInterest()
        {
            return SystemWindow.AllToplevelWindows
                .Where(row =>
                {
                    var success = DwmApi.DwmGetWindowAttribute(row.HWnd, (int) DwmApi.DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, out IntPtr result, sizeof(int));
                    
                    var resFlag = (DwmApi.DWM_WINDOW_ATTR_CLOAKED_REASON) result.ToInt32();
                    bool isCloaked = resFlag.HasFlag(DwmApi.DWM_WINDOW_ATTR_CLOAKED_REASON.DWM_CLOAKED_APP) 
                                     || resFlag.HasFlag(DwmApi.DWM_WINDOW_ATTR_CLOAKED_REASON.DWM_CLOAKED_INHERITED) 
                                     //|| resFlag.HasFlag(DwmApi.DWM_WINDOW_ATTR_CLOAKED_REASON.DWM_CLOAKED_SHELL) // otherwise windows on other virtual desktops are not restored
                                     ;
                    
                    return row.Parent.HWnd.ToInt64() == 0
                           && !string.IsNullOrEmpty(row.Title)
                           && !isCloaked
                           // && !row.Title.Equals("Program Manager")
                           //&& !row.Title.Contains("Task Manager")
                           && row.Visible;
                });
        }
    }
}
