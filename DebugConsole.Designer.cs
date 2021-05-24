
namespace LiveSplit.SourceSplit
{
    partial class DebugConsole
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.boxConsole = new System.Windows.Forms.TextBox();
            this.btnDump = new System.Windows.Forms.Button();
            this.btnClear = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // boxConsole
            // 
            this.boxConsole.Location = new System.Drawing.Point(13, 13);
            this.boxConsole.Multiline = true;
            this.boxConsole.Name = "boxConsole";
            this.boxConsole.ReadOnly = true;
            this.boxConsole.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.boxConsole.Size = new System.Drawing.Size(466, 233);
            this.boxConsole.TabIndex = 0;
            // 
            // btnDump
            // 
            this.btnDump.Location = new System.Drawing.Point(395, 253);
            this.btnDump.Name = "btnDump";
            this.btnDump.Size = new System.Drawing.Size(83, 23);
            this.btnDump.TabIndex = 1;
            this.btnDump.Text = "Dump to Fille";
            this.btnDump.UseVisualStyleBackColor = true;
            this.btnDump.Click += new System.EventHandler(this.btnDump_Click);
            // 
            // btnClear
            // 
            this.btnClear.Location = new System.Drawing.Point(314, 253);
            this.btnClear.Name = "btnClear";
            this.btnClear.Size = new System.Drawing.Size(75, 23);
            this.btnClear.TabIndex = 2;
            this.btnClear.Text = "Clear";
            this.btnClear.UseVisualStyleBackColor = true;
            this.btnClear.Click += new System.EventHandler(this.btnClear_Click);
            // 
            // DebugConsole
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(491, 286);
            this.Controls.Add(this.btnClear);
            this.Controls.Add(this.btnDump);
            this.Controls.Add(this.boxConsole);
            this.Name = "DebugConsole";
            this.Text = "Debug Console";
            this.FormClosing += new System.Windows.Forms.FormClosingEventHandler(this.Closing);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.TextBox boxConsole;
        private System.Windows.Forms.Button btnDump;
        private System.Windows.Forms.Button btnClear;
    }
}