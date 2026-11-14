using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// 联想性能模式独立控制工具（ThinkBook G7+ / ITS 智能散热）
// 双形态：无参数=托盘常驻；带参数=命令行。
// 实现（逆向 LNVDispatcherService 控制处理器 FUN_14005xxxx 完整 case 表）：
//   ① 注册表写 ITS_CurrentSettingV = 目标档（Vantage 同款先写法）
//   ② ControlService(LenovoProcessManagement, 控制码) 让调度器完整应用：
//      0xA3=省电(ITS 0+风扇0x13) 0xA4=智能(ITS 1+0x11) 0xA5=性能(ITS 3+0x15)
//      0xAC=极客(置极客位+强制ITS3+场景0x40) 0xAD=极客自定义位 互切自动清极客位
//   ③ 控制码被拒时兜底：WMI 固件直写 + 注册表 Cur（极客位无法兜底）
// 模式: 1=智能 2=省电 3=性能 4=极客 5=极客自定义
// 调度模式(SchedMode): 手动/联想AI/温度自适应 互斥单选；手动切档(菜单/热键/Fn+Q)=接管回手动
// 快捷键: Ctrl+Alt+1/2/3/4 直切 ｜ Ctrl+Alt+Q 循环
// 开机自启 = 计划任务（登录时、最高权限，避免 Run 键无提权失效）
// 极简 key=value 配置（exe 旁 LenovoPerfCtl.json）
static class Config {
    static System.Collections.Generic.Dictionary<string, string> map =
        new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
    static string path;
    public static void Load(string p) {
        path = p;
        try {
            foreach (string line in System.IO.File.ReadAllLines(p)) {
                int eq = line.IndexOf('=');
                if (eq > 0) map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
        } catch { }
    }
    public static string Get(string k) { return map.ContainsKey(k) ? map[k] : null; }
    public static void Set(string k, string v) {
        map[k] = v;
        try {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in map) sb.Append(kv.Key).Append('=').Append(kv.Value).AppendLine();
            System.IO.File.WriteAllText(path, sb.ToString());
        } catch { }
    }
}

static class LenovoPerfCtl {
    static readonly string[] ModeNames = { "无", "智能", "省电", "性能", "极客", "极客自定义" };
    static readonly uint[] Cycle = { 1, 2, 3, 4 };
    static readonly Color[] ModeColor = {
        Color.Gray, Color.DodgerBlue, Color.SeaGreen, Color.DarkOrange, Color.Red, Color.Firebrick
    };

    const string RegPath = @"SYSTEM\CurrentControlSet\Services\LenovoProcessManagement\Performance\PowerSlider";
    const string ServiceName = "LenovoProcessManagement";
    const string TaskName = "LenovoPerfCtl";
    const uint CtrlCool = 0xA3, CtrlAuto = 0xA4, CtrlHighPerf = 0xA5;
    const uint CtrlGeek = 0xAC, CtrlCustomGeek = 0xAD;

    static uint FanCode(uint mode) {
        return (mode == 0 || mode == 2) ? 0x13u : (mode == 1 ? 0x11u : 0x15u);
    }
    // 调度器真实码表（逆向实证：case 值 = 控制码-1）
    static uint CtrlCode(uint mode) {
        if (mode == 1) return CtrlAuto;
        if (mode == 3) return CtrlHighPerf;
        if (mode == 4 || mode == 5) return CtrlGeek;
        return CtrlCool; // 0 关闭 与 2 省电 固件层同为 0x13 档
    }
    static string Name(uint m) { return m < ModeNames.Length ? ModeNames[m] : ("未知" + m); }

    // ---------- 状态读取 ----------
    static uint Read(string name) {
        using (var k = Registry.LocalMachine.OpenSubKey(RegPath))
            return k == null ? 0 : Convert.ToUInt32(k.GetValue(name, 0));
    }
    static uint CurrentMode() { return Read("ITS_CurrentSetting"); }

    // ---------- 模式应用（三层） ----------
    static bool SendCtrl(uint code) {
        IntPtr scm = OpenSCManagerW(null, null, 1);
        if (scm == IntPtr.Zero) return false;
        IntPtr svc = OpenServiceW(scm, ServiceName, 0x104);
        bool ok = false;
        if (svc != IntPtr.Zero) {
            var st = new ServiceStatus();
            ok = ControlService(svc, code, ref st);
            CloseServiceHandle(svc);
        }
        CloseServiceHandle(scm);
        return ok;
    }

