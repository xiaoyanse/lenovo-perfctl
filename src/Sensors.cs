using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// 传感器桥：LibreHardwareMonitor（MPL-2.0，DLL 放 exe 旁即启用；缺失时优雅降级）
// 信号：CPU 温度(Package)/GPU 温度/CPU 占用/内存占用/电源状态
// 两个结构性防御（2026-11-14 烤机卡死事故教训）：
//   ① LHM 交互全部隔离在 SensorHost 类型——.NET JIT 按方法加载引用类型，
//     老写法 Sensors.Init 方法体引用 LHM 类型，缺 DLL 时异常在方法 JIT 阶段抛出，
//     方法内 try 接不住，进程直接崩（"缺 DLL 自动降级"从未生效过）
//   ② 轮询在专用后台线程——LHM 在高温高负载下有内部锁风暴（实测 3 线程互烧 1.5 核），
//     绝不能在 UI 线程同步调 Update；连发卡顿自动熔断，防止后台空烧
static class Sensors {
    static SensorHost host;
    static Thread worker;
    static volatile bool running;
    static volatile float cpuT = float.NaN, gpuT = float.NaN, load = float.NaN, mem = float.NaN;
    const int PollMs = 2000;
    const int StallMs = 3000, StallLimit = 3; // 单次轮询超时阈值与熔断次数

    public static bool Available { get; private set; }
    public static string InitError { get; private set; }

    public static void Init() {
        try {
            host = new SensorHost(); // LHM 程序集在此处（try 内）才被 JIT 加载 → 缺 DLL 可降级
            host.Open();
            Available = host.Ok;
        } catch (Exception ex) {
            InitError = ex.Message;
            Available = false;
            return;
        }
        if (!Available) return;
        running = true;
        worker = new Thread(PollLoop) { IsBackground = true, Name = "SensorPoll" };
        worker.Start();
    }

    static void PollLoop() {
        int stalls = 0, slows = 0;
        while (running) {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { host.Update(); } catch (Exception ex) {
                LenovoPerfCtl.Log("传感器轮询异常，停用: " + ex.Message);
                break;
            }
            long ms = sw.ElapsedMilliseconds;
            if (ms > 30000) { // 真挂起才计入熔断
                stalls++;
                LenovoPerfCtl.Log("传感器轮询挂起 " + ms + "ms（" + stalls + "/" + StallLimit + "）");
                if (stalls >= StallLimit) {
                    LenovoPerfCtl.Log("传感器连续挂起，熔断停用（温度显示与温控调度降级）");
                    break;
                }
            } else if (ms > StallMs) { // 慢轮询常见于高负载（数据晚几秒不影响调度），只低频记录
                slows++;
                if (slows % 10 == 1) LenovoPerfCtl.Log("传感器轮询偏慢 " + ms + "ms（周期性，不影响调度）");
            } else stalls = 0;
            float? c = host.CpuTemp(), g = host.GpuTemp(), l = host.CpuLoad(), m = host.MemLoad();
            cpuT = c ?? float.NaN; gpuT = g ?? float.NaN; load = l ?? float.NaN; mem = m ?? float.NaN;
            Thread.Sleep(PollMs);
        }
        running = false;
        Available = false;
    }

    // UI 线程只读缓存快照，永不接触 LHM
    public static float? CpuTemp() { return Val(cpuT); }
    public static float? GpuTemp() { return Val(gpuT); }
    public static float? CpuLoad() { return Val(load); }
    public static float? MemLoad() { return Val(mem); }
    static float? Val(float v) { return float.IsNaN(v) ? (float?)null : v; }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemPowerStatus(ref PowerStatus ps);
    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, Reserved1;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }
    public static bool OnAcPower() {
        var ps = new PowerStatus();
        GetSystemPowerStatus(ref ps);
        return ps.ACLineStatus != 0;
    }
}

// LHM 唯一触碰点：类型隔离边界，缺 DLL 时只在 new/Open 处抛可捕获异常
class SensorHost {
    readonly List<LibreHardwareMonitor.Hardware.IHardware> cpuList =
        new List<LibreHardwareMonitor.Hardware.IHardware>();
    readonly List<LibreHardwareMonitor.Hardware.IHardware> gpuList =
        new List<LibreHardwareMonitor.Hardware.IHardware>();
    LibreHardwareMonitor.Hardware.IHardware mem;
    public bool Ok;

    public void Open() {
        var computer = new LibreHardwareMonitor.Hardware.Computer {
            IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true
        };
        computer.Open();
        foreach (var hw in computer.Hardware) {
            var t = hw.HardwareType;
            if (t == LibreHardwareMonitor.Hardware.HardwareType.Cpu) cpuList.Add(hw);
            else if (t == LibreHardwareMonitor.Hardware.HardwareType.GpuNvidia ||
                     t == LibreHardwareMonitor.Hardware.HardwareType.GpuAmd ||
                     t == LibreHardwareMonitor.Hardware.HardwareType.GpuIntel) gpuList.Add(hw);
            else if (t == LibreHardwareMonitor.Hardware.HardwareType.Memory) mem = hw;
        }
        Ok = cpuList.Count > 0;
    }

    public void Update() {
        foreach (var h in cpuList) h.Update();
        foreach (var h in gpuList) h.Update();
        if (mem != null) mem.Update();
    }

    // CPU Package 温度（无读数返回 null；兜底 Core Max）
    public float? CpuTemp() {
        float? best = null;
        foreach (var h in cpuList)
            foreach (var s in h.Sensors)
                if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature &&
                    s.Name.Contains("Package") && s.Value.HasValue)
                    best = Math.Max(best ?? 0, s.Value.Value);
        if (best != null) return best;
        foreach (var h in cpuList)
            foreach (var s in h.Sensors)
                if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature &&
                    s.Name.Contains("Max") && s.Value.HasValue)
                    return s.Value.Value;
        return null;
    }

    public float? GpuTemp() {
        float? best = null;
        foreach (var h in gpuList)
            foreach (var s in h.Sensors)
                if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Temperature && s.Value.HasValue) {
                    string n = s.Name.ToLowerInvariant();
                    if (n.Contains("hot spot") || n.Contains("memory")) continue;
                    best = Math.Max(best ?? 0, s.Value.Value);
                }
        return best;
    }

    public float? CpuLoad() {
        foreach (var h in cpuList)
            foreach (var s in h.Sensors)
                if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Load &&
                    s.Name.ToLowerInvariant().Contains("total") && s.Value.HasValue)
                    return s.Value.Value;
        return null;
    }

    public float? MemLoad() {
        if (mem == null) return null;
        foreach (var s in mem.Sensors)
            if (s.SensorType == LibreHardwareMonitor.Hardware.SensorType.Load &&
                s.Name.ToLowerInvariant().Contains("memory") && s.Value.HasValue)
                return s.Value.Value;
        return null;
    }
}
