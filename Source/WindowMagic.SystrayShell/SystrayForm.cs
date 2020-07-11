using System;
using System.Windows.Forms;
using WindowMagic.Common;

namespace WindowMagic.SystrayShell
{
    public partial class SystrayForm : Form
    {
        private readonly PersistentWindowProcessor _pwp;

        public SystrayForm(PersistentWindowProcessor pwp)
        {
            this._pwp = pwp;
            InitializeComponent();
        }

        private void exitToolStripMenuItemClickHandler(object sender, EventArgs e)
        {
            this.notifyIconMain.Visible = false;
            this.notifyIconMain.Icon = null;

            this._pwp.Stop();

            Application.Exit();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
        }
    }
}
