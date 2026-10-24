# 外设电量

中文 Windows 桌面电量与连接监控工具，C# / WPF / .NET 10 LTS。使用原生 Win32 托盘、SetTimer、设备通知和当前用户自启，不需要管理员权限、浏览器运行时或后台服务。正式运行没有虚构设备或电量。

## 直接运行

当前版本 **1.1.0**：按参考图新增每设备独立的环形电量托盘；普通 ZEN65 的 USB 原生接口已实机读通，设备报告 100%。本机 2.4G 接收器 `36B0:3002` 的电量仍未打通，充电字段因官方驱动与固件语义冲突保留未知。完整证据见 [ZEN65 调查](docs/ZEN65-RESEARCH.md)。没有刷写或修改键盘固件。

打开 `artifacts/win-x64/PeripheralPower.exe`，或解压 `artifacts/PeripheralPower-win-x64.zip` 后双击 `PeripheralPower.exe`。这是 Windows x64 自包含发布，程序目录内的 DLL 和运行时文件需要一同保留。

本地 artifacts/ 保留运行包和源码包。下载副本、设备探测数据及未完成的无线实验已移入本地 .archive/，不提交仓库。

1. 在“发现设备”中选择系统实际发现的设备，点击“加入我的设备”。同一 Container ID 的 HID 接口合并。
2. 在“我的设备”中改名称、排序、隐藏或移除。不同模式的身份不会凭名称自动合并；确认是同一个实物后，可在发现页关联到当前目标。
3. 点击“简洁面板”打开紧凑卡片。面板空白处右键可管理、刷新、切换主题或退出。
4. **左键托盘打开或前置原简洁面板；右键托盘直接打开管理窗口的全局设置页。** 两种点击都不触发刷新，不先弹出菜单。需要手动刷新时，使用面板空白处右键菜单或管理窗口的“立即刷新”。悬停只展示缓存，不查询硬件。关闭两个窗口都保留托盘；通过“退出程序”彻底退出。

自动刷新默认 60 秒，允许 30 秒到 30 分钟。修改后点“保存并立即应用”。自动刷新关闭时仍可手动刷新。没有监测目标时不设周期刷新定时器。托盘显示超长信息时，浮层可滚动，离开图标和浮层后收起。

每个已取得有效电量的可见监测目标独立显示一个托盘图标：深色圆底、绿色开口电量环、白色数字和底部设备符号；档位保持如 `3/5` 的表达。历史值与系统缓存用灰色显示，并在悬停中说明来源和时间。隐藏、移除目标后对应图标会更新；完全没有有效电量时保留一个入口图标。图标按 Windows 托盘 DPI 绘制，不增加每秒刷新定时器。

## 读数如何理解

- 电量百分比、0～5 档、未知、不支持及上次电量分别显示。没有返回值不会显示为 0%。
- 接收器或充电底座存在，不代表外设本体开机；蓝牙配对也不代表当前在线。证据不足显示“连接状态未知”。
- 电量时间是最近有效设备查询时间。读取失败保留成功时间并标为历史值。Windows 缓存会标为“系统缓存、采样时间未知”，并分别列出缓存观察时间；它不是设备实时采样时间。
- 充电状态只有协议确实提供时显示，USB 连接不会自动标为充电。
- 三款重点设备的支持范围见 [兼容性表](docs/COMPATIBILITY.md)，调查来源和限制见 [硬件说明](docs/HARDWARE.md)。未验证的连接方式不能视作支持承诺。

## 设置与数据

“全局设置与诊断”提供周期、自启、手动启动窗口和深浅色主题，可通过右键任一托盘图标直接进入。开启自启使用 HKCU Run，登录时静默驻留托盘。如果被 Windows 的启动应用设置禁用，会如实显示，程序不会绕过系统禁用。

默认数据目录：`%LOCALAPPDATA%\PeripheralPower`。包含 `config.json`、`state-cache.json` 和滚转日志。日志文件约 512 KiB 轮转，保留 3 份。历史状态跨启动保留，但会清除当前在线判断。

诊断导出到你选择的本地 JSON，包含设备路径、Container ID、父节点、VID/PID、Usage、接口、适配器匹配依据和错误，不收集按键内容，不自动上传。设备序列号及路径可能识别实物，分享前可自行删去。

## 构建、测试和发布

安装 Microsoft .NET 10 SDK（当前工程使用 10.0.402，允许同系列更新），然后在 PowerShell 7（`pwsh`）中进入项目目录运行：

```powershell
.\scripts\build.ps1
.\scripts\build.ps1 -Publish
.\scripts\build.ps1 -NativeTests
```

原生集成测试需先退出正在运行的应用，避免与单实例测试争用。脚本优先使用项目内 `.tools\dotnet\dotnet.exe`，否则使用 PATH 中的 dotnet。没有第三方 NuGet 包；自包含发布首次需要从官方 NuGet 获取 Microsoft 运行时包。也可直接执行：

```powershell
dotnet build src/PeripheralPower.App/PeripheralPower.App.csproj -c Release
dotnet run --project tests/PeripheralPower.Tests -c Release
dotnet run --project tests/PeripheralPower.NativeTests -c Release
dotnet publish src/PeripheralPower.App/PeripheralPower.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

测试说明和实际结果见 [验收记录](docs/TEST-RESULTS.md)。本轮多图标及新点击交互的原生/UI 测试 **17/17 通过**。[两段 10 分钟性能实测](docs/PERFORMANCE.md) 属于改版前的单图标版本，不能直接代表当前多图标版本；本轮没有重跑 20 分钟测量。性能测试脚本在 `scripts/Measure-Performance.ps1`，已改为通过新左键路由打开面板，并识别当前实际托盘图标 ID。完整运行约 21 分钟，需先退出应用并准备实际发现记录；重复执行时指定新的 `-ProfileDirectory`。`scripts/package.ps1` 可独立重新打包当前发布目录、完整源码，并生成 SHA-256 校验文件。UI 集成测试命令会在指定的隔离配置目录中添加真实发现的设备，并生成截图，不注入模拟读数：

```powershell
.\artifacts\win-x64\PeripheralPower.exe --data-dir "$PWD\artifacts\ui-profile" --ui-smoke "$PWD\artifacts\ui-smoke"
```

应用未运行时，可导出发现诊断并退出；应用运行中请使用管理窗口的“导出诊断”（重复启动会按单实例规则前置管理窗口）：

```powershell
.\artifacts\win-x64\PeripheralPower.exe --tray --diagnose "$PWD\artifacts\diagnostic.json"
```

## 工程组织

- `PeripheralPower.Core`：状态、缓存、配置、日志、刷新合并和通道串行。
- `PeripheralPower.Hardware`：SetupAPI / HID / ConfigManager 发现、协议匹配和有界隔离探测进程。
- `PeripheralPower.App/Native`：Win32 消息宿主、SetTimer、托盘、睡眠/设备事件、单实例与自启。
- `PeripheralPower.App/Views`：简洁面板、管理窗口和悬停浮层。
- `tests`：独立于正式设备的模型、协议 fixture、异常压力和原生消息集成测试。

新增型号按 [适配指南](docs/ADAPTER-GUIDE.md) 操作。项目采用 GPL-3.0；上游来源和许可证见 [第三方声明](THIRD-PARTY-NOTICES.md)。

目标实测平台是 Windows 11 x64（build 26100）。没有运行 Windows 10、Windows on ARM 或其他架构的兼容性测试，不宣称已支持这些环境。
