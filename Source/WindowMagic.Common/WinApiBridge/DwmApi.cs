using System;
using System.Runtime.InteropServices;

namespace WindowMagic.Common.WinApiBridge
{
    class DwmApi
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out IntPtr pvAttribute, int cbAttribute);
	
        public enum DWMWINDOWATTRIBUTE: int
        {
            DWMWA_NCRENDERING_ENABLED = 1,
            DWMWA_NCRENDERING_POLICY,
            DWMWA_TRANSITIONS_FORCEDISABLED,
            DWMWA_ALLOW_NCPAINT,
            DWMWA_CAPTION_BUTTON_BOUNDS,
            DWMWA_NONCLIENT_RTL_LAYOUT,
            DWMWA_FORCE_ICONIC_REPRESENTATION,
            DWMWA_FLIP3D_POLICY,
            DWMWA_EXTENDED_FRAME_BOUNDS,
            DWMWA_HAS_ICONIC_BITMAP,
            DWMWA_DISALLOW_PEEK,
            DWMWA_EXCLUDED_FROM_PEEK,
            DWMWA_CLOAK,
            DWMWA_CLOAKED,
            DWMWA_FREEZE_REPRESENTATION,
            DWMWA_LAST             
        }

        [Flags]
        public enum DWM_WINDOW_ATTR_CLOAKED_REASON : int
        {
            DWM_CLOAKED_APP = 0x01,
            DWM_CLOAKED_SHELL = 0x02,
            DWM_CLOAKED_INHERITED = 0x04
        }
    }
}
