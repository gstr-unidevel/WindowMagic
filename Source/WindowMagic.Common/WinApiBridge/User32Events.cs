﻿namespace WindowMagic.Common.WinApiBridge
{
    /**
     * This is a subset of events from winuser.h.
     * See: https://docs.microsoft.com/en-us/windows/win32/winauto/event-constants
     */
    public enum User32Events : uint
    {
        EVENT_MIN = 0x00000001,
        EVENT_MAX = 0x7FFFFFFF,
        EVENT_SYSTEM_FOREGROUND = 0x0003,
        EVENT_SYSTEM_MENUSTART = 0x0004,
        EVENT_SYSTEM_MENUEND = 0x0005,
        EVENT_SYSTEM_MENUPOPUPSTART = 0x0006,
        EVENT_SYSTEM_MENUPOPUPEND = 0x0007,
        EVENT_SYSTEM_CAPTURESTART = 0x0008,
        EVENT_SYSTEM_CAPTUREEND = 0x0009,
        EVENT_SYSTEM_MOVESIZESTART = 0x000A,
        EVENT_SYSTEM_MOVESIZEEND = 0x000B,
        EVENT_SYSTEM_CONTEXTHELPSTART = 0x000C,
        EVENT_SYSTEM_CONTEXTHELPEND = 0x000D,
        EVENT_SYSTEM_DRAGDROPSTART = 0x000E,
        EVENT_SYSTEM_DRAGDROPEND = 0x000F,
        EVENT_SYSTEM_DIALOGSTART = 0x0010,
        EVENT_SYSTEM_DIALOGEND = 0x0011,
        EVENT_SYSTEM_SCROLLINGSTART = 0x0012,
        EVENT_SYSTEM_SCROLLINGEND = 0x0013,
        EVENT_SYSTEM_SWITCHSTART = 0x0014,
        EVENT_SYSTEM_SWITCHEND = 0x0015,
        EVENT_SYSTEM_MINIMIZESTART = 0x0016,
        EVENT_SYSTEM_MINIMIZEEND = 0x0017,
        EVENT_SYSTEM_DESKTOPSWITCH = 0x0020,
        EVENT_SYSTEM_SWITCHER_APPGRABBED = 0x0024,
        EVENT_SYSTEM_SWITCHER_APPOVERTARGET = 0x0025,
        EVENT_SYSTEM_SWITCHER_APPDROPPED = 0x0026,
        EVENT_SYSTEM_SWITCHER_CANCELLED = 0x0027,
        EVENT_SYSTEM_IME_KEY_NOTIFICATION = 0x0029,
        EVENT_SYSTEM_END = 0x00FF,

        WINEVENT_OUTOFCONTEXT = 0x0000,
        WINEVENT_SKIPOWNTHREAD = 0x0001,
        WINEVENT_SKIPOWNPROCESS = 0x0002,
        WINEVENT_INCONTEXT = 0x0004
    }
}