    static string ApplyMode(uint mode) {
        if (mode > 5) return "无效模式";
        using (var k = Registry.LocalMachine.CreateSubKey(RegPath)) {
            k.SetValue("ITS_CurrentSettingV", mode, RegistryValueKind.DWord);
        }
        uint code = CtrlCode(mode);
        bool ctrlOk = SendCtrl(code);
        if (mode == 5 && ctrlOk) SendCtrl(CtrlCustomGeek); // 极客自定义补发 bit3
        if (ctrlOk) return "调度器0x" + code.ToString("X") + "√";
        // 兜底：固件直写 + Cur 直写（极客位只能由 0xAC 走调度器，无法兜底）
        string wmi = FirmwareSetFan(FanCode(mode));
        using (var k = Registry.LocalMachine.CreateSubKey(RegPath)) {
            uint cur = mode == 2 ? 0u : (mode >= 4 ? 3u : mode);
            k.SetValue("ITS_CurrentSetting", cur, RegistryValueKind.DWord);
        }
        return "（控制码被拒，固件直写" + wmi + "）";
    }

    // 切档后的生效校验（Cur=固件实际档，V=目标档，VSet=ITS_SE_VantageSetting 位标志）
    static bool Verify(uint mode) {
        uint cur = Read("ITS_CurrentSetting");
        uint v = Read("ITS_CurrentSettingV");
        uint vs = Read("ITS_SE_VantageSetting");
        if (mode == 4) return (vs & 4) != 0 && cur == 3;
        if (mode == 5) return (vs & 8) != 0;
        if (mode == 2) return cur == 0 && (v == 2 || v == 0); // 调度器会把 V 归一成 0
        if (mode == 1) return cur == 1 && (vs & 4) == 0;
        if (mode == 3) return cur == 3 && (vs & 4) == 0;
        return cur == 0; // mode 0 关闭
    }

    static string Signature() {
        return string.Format("Cur={0} V={1} Fan={2} VSet=0x{3:x} Scen=0x{4:x}",
            Read("ITS_CurrentSetting"), Read("ITS_CurrentSettingV"),
            Read("ITS_FanMode"), Read("ITS_SE_VantageSetting"), Read("ITS_SE_CurrentScenario"));
    }

    // 托盘显示档：极客位 > 自定义位 > V 目标档 > Cur（ITS-0 在托盘语境即省电）
    public static uint DisplayMode() {
        uint vs = Read("ITS_SE_VantageSetting");
        if ((vs & 4) != 0) return 4;
        if ((vs & 8) != 0) return 5;
        uint v = Read("ITS_CurrentSettingV");
        if (v >= 1 && v <= 3) return v;
        uint cur = Read("ITS_CurrentSetting");
        return cur == 3 ? 3u : (cur == 1 ? 1u : 2u);
    }

    // AI 自动切换（dispatcher ITS_SE_VantageSetting bit1：0xAA 开 / 0xAB 关）
    internal static bool AiAutoOn() { return (Read("ITS_SE_VantageSetting") & 2) != 0; }
    internal static bool SetAiAuto(bool on) { return SendCtrl(on ? 0xAAu : 0xABu); }

    // WMI 固件直达（dispatcher 同款 moniker 姿势）
    static string FirmwareSetFan(uint code) {
        try {
            dynamic svc = Marshal.BindToMoniker("winmgmts:{impersonationLevel=impersonate}!\\\\.\\root\\wmi");
            dynamic cls = svc.Get("LENOVO_GAMEZONE_DATA");
            dynamic inp = cls.Methods_.Item("SetSmartFanMode").InParameters.SpawnInstance_();
            inp.Properties_.Item("Data").Value = code;
            dynamic inst = svc.Get("LENOVO_GAMEZONE_DATA.InstanceName=\"ACPI\\\\PNP0C14\\\\GMZN_0\"");
            inst.ExecMethod_("SetSmartFanMode", inp);
            return "√";
        } catch (Exception ex) {
            string m = ex.Message.Trim();
            if (ex.InnerException != null) m = ex.InnerException.Message.Trim();
            return "×(" + m.Substring(0, Math.Min(40, m.Length)) + ")";
        }
    }

