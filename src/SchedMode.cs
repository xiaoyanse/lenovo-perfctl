// 调度控制权三态（互斥单选）：手动=用户定档永不被改 / 联想AI=服务侧自动 / 温度自适应=本工具自动
// 任何手动切档（菜单/热键/Fn+Q 外部检测/CLI）都视为接管，统一回到"手动"
static class SchedMode {
    public const int Manual = 0, LenovoAi = 1, Thermal = 2;
    static readonly string[] Names = { "手动", "联想AI引擎", "温度自适应" };

    public static string Name(int m) { return Names[m]; }

    // 状态真相源：SmartEngine.Enabled（json smart 位）+ 联想注册表 bit1，无独立存储
    public static int Current() {
        if (SmartEngine.Enabled) return Thermal;
        return LenovoPerfCtl.AiAutoOn() ? LenovoAi : Manual;
    }

    public static void Set(int m) {
        if (m == Current()) return;
        if (m == Thermal) {
            // 温控接管：关联想AI，避免两套引擎互相改档
            if (LenovoPerfCtl.AiAutoOn()) {
                LenovoPerfCtl.SetAiAuto(false);
                LenovoPerfCtl.Log("调度接管：已关闭联想AI引擎");
            }
            SmartEngine.Enabled = true;
            Config.Set("smart", "1");
        } else if (m == LenovoAi) {
            SmartEngine.Enabled = false;
            Config.Set("smart", "0");
            if (!LenovoPerfCtl.SetAiAuto(true)) {
                LenovoPerfCtl.Log("开启联想AI引擎失败（控制码被拒）");
                return;
            }
        } else {
            if (SmartEngine.Enabled) { SmartEngine.Enabled = false; Config.Set("smart", "0"); }
            if (LenovoPerfCtl.AiAutoOn()) LenovoPerfCtl.SetAiAuto(false);
        }
        LenovoPerfCtl.Log("调度模式 => " + Name(m));
    }

    // 启动归一化：历史版本可能留下双开残留（温控+AI 同时开），尊重 smart=1 让温控接管
    public static void NormalizeAtStartup() {
        if (SmartEngine.Enabled && LenovoPerfCtl.AiAutoOn()) {
            LenovoPerfCtl.SetAiAuto(false);
            LenovoPerfCtl.Log("启动归一化：温控接管，已关闭联想AI引擎");
        }
    }
}
