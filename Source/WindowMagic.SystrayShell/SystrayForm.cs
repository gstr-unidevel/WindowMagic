using System;
using System.Windows.Forms;
using WindowMagic.Common;
using WindowMagic.WpfShell;

namespace WindowMagic.SystrayShell
{
    public partial class SystrayForm : Form
    {
        private readonly PersistentWindowProcessor pwp;
        public MainWindow MainView { get; set; }

        public SystrayForm(PersistentWindowProcessor pwp)
        {
            this.pwp = pwp;
            InitializeComponent();
        }

        private void DiagnosticsToolStripMenuItemClickHandler(object sender, EventArgs e)
        {
            bool shouldShow = false;
            if (this.MainView == null ||
                this.MainView.IsClosed)
            {
                this.MainView = new MainWindow(pwp);
                shouldShow = true;
            }

            if (shouldShow)
            {
                this.MainView.Show();
            }
        }

        private void ExitToolStripMenuItemClickHandler(object sender, EventArgs e)
        {
            this.notifyIconMain.Visible = false;
            this.notifyIconMain.Icon = null;
            Application.Exit();
        }

    }
}
