using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

// 传感器桥：LibreHardwareMonitor（MPL-2.0，DLL 放 exe 旁即启用；缺失时优雅降级）
// 信号：CPU 温度(Package)/GPU 温度/CPU 占用/内存占用/电源状态
static class Sensors {
    static Computer computer;
    static readonly List<IHardware> cpuList = new List<IHardware>();
    static readonly List<IHardware> gpuList = new List<IHardware>();
    static IHardware mem;
    public static bool Available { get; private set; }
    public static string InitError { get; private set; }

    public static void Init() {
        try {
            computer = new Computer {
                IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true
            };
            computer.Open();
            foreach (IHardware hw in computer.Hardware) {
                if (hw.HardwareType == HardwareType.Cpu) cpuList.Add(hw);
                else if (hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuAmd ||
                         hw.HardwareType == HardwareType.GpuIntel) gpuList.Add(hw);
                else if (hw.HardwareType == HardwareType.Memory) mem = hw;
            }
            Available = cpuList.Count > 0;
        } catch (Exception ex) {
            InitError = ex.Message;
            Available = false;
        }
    }

    public static void Update() {
        if (!Available) return;
        foreach (IHardware h in cpuList) h.Update();
        foreach (IHardware h in gpuList) h.Update();
        if (mem != null) mem.Update();
    }

    // CPU Package 温度（传感器库缺失或无读数时返回 null）
    public static float? CpuTemp() {
        float? best = null;
        foreach (IHardware h in cpuList)
            foreach (ISensor s in h.Sensors)
                if (s.SensorType == SensorType.Temperature && s.Name.Contains("Package") && s.Value.HasValue)
                    best = Math.Max(best ?? 0, s.Value.Value);
        if (best != null) return best;
        foreach (IHardware h in cpuList) // 兜底：Core Max
            foreach (ISensor s in h.Sensors)
                if (s.SensorType == SensorType.Temperature && s.Name.Contains("Max") && s.Value.HasValue)
                    return s.Value.Value;
        return null;
    }

    public static float? GpuTemp() {
        float? best = null;
        foreach (IHardware h in gpuList)
            foreach (ISensor s in h.Sensors)
                if (s.SensorType == SensorType.Temperature && s.Value.HasValue) {
                    string n = s.Name.ToLowerInvariant();
                    if (n.Contains("hot spot") || n.Contains("memory")) continue;
                    best = Math.Max(best ?? 0, s.Value.Value);
                }
        return best;
    }

    public static float? CpuLoad() {
        foreach (IHardware h in cpuList)
            foreach (ISensor s in h.Sensors)
                if (s.SensorType == SensorType.Load && s.Name.ToLowerInvariant().Contains("total") && s.Value.HasValue)
                    return s.Value.Value;
        return null;
    }

    public static float? MemLoad() {
        if (mem == null) return null;
        foreach (ISensor s in mem.Sensors)
            if (s.SensorType == SensorType.Load && s.Name.ToLowerInvariant().Contains("memory") && s.Value.HasValue)
                return s.Value.Value;
        return null;
    }

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
