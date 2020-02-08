using System;
using System.Threading;
using System.Windows.Forms;
using WindowMagic.Common;

namespace WindowMagic.SystrayShell
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
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

            using (PersistentWindowProcessor pwp = new PersistentWindowProcessor())
            {
                pwp.Start();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (new SystrayForm(pwp))
                {
                    Application.Run();
                }
            }
        }
    }
}
