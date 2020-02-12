using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WindowMagic.Common.WinApiBridge
{
    /**
     * See: https://docs.microsoft.com/en-us/windows/win32/api/winuser/
     * And: https://docs.microsoft.com/en-us/windows/win32/menurc/wm-syscommand
     */
    public static class WinUserConstants
    {
        public const int WM_SYSCOMMAND = 0x0112;

        public static IntPtr SCF_ISSECURE = new IntPtr(0x01);
        public static IntPtr SC_MAXIMIZE = new IntPtr(0xF030);
        public static IntPtr SC_MINIMIZE = new IntPtr(0xF020);
        public static IntPtr SC_MONITORPOWER = new IntPtr(0xF170);
        public static IntPtr SC_MOVE = new IntPtr(0xF010);
        public static IntPtr SC_SIZE = new IntPtr(0xF000);
        public static IntPtr SC_RESTORE = new IntPtr(0xF120);
        //public static IntPtr SC_SCREENSAVE = new IntPtr(0xF140);
        public static int SC_SCREENSAVE = 0xF140;
    }
}