    // ---------- 权限与自启 ----------
    static bool IsAdmin() {
        using (var id = WindowsIdentity.GetCurrent())
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    static void RelaunchElevated() {
        var psi = new ProcessStartInfo {
            FileName = Application.ExecutablePath,
            UseShellExecute = true,
            Verb = "runas"
        };
        try { Process.Start(psi); } catch { /* UAC 被取消 */ }
    }

    static bool AutoStartEnabled() {
        var pi = new ProcessStartInfo("schtasks", "/query /tn " + TaskName) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true
        };
        try { var p = Process.Start(pi); p.WaitForExit(); return p.ExitCode == 0; } catch { return false; }
    }

    // 自启+保活 = 双触发器计划任务（登录拉起 + 每5分钟重复拉起，单实例锁保证重复触发无害）
    static bool SetAutoStart(bool enable) {
        string ps = enable
            ? "$a=New-ScheduledTaskAction -Execute '" + Application.ExecutablePath + "';" +
              "$t1=New-ScheduledTaskTrigger -AtLogOn;" +
              "$t2=New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1)" +
              " -RepetitionInterval (New-TimeSpan -Minutes 5) -RepetitionDuration (New-TimeSpan -Days 3650);" +
              "$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries" +
              " -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Seconds 0);" +
              "Register-ScheduledTask -TaskName " + TaskName +
              " -Action $a -Trigger $t1,$t2 -Settings $s -RunLevel Highest -Force|Out-Null;exit $?"
            : "Unregister-ScheduledTask -TaskName " + TaskName + " -Confirm:$false -ErrorAction SilentlyContinue;exit 0";
        var pi = new ProcessStartInfo("powershell", "-NoProfile -Command \"" + ps + "\"") {
            UseShellExecute = false, CreateNoWindow = true
        };
        try { var p = Process.Start(pi); p.WaitForExit(); return p.ExitCode == 0; } catch { return false; }
    }

    // ---------- Win32 ----------
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr OpenSCManagerW(string m, string d, uint a);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr OpenServiceW(IntPtr s, string n, uint a);
    [DllImport("advapi32.dll")]
    static extern bool ControlService(IntPtr h, uint c, ref ServiceStatus st);
    [DllImport("advapi32.dll")]
    static extern bool CloseServiceHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatus {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode;
        public uint ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(uint pid);
    const uint MOD_ALT = 1, MOD_CONTROL = 2;
    const int WM_HOTKEY = 0x0312;

    // ---------- 日志与 OSD ----------
    internal static void Log(string s) {
        try {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(Application.StartupPath, "LenovoPerfCtl.log"),
                DateTime.Now.ToString("MM-dd HH:mm:ss.fff ") + s + "\r\n");
        } catch { }
    }

    // 屏幕 OSD 提示已拆至 Osd.cs（职责独立组件）

    class TrayApp : NativeWindow {
        readonly NotifyIcon icon;
        readonly ContextMenuStrip menu;
        readonly ToolStripMenuItem[] modeItems = new ToolStripMenuItem[6];
        ToolStripMenuItem autoStartItem;
        readonly ToolStripMenuItem[] schedItems = new ToolStripMenuItem[3];
        readonly System.Windows.Forms.Timer timer;
        readonly Icon[] icons = new Icon[6];
        uint shownMode = 99;
        DateTime oursSwitchAt = DateTime.MinValue;


        public TrayApp() {
            CreateHandle(new CreateParams());
            menu = BuildMenu();
            for (uint i = 0; i <= 5; i++) icons[i] = MakeIcon(ModeColor[i]);
            icon = new NotifyIcon {
                ContextMenuStrip = menu,
                Visible = true,
                Text = "联想性能控制"
            };
            RegisterHotKeys();
            if (Sensors.Available) { Sensors.Update(); Log(string.Format("传感器: 已启用 CPU={0}C GPU={1}C 负载={2}%", Sensors.CpuTemp(), Sensors.GpuTemp(), Sensors.CpuLoad())); } else Log("传感器: 未启用（缺 LibreHardwareMonitorLib.dll） " + (Sensors.InitError ?? ""));
            timer = new System.Windows.Forms.Timer { Interval = 2000 };
            timer.Tick += (s, e) => {
                if (Sensors.Available) Sensors.Update();
                if (SmartEngine.Enabled) {
                    uint target = SmartEngine.Evaluate();
                    if (target != 0 && target != DisplayMode()) Switch(target, false);
                }
                Refresh();
            };
            Refresh();
            timer.Start();
        }

