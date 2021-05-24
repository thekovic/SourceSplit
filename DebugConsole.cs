using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LiveSplit.SourceSplit
{
    public partial class DebugConsole : Form
    {
        public DebugConsole()
        {
            InitializeComponent();
        }

        public void AddText(string text)
        {
            boxConsole.AppendText(text + "\r\n");
        }

        private void Closing(object sender, FormClosingEventArgs e)
        {
            this.Hide();
            e.Cancel = true;
        }

        private void btnDump_Click(object sender, EventArgs e)
        {
            string[] info = new string[] { boxConsole.Text };
            File.WriteAllLines("log.txt", info);
        }

        private void btnClear_Click(object sender, EventArgs e)
        {
            boxConsole.Clear();
        }
    }
}
