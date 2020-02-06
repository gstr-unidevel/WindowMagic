using System;

namespace WindowMagic.Common.Models
{
    public class WindowsPositionInfo
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public int Flags;
    }
}
