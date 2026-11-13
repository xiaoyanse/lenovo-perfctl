using System;
using System.Globalization;
using System.IO;

// 智能调度引擎：按传感器信号自动切四档（默认规则 + 阈值可配置 + 滞回防抖）
// 设计：高温升档散热（档位越高风扇曲线越激进）、低温/轻载降档省电、电池保守、手动切档后暂停自动
static class SmartEngine {
    public static bool Enabled;
    public static DateTime pauseUntil = DateTime.MinValue;

    // 默认阈值（可在 LenovoPerfCtl.json 覆盖）
    public static int CpuHot = 88;        // °C，持续 HotSec 升档
    public static int CpuCool = 62;       // °C，持续 CoolSec 且轻载降档
    public static int GpuHot = 80;        // °C，GPU 热时至少性能档
    public static int LoadHigh = 65;      // % CPU 占用，持续 HighSec 至少性能档
    public static int LoadLow = 35;       // % 占用低于此视为轻载
    public static int HotSec = 10;        // 高温确认秒数
    public static int CoolSec = 60;       // 低温确认秒数
    public static int HighSec = 15;       // 高负载确认秒数
    public static int MinDwell = 90;      // 每档最短驻留秒（防抖）
    public static int PauseMin = 10;      // 手动切档后自动调度暂停分钟

    static DateTime hotSince = DateTime.MinValue, coolSince = DateTime.MinValue, highSince = DateTime.MinValue;
    static DateTime lastSwitch = DateTime.MinValue;
    static readonly uint[] Ladder = { 1, 3, 4 }; // 智能→性能→极客（电池时上限=智能）
    public static string LastDecision = "";

    public static void LoadConfig(string path) {
        try {
            foreach (string line in File.ReadAllLines(path)) {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                int val;
                if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out val)) continue;
                switch (k) {
                    case "cpu_hot": CpuHot = val; break;
                    case "cpu_cool": CpuCool = val; break;
                    case "gpu_hot": GpuHot = val; break;
                    case "load_high": LoadHigh = val; break;
                    case "load_low": LoadLow = val; break;
                    case "hot_sec": HotSec = val; break;
                    case "cool_sec": CoolSec = val; break;
                    case "high_sec": HighSec = val; break;
                    case "min_dwell": MinDwell = val; break;
                    case "pause_min": PauseMin = val; break;
                }
            }
        } catch { }
    }

    public static void NotifyManualSwitch() {
        pauseUntil = DateTime.Now.AddMinutes(PauseMin);
        ResetTimers();
    }

    static void ResetTimers() {
        hotSince = coolSince = highSince = DateTime.MinValue;
    }

    // 每 2 秒由托盘 Timer 调用；返回需要切到的档位，0=不动作
    public static uint Evaluate() {
        if (!Enabled || DateTime.Now < pauseUntil) { LastDecision = Enabled ? "手动优先(暂停中)" : "关闭"; return 0; }
        if ((DateTime.Now - lastSwitch).TotalSeconds < MinDwell) { LastDecision = "驻留冷却中"; return 0; }

        float? cpuT = Sensors.CpuTemp(), gpuT = Sensors.GpuTemp(), load = Sensors.CpuLoad();
        bool ac = Sensors.OnAcPower();
        uint cur = DisplayModeSnapshot();
        int idx = Array.IndexOf(Ladder, cur);
        if (idx < 0) idx = 0; // 当前不在阶梯上（如省电）按智能处理

        DateTime now = DateTime.Now;
        LastDecision = string.Format("CPU={0} GPU={1} 负载={2} {3}",
            cpuT.HasValue ? cpuT.Value.ToString("0") : "?",
            gpuT.HasValue ? gpuT.Value.ToString("0") : "?",
            load.HasValue ? load.Value.ToString("0") : "?",
            ac ? "插电" : "电池");

        // 电池：锁智能档（高温也不升——散热收益赶不上耗电）
        if (!ac) {
            ResetTimers();
            if (cur != 1) { lastSwitch = now; return 1; }
            return 0;
        }

        // 高温升档（散热优先：档位越高风扇越激进）
        if (cpuT.HasValue && cpuT >= CpuHot) {
            if (hotSince == DateTime.MinValue) hotSince = now;
            else if ((now - hotSince).TotalSeconds >= HotSec && idx < Ladder.Length - 1) {
                LastDecision += " → 高温升档";
                lastSwitch = now; ResetTimers();
                return Ladder[idx + 1];
            }
            coolSince = DateTime.MinValue;
            return 0;
        }
        hotSince = DateTime.MinValue;

        // 高负载至少性能档
        if (load.HasValue && load >= LoadHigh) {
            if (highSince == DateTime.MinValue) highSince = now;
            else if ((now - highSince).TotalSeconds >= HighSec && cur < 3) {
                LastDecision += " → 高负载升档";
                lastSwitch = now; ResetTimers();
                return 3;
            }
            coolSince = DateTime.MinValue;
            return 0;
        }
        highSince = DateTime.MinValue;

        // GPU 热至少性能档
        if (gpuT.HasValue && gpuT >= GpuHot && cur < 3) {
            LastDecision += " → GPU热升档";
            lastSwitch = now; ResetTimers();
            return 3;
        }

        // 低温+轻载降档
        if (cpuT.HasValue && cpuT <= CpuCool && (!load.HasValue || load <= LoadLow)) {
            if (coolSince == DateTime.MinValue) coolSince = now;
            else if ((now - coolSince).TotalSeconds >= CoolSec && idx > 0) {
                LastDecision += " → 低温降档";
                lastSwitch = now; ResetTimers();
                return Ladder[idx - 1];
            }
        } else coolSince = DateTime.MinValue;
        return 0;
    }

    static uint DisplayModeSnapshot() { return LenovoPerfCtl.DisplayMode(); }
}
