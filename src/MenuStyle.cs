using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

// 主题配色（跟随系统深浅色自适应）：菜单与 OSD 共用一套配色单例
// 深色检测：HKCU\...\Themes\Personalize\AppsUseLightTheme（0=深色，缺省按浅色）
// 系统切换主题时经 WM_SETTINGCHANGE(ImmersiveColorSet) 调 RefreshTheme() 实时换肤
static class MenuStyle {
    public static bool IsDark { get; private set; }
    public static Color Bg, Border, Hover, Text, SubText;     // 菜单配色
    public static Color OsdBg, OsdBorder, OsdSub;             // OSD 卡片配色

    static MenuStyle() { RefreshTheme(); }

    public static void RefreshTheme() {
        IsDark = ReadSystemDark();
        if (IsDark) {
            Bg = Color.FromArgb(37, 37, 45);      Border = Color.FromArgb(60, 60, 74);
            Hover = Color.FromArgb(58, 58, 72);   Text = Color.FromArgb(232, 232, 240);
            SubText = Color.FromArgb(154, 154, 168);
            OsdBg = Color.FromArgb(28, 28, 36);   OsdBorder = Color.FromArgb(74, 74, 96);
            OsdSub = Color.FromArgb(178, 178, 196);
        } else {
            Bg = Color.FromArgb(249, 249, 251);   Border = Color.FromArgb(210, 210, 220);
            Hover = Color.FromArgb(230, 230, 240); Text = Color.FromArgb(28, 28, 40);
            SubText = Color.FromArgb(120, 120, 140);
            OsdBg = Color.FromArgb(248, 248, 251); OsdBorder = Color.FromArgb(205, 205, 218);
            OsdSub = Color.FromArgb(90, 90, 110);
        }
        ToolStripManager.Renderer = new ThemeRenderer();
    }

    static bool ReadSystemDark() {
        try {
            using (var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 1)) == 0;
        } catch { return false; }
    }

    public static void Apply(ContextMenuStrip menu) {
        menu.Font = new Font("Microsoft YaHei UI", 9.75f);
        menu.ShowImageMargin = false;
        menu.Padding = new Padding(4, 5, 4, 5);
    }

    class ThemeRenderer : ToolStripProfessionalRenderer {
        public ThemeRenderer() : base(new ThemeColors()) { RoundedEdges = true; }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e) {
            var rc = new Rectangle(Point.Empty, e.Item.Size);
            if (e.Item.Enabled && (e.Item.Selected || e.Item.Pressed)) {
                using (var b = new SolidBrush(Hover)) e.Graphics.FillRectangle(b, rc);
            } // 未选中/禁用项保持菜单底色，不画默认高亮
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) {
            e.TextColor = e.Item.Enabled ? Text : SubText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) {
            int y = e.Item.Height / 2;
            using (var p = new Pen(Border)) e.Graphics.DrawLine(p, 6, y, e.Item.Width - 6, y);
        }
    }

    class ThemeColors : ProfessionalColorTable {
        public override Color MenuBorder { get { return Border; } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color MenuItemSelected { get { return Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
        public override Color MenuItemPressedGradientBegin { get { return Hover; } }
        public override Color MenuItemPressedGradientEnd { get { return Hover; } }
        public override Color ToolStripDropDownBackground { get { return Bg; } }
        public override Color ImageMarginGradientBegin { get { return Bg; } }
        public override Color ImageMarginGradientMiddle { get { return Bg; } }
        public override Color ImageMarginGradientEnd { get { return Bg; } }
        public override Color SeparatorDark { get { return Border; } }
        public override Color SeparatorLight { get { return Border; } }
        public override Color CheckBackground { get { return Bg; } }
        public override Color CheckSelectedBackground { get { return Hover; } }
        public override Color ButtonSelectedHighlight { get { return Hover; } }
        public override Color ButtonSelectedBorder { get { return Border; } }
    }
}
