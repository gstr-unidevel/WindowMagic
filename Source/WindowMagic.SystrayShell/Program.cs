using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        public static void Main(string[] args)
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .Build();

            IServiceCollection services = new ServiceCollection();
            services
                .AddSingleton<IConfiguration>(configuration)
                .AddLogging(options => 
                {
#if DEBUG
                    options.AddDebug(); 
                    options.SetMinimumLevel(LogLevel.Trace);
#endif
                })
                .AddSingleton<IStateDetector, StateDetector>()
                .AddSingleton<IDisplayService, DisplayService>()
                .AddSingleton<IDesktopDisplayMetricsService, DesktopDisplayMetricsService>()
                .AddSingleton<IWindowPositionService, WindowPositionService>()
                .AddSingleton<PersistentWindowProcessor>()
                ;

            using (var serviceProvider = services.BuildServiceProvider())
            {
                var pwp = serviceProvider.GetService<PersistentWindowProcessor>();
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
