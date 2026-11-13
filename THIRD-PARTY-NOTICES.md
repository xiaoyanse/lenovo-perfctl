# 第三方组件声明（Third-Party Notices）

本仓库以二进制（DLL）形式再分发以下第三方组件，用于温度/负载传感器增强功能。
各组件版权归原作者所有，不在本项目的授权范围内；许可证全文见 `LICENSES/` 目录。

| 组件 | 版本 | 许可证 | 上游来源 |
|---|---|---|---|
| LibreHardwareMonitorLib | 0.9.7-pre785 | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| RAMSPDToolkit-NDD | 1.6.4 | MPL-2.0 | https://www.nuget.org/packages/RAMSPDToolkit-NDD |
| DiskInfoToolkit | 2.2.2 | MPL-2.0 | https://www.nuget.org/packages/DiskInfoToolkit |
| HidSharp | 2.6.4 | Apache-2.0 | https://www.hidguide.org/ |
| System.Memory | 4.6.3 | MIT | https://github.com/dotnet/runtime |
| System.Buffers | 4.6.1 | MIT | https://github.com/dotnet/runtime |
| System.Numerics.Vectors | 4.6.1 | MIT | https://github.com/dotnet/runtime |
| System.Runtime.CompilerServices.Unsafe | 6.1.2 | MIT | https://github.com/dotnet/runtime |
| System.Threading.AccessControl | 11.0.0 | MIT | https://github.com/dotnet/runtime |
| System.Security.Principal.Windows | 5.0.0 | MIT | https://github.com/dotnet/runtime |
| System.Management | 11.0.0 | MIT | https://github.com/dotnet/runtime |

许可证文本索引：

- `LICENSES/MPL-2.0.txt` — Mozilla Public License 2.0 全文（LibreHardwareMonitorLib、RAMSPDToolkit-NDD、DiskInfoToolkit）
- `LICENSES/Apache-2.0.txt` — Apache License 2.0 全文（HidSharp，Copyright 2010-2025 James F. Bellinger）
- `LICENSES/MIT.txt` — MIT License 文本（System.* 系列，.NET Foundation and Contributors）

MPL-2.0 组件按其第 3 条以可执行形式（DLL，未修改）分发，源码见上表上游链接；
上述组件的功能由其各自作者提供，不构成本项目的任何担保。
