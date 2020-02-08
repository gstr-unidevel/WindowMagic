using System.ComponentModel;
using Microsoft.Practices.Prism.Mvvm;
using NLog;

namespace WindowMagic.WpfShell
{
    public class DiagnosticsViewModel : BindableBase
    {
        public DiagnosticsViewModel()
        {
            EventLog = new BindingList<string>();
            MaxLogLevel = LogLevel.Debug;
        }

        public LogLevel MaxLogLevel { get; set; }


        public const string AllProcessesPropertyName = "AllProcesses";
        private BindingList<string> allProcesses;
        public BindingList<string> EventLog
        {
            get { return allProcesses; }
            set { SetProperty(ref allProcesses, value); } 
        }


    }
}
