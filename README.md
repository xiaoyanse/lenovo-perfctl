# LenovoPerfCtl - 联想性能模式独立控制

单文件替代联想 Vantage 的电源模式切换（省电/智能/性能/极客四档），17KB、零依赖、常驻内存约 11MB
（Vantage 全家桶 4-5GB）。卸载 Vantage 后四档功能完整保留——本工具直连联想驱动服务。

实测机型：ThinkBook 16 G7+ IAH（21TL）+ LenovoProcessManagement 服务 1.x。
其他联想机型若带同版调度服务理论上通用，欢迎反馈。

## 功能

- **托盘常驻**：图标颜色随模式（蓝=智能、绿=省电、橙=性能、红=极客），3 秒与系统状态同步
- **四档直切**：右键菜单四档 / `Ctrl+Alt+1/2/3/4` 直切 / `Ctrl+Alt+Q` 循环
- **切档 OSD**：屏幕大字提示 + 自动读回校验（真生效才显示 ✓）
- **调度模式三选一（互斥）**：手动（档位锁定不被自动改）/ 联想 AI 引擎 / 温度自适应
  （CPU/GPU 温度 + 负载 + 电源状态自动切四档：高温升档散热、低温轻载降档、电池锁智能；
  阈值在 `LenovoPerfCtl.json` 可调）。
  任何手动切档（菜单/热键/Fn+Q/CLI）自动接管为手动模式，托盘提示显示当前谁在开车
- **托盘温度显示**：悬停图标看 CPU/GPU 实时温度
- **开机自启 + 保活**：计划任务双触发器（登录拉起 + 每 5 分钟守护，崩溃自动复活）
- **CLI 模式**：`get` / `set auto|cool|perf|geek`（管理员终端）

## 使用

托盘模式：双击 `LenovoPerfCtl.exe`（自动 UAC 提权；单实例保护）。

命令行模式（管理员）：

```
LenovoPerfCtl.exe get                        # 查看当前模式与状态签名
LenovoPerfCtl.exe set auto|cool|perf|geek|1-5
```

托盘"退出"会同时撤销自启/保活任务（避免守护触发器把退出的托盘拉回来）；
需要再用时双击程序，菜单里重新勾选"开机自启+保活"。

## 编译

系统自带编译器，无需安装任何构建工具（管理员终端，仓库根目录执行）：

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe -nologo -target:winexe -codepage:65001 ^
  -out:dist\LenovoPerfCtl.exe ^
  -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:dist\LibreHardwareMonitorLib.dll ^
  src\LenovoPerfCtl.cs src\Sensors.cs src\SmartEngine.cs src\SchedMode.cs src\Osd.cs src\MenuStyle.cs
```

目录布局：`src/` 源码、`dist/` 完整运行包（exe + 全部依赖 DLL，即编译产物目录）。
缺 DLL 时自动降级，四档切换不受影响，仅温度监控与智能调度不可用；
也可以直接到本仓库 Releases 页下载编译好的完整包。

## 工作原理

四档控制走 `ControlService("LenovoProcessManagement", 控制码)` 自定义码（128-255 区间），
码表通过分析调度服务控制处理器还原，与实机行为交叉验证：

| 控制码 | 作用 | 调度器内部动作 |
|---|---|---|
| 0xA3 | 省电（=关闭） | ITS 0 + 固件 SetSmartFanMode(0x13)，清极客位 |
| 0xA4 | 智能 | ITS 1 + SetSmartFanMode(0x11)，清极客位 |
| 0xA5 | 性能 | ITS 3 + SetSmartFanMode(0x15)，清极客位 |
| 0xAC | 极客 | 置 VantageSetting bit2 + 强制 ITS 3 + 场景 0x40 |
| 0xAD / 0xAE | 极客自定义 开/关 | VantageSetting bit3 |
| 0xAA / 0xAB | AI 自动切换 开/关 | VantageSetting bit1 |

切档两步（Vantage 同款）：先写注册表 `ITS_CurrentSettingV`（目标档），再发控制码；
调度器随后自行回写固件状态 `ITS_CurrentSetting` 并归一化。

状态读取（含生效校验）：

- 键：`HKLM\SYSTEM\CurrentControlSet\Services\LenovoProcessManagement\Performance\PowerSlider`
- 生效签名：省电 `Cur=0`｜智能 `Cur=1`｜性能 `Cur=3`｜极客 `Cur=3, V=4, VSet=0x205`

固件 WMI 直写（`LENOVO_GAMEZONE_DATA.SetSmartFanMode`）保留为控制码被拒时的兜底路径。

## 前置条件

- 需要 `LenovoProcessManagement` 服务（联想驱动包自带，**勿禁用/卸载**）
- 切档需要管理员权限（工具托盘模式自动 UAC 提权）

## 温度传感器依赖

温度读取集成 [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)（MPL-2.0 开源）。
所需 DLL 已随仓库提供于 `dist/` 目录（与 exe 同级，开箱即用），第三方许可见 `THIRD-PARTY-NOTICES.md`；
缺失时工具自动降级（无温度显示，智能调度不可用），不影响四档切换主功能。

```json
// LenovoPerfCtl.json 阈值示例（缺省用内置默认值）
cpu_hot=88
cpu_cool=62
gpu_hot=80
load_high=65
hot_sec=10
cool_sec=60
```

## 声明

- 仅供个人在自己拥有的联想设备上学习研究使用，使用自担风险
- 与 Lenovo 无任何关联；Lenovo/Vantage 是联想公司的商标
- 不要公开分发从联想软件还原的内部材料，本仓库只包含自主编写的源码
