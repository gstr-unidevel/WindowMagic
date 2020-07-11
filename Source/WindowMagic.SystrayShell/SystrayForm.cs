using System;
using System.Windows.Forms;
using WindowMagic.Common;

namespace WindowMagic.SystrayShell
{
    public partial class SystrayForm : Form
    {
        private readonly PersistentWindowProcessor pwp;

        public SystrayForm(PersistentWindowProcessor pwp)
        {
            this.pwp = pwp;
            InitializeComponent();
        }

        private void ExitToolStripMenuItemClickHandler(object sender, EventArgs e)
        {
            this.notifyIconMain.Visible = false;
            this.notifyIconMain.Icon = null;

            this.pwp.Stop();

            Application.Exit();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
        }
    }
}