        ContextMenuStrip BuildMenu() {
            var m = new ContextMenuStrip();
            // 调度模式三选一（互斥单选，谁开车一目了然）
            for (int i = 0; i < 3; i++) {
                int sm = i;
                var it = new ToolStripMenuItem("○ " + SchedMode.Name(sm));
                it.Click += (s, e) => {
                    SchedMode.Set(sm);
                    Osd.Show("调度：" + SchedMode.Name(sm), Color.MediumPurple);
                    Refresh();
                };
                schedItems[i] = it;
                m.Items.Add(it);
            }
            m.Items.Add(new ToolStripSeparator());
            for (uint i = 1; i <= 4; i++) {
                uint mode = i;
                var it = new ToolStripMenuItem(ModeNames[i] + "模式") { Tag = mode };
                it.Click += (s, e) => Switch(mode);
                modeItems[i] = it;
                m.Items.Add(it);
            }
            m.Items.Add(new ToolStripSeparator());
            autoStartItem = new ToolStripMenuItem("开机自启+保活");
            autoStartItem.Click += (s, e) => {
                if (!SetAutoStart(!AutoStartEnabled()))
                    icon.ShowBalloonTip(2000, "联想性能控制", "设置自启失败（需要管理员权限）", ToolTipIcon.Warning);
                Refresh();
            };
            m.Items.Add(autoStartItem);
            m.Items.Add(new ToolStripSeparator());
            var exit = new ToolStripMenuItem("退出");
            exit.Click += (s, e) => {
                // 显式退出 = 同时撤自启+保活任务，否则保活触发器 5 分钟内会把托盘拉回来
                if (AutoStartEnabled()) SetAutoStart(false);
                icon.Visible = false; Application.Exit();
            };
            m.Items.Add(exit);
            return m;
        }

        void RegisterHotKeys() {
            var sb = new System.Text.StringBuilder();
            for (uint i = 1; i <= 4; i++) {
                bool ok = RegisterHotKey(Handle, (int)i, MOD_CONTROL | MOD_ALT, 0x30 + i); // '1'-'4'
                sb.Append("Ctrl+Alt+").Append(i).Append(ok ? "√ " : "×被占用 ");
            }
            bool okQ = RegisterHotKey(Handle, 9, MOD_CONTROL | MOD_ALT, 0x51); // 'Q'
            sb.Append("Ctrl+Alt+Q").Append(okQ ? "√" : "×被占用");
            Log("热键注册: " + sb);
            if (!okQ) Osd.Show("⚠ Ctrl+Alt+Q 被其他程序占用", Color.OrangeRed);
        }

        void Switch(uint mode, bool manual = true) {
            if (manual) SchedMode.Set(SchedMode.Manual); // 手动切档 = 接管回手动
            oursSwitchAt = DateTime.Now;
            string r = ApplyMode(mode);
            // 调度器完整应用有延迟（实测性能档最长约12秒），两次读回校验
            System.Threading.Thread.Sleep(800);
            bool ok = Verify(mode);
            if (!ok) {
                System.Threading.Thread.Sleep(3000);
                ok = Verify(mode);
            }
            string verdict = ok ? "✓已生效" : "⚠未生效 " + Signature();
            Log(string.Format("切档 {0} => {1} {2}", Name(mode), r, verdict));
            shownMode = 99; // 强制刷新
            Refresh();
            Osd.Show((ok ? "● " : "●? ") + Name(mode) + "模式 " + (manual ? "[手动]" : "[温控]")
                + "  " + (ok ? "已生效" : "未生效"), ModeColor[Math.Min((int)mode, 5)]);
        }

        void Refresh() {
            uint cur = DisplayMode();
            bool modeChanged = cur != shownMode;
            uint prev = shownMode;
            shownMode = cur;
            // 层级仲裁：检测外部切档（Fn+Q 等不经过本工具的档位变化）= 用户接管
            // 我们自己的切档有 8 秒时间戳标记；其余变化视为外部干预 → 切回手动模式
            if (modeChanged && prev != 99) {
                bool ours = (DateTime.Now - oursSwitchAt).TotalSeconds < 8;
                if (!ours) {
                    SchedMode.Set(SchedMode.Manual);
                    Log(string.Format("外部切档 {0}->{1}（Fn+Q/系统），已接管为手动",
                        Name(prev), Name(cur)));
                }
            }
            icon.Icon = icons[Math.Min((int)cur, 5)];
            string tip = "联想性能控制 - " + Name(cur) + "模式";
            if (Sensors.Available) {
                var cpuT = Sensors.CpuTemp(); var gpuT = Sensors.GpuTemp();
                if (cpuT.HasValue) tip += " | CPU " + cpuT.Value.ToString("0") + "°";
                if (gpuT.HasValue) tip += " GPU " + gpuT.Value.ToString("0") + "°";
            }
            int sm = SchedMode.Current();
            tip += " [" + SchedMode.Name(sm) + "]";
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            icon.Text = tip;
            for (uint i = 1; i <= 4; i++) modeItems[i].Checked = (i == cur);
            for (int i = 0; i < 3; i++) schedItems[i].Text = (i == sm ? "● " : "○ ") + SchedMode.Name(i);
            autoStartItem.Checked = AutoStartEnabled();
        }

