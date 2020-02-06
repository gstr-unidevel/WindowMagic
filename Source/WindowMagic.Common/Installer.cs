using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WindowMagic.Common
{
    [RunInstaller(true)]
    public partial class Installer : System.Configuration.Install.Installer
    {
        public Installer()
        {
            InitializeComponent();
        }

        public override void Install(IDictionary stateSaver)
        {
            // Debugger.Launch();
            // Add or remove in case this is a reinstall and the user changed their mind
            AddRemoveFromStartup(this.Context.Parameters["RUNATLOGIN"] == "1");
            
            base.Install(stateSaver);
        }

        public override void Commit(IDictionary savedState)
        {
            if (this.Context.Parameters["STARTWHENCOMPLETE"] == "1")
            {
                Process.Start(GetAssemblyPath());
            }

            base.Commit(savedState);
        }

        public override void Uninstall(IDictionary savedState)
        {
            if (this.Context.Parameters["RUNATLOGIN"] == "1")
            {
                AddRemoveFromStartup(false);
            }

            base.Uninstall(savedState);
        }

        private const string AppName = "WindowMagic";

        /**
         * Add or remove from users startup folder.
         */
        public static void AddRemoveFromStartup(bool addRemoveFlag)
        {
            if (addRemoveFlag)
            {
                Console.WriteLine($"{AppName} added to \"Run\" registry key for user");
                var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                key?.SetValue(AppName, GetAssemblyPath());
            }
            else
            {
                Console.WriteLine($"{AppName} removed from \"Run\" registry key for user");
                var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);
                key?.DeleteValue(AppName, false);
            }
        }

        private static string GetAssemblyPath()
        {
            return Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? throw new InvalidOperationException(), "WindowMagic.exe");
        }
    }
}
