# WindowMagic
The code is forked from https://github.com/kangyu-california/PersistentWindows which is forked from http://www.ninjacrab.com/persistent-windows/. I renamed to *"WindowMagic"* since it's such a major refactor.
Differences:
* Event-driven window position detection loop, rather than polling. Uses almost zero machine resources.
* No splash screen
* Installer with option to add to auto-run (and uninstalls cleanly!)

It seems to be a perfect solution to this unsolved Windows problem since Windows 7 era
https://answers.microsoft.com/en-us/windows/forum/windows_10-hardware/windows-10-multiple-display-windows-are-moved-and/2b9d5a18-45cc-4c50-b16e-fd95dbf27ff3?page=1&auth=1


# Original description
```
What is PersistentWindows?
A poorly named utility that persists window positions and size when the monitor display count/resolution adjusts 
and restores back to it’s previous settings.

For those of you with multi-monitors running on a mixture of DisplayPort and any other connection, you can run 
this tool and not have to worry about re-arranging when all is back to normal.

```
# Key features 
- Keeps track of windows layout, automatically restores last windows layout with matching monitor setup
- Manages different monitor setups automatically (dual monitor setup, single monitor setup etc.)
- Remote desktop session also benefits from running this software on target machine, whether monitor setup matches or not.
- Can automatically launch upon login for a user

