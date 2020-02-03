using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Ninjacrab.PersistentWindows.Common;

namespace Ninjacrab.PersistentWindows.SystrayShell
{
    static class Program
    {
        


        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
#if (!DEBUG)
            Mutex singleInstMutex = new Mutex(true, Application.ProductName);
            if (!singleInstMutex.WaitOne(TimeSpan.Zero, true))
            {
                MessageBox.Show($"Only one instance of {Application.ProductName} can be run!");
                return;
            }
            else
            {
                singleInstMutex.ReleaseMutex();
            }
#endif

           

            // Seriously - this makes people want to abandon tools like this... let's make it lean!
            // StartSplashForm();

            using (PersistentWindowProcessor pwp = new PersistentWindowProcessor())
            {
                pwp.Start();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (new SystrayForm())
                {
                    Application.Run();
                }
            }
        }
    }
}
