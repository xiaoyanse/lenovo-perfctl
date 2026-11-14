using System;
using System.Drawing;
using System.Windows.Forms;

// 屏幕上方居中的大字提示（不抢焦点，自动淡出，类似联想 Fn+Q 的 OSD）
static class Osd {
    static OsdForm cur;
    public static void Show(string text, Color c) {
        try {
            if (cur != null && !cur.IsDisposed) { cur.Close(); cur.Dispose(); }
            cur = new OsdForm(text, c);
            cur.Show();
        } catch { }
    }

    class OsdForm : Form {
        readonly System.Windows.Forms.Timer timer;
        readonly DateTime born = DateTime.Now;
        public OsdForm(string text, Color c) {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(24, 24, 28);
            Opacity = 0.92;
            Size = new Size(400, 86);
            var lbl = new Label {
                Text = text, Dock = DockStyle.Fill, ForeColor = c,
                Font = new Font("Microsoft YaHei UI", 24, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Add(lbl);
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + wa.Height / 10);
            timer = new System.Windows.Forms.Timer { Interval = 60 };
            timer.Tick += (s, e) => {
                double age = (DateTime.Now - born).TotalMilliseconds;
                if (age > 1300) Opacity -= 0.12;
                if (Opacity <= 0.02) { timer.Stop(); Close(); Dispose(); }
            };
            timer.Start();
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x8000000 | 0x80; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                return cp;
            }
        }
    }
}