        static Icon MakeIcon(Color c) {
            using (var bmp = new Bitmap(16, 16)) {
                using (var g = Graphics.FromImage(bmp)) {
                    g.Clear(Color.Transparent);
                    using (var b = new SolidBrush(c)) g.FillEllipse(b, 1, 1, 14, 14);
                    g.DrawEllipse(Pens.White, 1, 1, 14, 14);
                }
                return (Icon)Icon.FromHandle(bmp.GetHicon()).Clone();
            }
        }

        protected override void WndProc(ref Message m) {
            if (m.Msg == WM_HOTKEY) {
                int id = m.WParam.ToInt32();
                Log("收到热键 id=" + id);
                if (id == 9) {
                    int idx = Array.IndexOf(Cycle, CurrentMode());
                    Switch(Cycle[(idx + 1) % Cycle.Length]);
                } else if (id >= 1 && id <= 4) Switch((uint)id);
            }
            base.WndProc(ref m);
        }
    }

    // ---------- 入口 ----------
    [STAThread]
    static void Main(string[] args) {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        if (args.Length == 0) {
            Config.Load(System.IO.Path.Combine(Application.StartupPath, "LenovoPerfCtl.json"));
            Sensors.Init();
            SmartEngine.LoadConfig(System.IO.Path.Combine(Application.StartupPath, "LenovoPerfCtl.json"));
            SmartEngine.Enabled = Config.Get("smart") == "1";
            SchedMode.NormalizeAtStartup();
            // 托盘模式：需要管理员（注册表 HKLM + ControlService）
            if (!IsAdmin()) { RelaunchElevated(); return; }
            bool created;
            using (var mutex = new Mutex(true, "LenovoPerfCtl_Single", out created)) {
                if (!created) return; // 已在运行
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                new TrayApp();
                Application.Run();
            }
            return;
        }

        // 命令行模式：winexe 下重新挂接父控制台以显示输出
        AttachConsole(0xFFFFFFFF); // ATTACH_PARENT_PROCESS
        if (!IsAdmin()) { Console.WriteLine("需要管理员权限（右键管理员运行，或用托盘模式自动提权）"); return; }

        switch (args[0].ToLowerInvariant()) {
            case "get":
                Console.WriteLine("当前模式: {0}", Name(DisplayMode()));
                Console.WriteLine("状态签名: {0}", Signature());
                break;
            case "set":
                if (args.Length < 2) { Console.WriteLine("用法: set <auto|cool|perf|geek|1-5>"); return; }
                Console.WriteLine("切换到: {0}", Name(ParseMode(args[1])));
                Console.WriteLine("结果: {0}", ApplyMode(ParseMode(args[1])));
                Thread.Sleep(800);
                Console.WriteLine("读回: {0}  校验: {1}", Signature(), Verify(ParseMode(args[1])) ? "✓生效" : "✗未生效");
                break;
            default:
                Console.WriteLine("LenovoPerfCtl - 联想性能模式独立控制");
                Console.WriteLine("  无参数启动 = 托盘模式（Ctrl+Alt+1/2/3/4 切档, Ctrl+Alt+Q 循环）");
                Console.WriteLine("  get        查看当前模式");
                Console.WriteLine("  set <auto|cool|perf|geek|1-5>  切换模式");
                break;
        }
        Console.Out.Flush();
    }

    static uint ParseMode(string s) {
        switch (s.ToLowerInvariant()) {
            case "auto": case "智能": return 1;
            case "cool": case "省电": return 2;
            case "perf": case "性能": return 3;
            case "geek": case "极客": return 4;
            default: return Convert.ToUInt32(s);
        }
    }
}
