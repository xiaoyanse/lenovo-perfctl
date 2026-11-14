using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// 屏幕 OSD：UpdateLayeredWindow 逐像素 alpha 直绘——真透明底，文字浮于屏幕
// 无窗体底色（普通 Form 不画背景时图层缓冲是黑色，必须走 DIB 预乘 + ULW），
// 点击穿透（WS_EX_TRANSPARENT）、不抢焦点；淡入上浮 → 停留 → 淡出
static class Osd {
    static OsdForm cur;
    public static void Show(string title, string sub) {
        try {
            if (cur != null && !cur.IsDisposed) cur.Kill(); // 先停动画定时器再关窗，防 tick 访问已销毁句柄
            cur = new OsdForm(title, sub);
            cur.Show();
        } catch { }
    }

    class OsdForm : Form {
        const int W = 480, H = 116;
        const int FadeInMs = 200, HoldMs = 1400, FadeOutMs = 500; // 三段动画节拍
        Bitmap frame;                       // GDI+ 绘制层（非预乘 ARGB）
        IntPtr dib, memDc, oldObj;          // 预乘 DIB 与其 DC（ULW 数据源）
        readonly Timer timer;
        readonly int x0, y0;
        readonly DateTime born = DateTime.Now;

        public OsdForm(string title, string sub) {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Size = new Size(W, H);
            var wa = Screen.PrimaryScreen.WorkingArea;
            x0 = wa.Left + (wa.Width - W) / 2;
            y0 = wa.Bottom - H - (int)(wa.Height * 0.06); // 底部居中
            BuildFrame(title, sub);
            timer = new Timer { Interval = 15 };
            timer.Tick += (s, e) => Animate();
        }

        void BuildFrame(string t, string s) {
            frame = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(frame)) {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                var fmt = new StringFormat { Alignment = StringAlignment.Center };
                var rc1 = new RectangleF(0, 14, W, 58);
                var rc2 = new RectangleF(0, 78, W, 30);
                using (var f1 = new Font("Microsoft YaHei UI", 30, FontStyle.Bold))
                using (var f2 = new Font("Microsoft YaHei UI", 10.5f))
                using (var sh = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) { // 细投影保亮底可读
                    g.DrawString(t, f1, sh, new RectangleF(rc1.X + 2, rc1.Y + 2, W, 58), fmt);
                    g.DrawString(s, f2, sh, new RectangleF(rc2.X + 1, rc2.Y + 1, W, 30), fmt);
                    g.DrawString(t, f1, Brushes.White, rc1, fmt);
                    using (var w2 = new SolidBrush(Color.FromArgb(222, 222, 232)))
                        g.DrawString(s, f2, w2, rc2, fmt);
                }
            }
            // 建 top-down 32bpp DIB，把 GDI+ 像素预乘后拷入（ULW 要求 premultiplied alpha）
            var bi = new BITMAPINFOHEADER {
                biSize = 40, biWidth = (uint)W, biHeight = -H,
                biPlanes = 1, biBitCount = 32, biCompression = 0
            };
            IntPtr bits;
            dib = CreateDIBSection(IntPtr.Zero, ref bi, 0, out bits, IntPtr.Zero, 0);
            var src = frame.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            byte[] buf = new byte[W * H * 4];
            Marshal.Copy(src.Scan0, buf, 0, buf.Length);
            frame.UnlockBits(src);
            // 小端内存序 B,G,R,A；ULW 需要 premultiplied：rgb *= a/255
            byte[] pre = new byte[buf.Length];
            for (int i = 0; i < W * H; i++) {
                byte b = buf[i * 4], g = buf[i * 4 + 1], r = buf[i * 4 + 2], a = buf[i * 4 + 3];
                pre[i * 4] = (byte)(b * a / 255);
                pre[i * 4 + 1] = (byte)(g * a / 255);
                pre[i * 4 + 2] = (byte)(r * a / 255);
                pre[i * 4 + 3] = a;
            }
            Marshal.Copy(pre, 0, bits, pre.Length);
            frame.Dispose(); frame = null;
            memDc = CreateCompatibleDC(IntPtr.Zero);
            oldObj = SelectObject(memDc, dib);
        }

        void UpdateLayer(int alpha, int y) {
            if (IsDisposed || !IsHandleCreated) return; // 防御：销毁后不再触碰句柄
            var pt = new Point(x0, y);
            var sz = new Size(W, H);
            var srcPt = Point.Empty;
            var bf = new BLENDFUNCTION { BlendOp = 1, SourceConstantAlpha = (byte)alpha, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, IntPtr.Zero, ref pt, ref sz, memDc, ref srcPt, 0, ref bf, 2);
        }

        void Animate() {
            if (IsDisposed) { timer.Stop(); return; } // 窗体已销毁（被新 OSD 替换等），定时器自杀
            double t = (DateTime.Now - born).TotalMilliseconds;
            if (t < FadeInMs) {
                double k = t / FadeInMs, ease = 1 - (1 - k) * (1 - k);
                UpdateLayer((int)(255 * ease), y0 + (int)((1 - ease) * 16));
            } else if (t < FadeInMs + HoldMs) {
                UpdateLayer(255, y0);
            } else if (t < FadeInMs + HoldMs + FadeOutMs) {
                UpdateLayer((int)(255 * (1 - (t - FadeInMs - HoldMs) / FadeOutMs)), y0);
            } else { timer.Stop(); Close(); }
        }

        public void Kill() { timer.Stop(); if (!IsDisposed) Close(); }

        protected override void OnHandleCreated(EventArgs e) {
            base.OnHandleCreated(e);
            UpdateLayer(0, y0 + 16); // 首帧全透明，防止初始闪默认背景
        }
        protected override void OnShown(EventArgs e) { base.OnShown(e); timer.Start(); }
        protected override void OnHandleDestroyed(EventArgs e) {
            if (memDc != IntPtr.Zero) {
                SelectObject(memDc, oldObj);
                DeleteDC(memDc); memDc = IntPtr.Zero;
            }
            if (dib != IntPtr.Zero) { DeleteObject(dib); dib = IntPtr.Zero; }
            base.OnHandleDestroyed(e);
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams {
            get {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80000 | 0x20 | 0x8000000 | 0x80; // LAYERED|TRANSPARENT|NOACTIVATE|TOOLWINDOW
                return cp;
            }
        }

        [DllImport("user32.dll")]
        static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref Point pptDst,
            ref Size psize, IntPtr hdcSrc, ref Point pprSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
        [StructLayout(LayoutKind.Sequential)]
        struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage,
            out IntPtr bits, IntPtr hSection, uint offset);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER {
            public uint biSize, biWidth; public int biHeight;
            public ushort biPlanes, biBitCount;
            public uint biCompression, biSizeImage;
            public int biXPelsPerMeter, biYPelsPerMeter;
            public uint biClrUsed, biClrImportant;
        }
    }
}
