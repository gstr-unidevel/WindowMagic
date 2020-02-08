using System.Windows;
using WindowMagic.Common;

namespace WindowMagic.WpfShell
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public PersistentWindowProcessor Pwp { get; }
        public bool IsClosed { get; set; }
        public DiagnosticsViewModel DiagnosticsModel { get; }

        public MainWindow(PersistentWindowProcessor pwp)
        {
            this.Pwp = pwp;
            this.DiagnosticsModel = new DiagnosticsViewModel();

            InitializeComponent();
        }

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            IsClosed = true;
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            DiagnosticsModel.EventLog.Clear();
        }

        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            this.Pwp.CaptureLayout();
        }
    }
}
