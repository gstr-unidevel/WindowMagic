using System;
using System.Windows.Controls;
using WindowMagic.Common;
using WindowMagic.Common.Diagnostics;

namespace WindowMagic.WpfShell
{
    /// <summary>
    /// Interaction logic for DiagnosticsView.xaml
    /// </summary>
    public partial class DiagnosticsView : UserControl
    {
        private DiagnosticsViewModel DiagnosticsModel
        {
            get => this.DataContext as DiagnosticsViewModel;
        }

        public DiagnosticsView()
        {
            InitializeComponent();
            
            Log.LogEvent += (level, message) =>
                {
                    //if (level != LogLevel.Trace)
                    //{

                    this.Dispatcher.InvokeAsync(() =>
                    {
                        var eventLog = DiagnosticsModel.EventLog;
                        eventLog.Add(string.Format("{0}: [{1}]: {2}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), level, message));
                        
                        if (DiagnosticsModel.EventLog.Count > 500)
                        {
                            eventLog.RemoveAt(0);
                        }


                        eventLogList.SelectedIndex = eventLogList.Items.Count - 1;
                        eventLogList.ScrollIntoView(eventLogList.Items[eventLogList.Items.Count - 1]);

                    });
                    //}
            };
        }

        private void ClearButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            DiagnosticsModel.EventLog.Clear();
        }

        private void CaptureButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (DataContext is PersistentWindowProcessor pwp)
            {
                Dispatcher?.Invoke(() => pwp.CaptureLayout());
            }
        }
    }
}
