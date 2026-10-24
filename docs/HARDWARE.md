# 硬件支持、证据与扩展

调查日期：2026-10-23。正式运行只显示本机枚举结果，不内置三款目标设备或示例电量。下面的“已实现”指源码中存在查询路径；“待实机验证”不代表兼容性已经确认。

## 当前矩阵

| 型号 / 模式 | 设备识别与连接 | 电量 | 充电 | 实际验证情况 |
| --- | --- | --- | --- | --- |
| ROG Harpe Ace Mini / USB 有线 | 精确 ASUS 0B05:1B63、MI_00、厂商 Usage Page；单容器合并接口 | HID 12/07 查询，0～100% | 回复字节 10 | Windows 查询代码已实现，目标实机待验证 |
| ROG Harpe Ace Mini / OMNI 2.4 GHz | 0B05:1ACE 的配对查询确认 1B65；同容器 COL01 FF02 / COL03 FF01 | HID 03/12/07，真实读取 46% | 实际回复“未充电” | 2026-10-23 本机读取通过；关机/模式切换/官方软件并发等仍需进一步实机验收 |
| ROG Harpe Ace Mini / 蓝牙 | 按 Windows 实例与容器识别；配对节点不等同在线 | 系统电量缓存可用时显示；采样时间未知 | 未实现 | 待实机验证 |
| 普通 WOB ZEN65 / USB 原生65字节 | 36B0:3025、FF60:0061、IN/OUT65及真实ReportID；有效本体回复证明在线 | AA10→AA30→AA11，真实报告100% | 未知，状态字节与UI映射冲突 | 2026-10-23 23:40:09实机通过电量/有线连接；有ADC上游，非固定USB占位 |
| 普通 WOB ZEN65 / 当前2.4GHz 36B0:3002 | 用户拔插确认接收器serial19971217；不据接收器存在推断本体在线 | 尚未读通；Report3F+63字节不套原生AA64 | 未确认 | VIA版本查询未回复、有界被动无报告，不证明永久不支持 |
| 普通 WOB ZEN65 / 原生2.4GHz 36B0:0001 | 官方配置MI01COL02、FF87:0020，仅65字节匹配启用；AA55链路查询 | 原生AA30已实现 | 未知 | 待对应实物验证；不是本机3002分支 |
| 普通 WOB ZEN65 / USB VIA33字节、蓝牙 | VIA33不发送原生AA64；蓝牙不将配对当在线 | VIA专用通路未确认；蓝牙只查有效系统缓存 | 未确认 | 不套RT协议，不伪造读数 |
| Flydigi Apex 5 / 有线、接收器 | 精确 37D7:2501、Usage Page FFA0、MI_02；回复必须证实型号 128/129 | command 1，0～5 档 | 高半字节单独解码 | Windows 通信移植已实现；上游 Linux 有实机证据，本项目待目标实机验证 |
| Flydigi Apex 5 / 蓝牙、XInput-only | 无可靠 XInput 槽位→PnP 物理身份映射 | 系统属性可用时显示缓存 | 未实现 | 不调用任意 XInput 槽位冒充选定设备 |
| 其他 HID / 蓝牙设备 | Windows 原生枚举；按 ContainerID 归并 | 只读已有 Windows 缓存；无值则不支持 | 未实现 | 无通用 HID 电量协议承诺 |

## 原生发现与身份

`WindowsDeviceDiscovery` 调用 SetupAPI 枚举当前 present devnodes、HID device interfaces，读取 ConfigManager 的容器 GUID、父设备、设备实例、总线、devnode status，以及 Windows 缓存的产品描述/制造商、HID VID/PID、Usage Page/Usage、报告长度。产品名称优先采用总线驱动缓存的 `DEVPKEY_Device_BusReportedDeviceDesc`，避免仅显示通用“HID Keyboard Device”。发现阶段不发实时 HID 产品/序列字符串请求：本机睡眠/接收器接口的两个字符串调用分别阻塞了约 5 秒，曾使整轮发现超时；移除后枚举在约 434 毫秒完成。诊断保留每个接口和每个节点；界面每个容器一项。没有有效 ContainerID 时回退为设备实例 ID，绝不通过名称合并两台设备。

ContainerID 是 Windows 提供的同一物理设备分组证据，详见 [Microsoft DEVPKEY_Device_ContainerId](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-containerid)。ContainerID 缺失时，若能找到具体 USB VID/PID 设备祖先则采用它的容器或实例身份，明确不采用 USB hub；虚拟 HID 的多个 collection 可按完全相同的设备 family 与精确父节点归并。本机 GVINPUT 的 5 个 collection 因而归为一项。仍不能建立可靠身份时保留单独实例，允许人工关联。

多功能接收器的容器不一定等于接收器后的一台外设，本版本不会据此推断在线。跨蓝牙、有线、接收器的自动关联需要额外可验证的 UID；未提供该关联。管理窗口可手动把多个身份关联为同一监测目标。

通用 USB 设备无法仅凭 USB bus 判断是有线本体还是 2.4 GHz 接收器，故默认连接方式“未知”。蓝牙总线/实例前缀可证明传输类型，仍不证明当前链路在线。厂商查询成功且回复通过校验，才记一次在线证据。查询失败保持连接状态未知，不写成断开或 0%。当前设备实例完全消失时系统属性路径可返回“已断开”。

## 状态读取和隔离

全部 SetupAPI、ConfigManager、CreateFile、HID I/O 只在短命 helper 进程执行。主程序 `IsolatedHardwareBackend` 最多运行 3 个 helper；同一通信通道固定散列到 64 个串行锁之一（可能保守串行，不会为历史设备无限累积锁）。发现操作另有串行锁，并在执行已知接收器身份查询前依次取得所有通道锁，以避免发现和监测同时向同一 OMNI 接收器提交命令；普通多个设备的读取仍可并发。默认发现 12 秒、单设备读取 5 秒，超时或取消使用 `Kill(entireProcessTree: true)`，等待进程终止后才释放调度占用。终止最多额外等待 2 秒；若无法确认退出，则停用后续探测直到应用重启，防止无限新增不可取消进程。

原生 HID 以共享读写访问打开，使用 overlapped FileStream；查询内还限制 1.4 秒、普通回复最多 2048 个报告、OMNI 回复最多 16 个报告，只有当前查询的短窗口会读取报告。不设置设备模式，不建立永久输入数据流，不记录键鼠或手柄输入内容。正式路径仅允许有来源的配对身份和电量状态只读命令；长度或 Usage 不符时拒绝发包。每次交换前只清理本次句柄输入队列一次，避免采用查询前的旧回复。打开/关闭 HID 句柄有可能触发设备固有唤醒行为；本版本没有修改灯光、DPI、映射、回报率或固件的 API。

Windows 官方 `System.Devices.BatteryLife` 是百分比 BYTE，[属性文档](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-devices-batterylife)给出其键和语义。另查询 Windows 蓝牙驱动实际使用的 `{104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2` BYTE 缓存，只接受单字节 0～100；此驱动键不是稳定公开 SDK 契约，不能保证各 Windows/驱动版本具备。微软站点上的[社区示例](https://learn.microsoft.com/en-gb/answers/questions/1156194/bluetooth-device-battery-indicator-display-in-syst)仅作为该额外兼容路径的来源，不作为官方支持保证。两类系统属性都标明“缓存、采样时间未知”；只记录本次获取时间，不设置实时采样时间。没有读取到属性不会制造 0%。

## 协议来源与许可

本次调查的上游 `main` 对应提交：[G-Helper c09017453d9ed0c52261b67a48d02bd55232e6ac](https://github.com/seerge/g-helper/commit/c09017453d9ed0c52261b67a48d02bd55232e6ac)（2026-10-21）、[OpenFlydigi 11653507bbe4dc77f3c479c2a7aaebf25bbda995](https://github.com/mkaliaha/openflydigi/commit/11653507bbe4dc77f3c479c2a7aaebf25bbda995)（2026-09-22）。以下文件链接便于阅读；复核历史实现时应以对应提交为准。

1. [G-Helper](https://github.com/seerge/g-helper)，[GPL-3.0 许可证](https://github.com/seerge/g-helper/blob/main/LICENSE)。ASUS 适配参考 [HarpeAceMini.cs](https://github.com/seerge/g-helper/blob/main/app/Peripherals/Mouse/Models/HarpeAceMini.cs) 的有线 0B05:1B63 / MI_00、64 字节包；[AsusMouse.cs](https://github.com/seerge/g-helper/blob/main/app/Peripherals/Mouse/AsusMouse.cs) 的只读 00 12 07 请求、电量字节 5、充电字节 10，以及 [WindowsUsbProvider.cs](https://github.com/seerge/g-helper/blob/main/app/AnimeMatrix/Communication/Platform/WindowsUsbProvider.cs) 的 Windows HID 读写方式。本项目没有移植任何配置写入；本项目以 GPL-3.0 发布并保留该来源。
2. [OpenFlydigi](https://github.com/mkaliaha/openflydigi)，每文件许可：[LICENSE](https://github.com/mkaliaha/openflydigi/blob/main/LICENSE) 说明 `flydigi/` 核心和 `docs/` 为 MIT，`PROTOCOL.md` 为 CC0；Qt GUI 为 GPL，与本次移植无关。参考 [device.py](https://github.com/mkaliaha/openflydigi/blob/main/flydigi/device.py) 的 32 字节 output report 03 5A A5、[motion.py](https://github.com/mkaliaha/openflydigi/blob/main/flydigi/motion.py) 的 command 1 回复和 [identity.py](https://github.com/mkaliaha/openflydigi/blob/main/flydigi/identity.py) 的型号映射、[电量说明](https://github.com/mkaliaha/openflydigi/blob/main/docs/device-settings.md)。版权：2026 Mikalai Kaliaha。这里只移植查询、最小身份校验与解码，没有 Linux hidraw 或配置写入代码。
3. 普通WOB ZEN65：从[官方支持页](https://www.wobkey.com/pages/support-for-zen-65)取得普通版驱动/固件，结合用户安装版ZEN65Device1.0.0.2、静态解密设备配置、RgDeviceManage电量函数、ARM固件AA30只读分支与ADC调度建立独立实现；[中国官网](https://www.woblab.cn/col.jsp?id=125)明确区分原生/VIA固件。没有分发或依赖官方DLL、安装器或固件。固定哈希、帧格式和字段冲突见 [ZEN65-RESEARCH.md](ZEN65-RESEARCH.md)、[ZEN65-LOCAL.md](ZEN65-LOCAL.md)。不套RT协议。官方资料没有确认开源许可，研究副本仅本机保留，不纳入发布包。

ROG 1ACE OMNI 接收器由多种鼠标共享，单看 PID 不能认定 Mini。新增证据来自 G-Helper [PeripheralsProvider.cs](https://github.com/seerge/g-helper/blob/main/app/Peripherals/PeripheralsProvider.cs)：`DedectOmniMouse` 在 MI_02 COL01 上发送只读 `01 A0 00 00`；`ResolveOmniMouse` 从回复 offset 5 起每 4 字节解码小端 PID；`MouseFromOmniPid` 将 1B65 映射到 Harpe Ace Mini。只有配对列表确认该型号，才在同容器 MI_02 COL03 上发送 `03 12 07` 读取鼠标状态。每次读取都重新核实配对身份，配对列表本身不视为在线。电量 0 且无充电标志的 OMNI 回复依上游休眠语义保留“状态未知 / 无有效本体电量”，避免把仍插着的接收器报成在线 0%。

Flydigi 同时验证精确 PID、厂商 Usage Page、MI_02、报告长度和回复型号，拒绝将充电底座当作手柄。MI_02 是依据上游 `device-settings.md` 的 USB interface 1.2 表采用的保守 Windows 路径限制，未经 Windows Apex 5 实机验证，可能漏识别其他固件布局；不会放宽后试探发包。不把“充电 sentinel=6”当作 6/5 档。高半字节 1 表示充电，低半字节仅在 0～5 时作为档位。任何未知值保留读取失败状态。

### OpenFlydigi MIT notice

Copyright (c) 2026 Mikalai Kaliaha

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## 新增适配器

1. 导出本机诊断，确认容器、接口、VID/PID、Usage、报告长度、产品/序列字符串和连接模式。
2. 找到可审查的官方文档或有许可的实机协议证据，记录精确匹配条件、包结构、回复校验、量程、充电语义与许可证；无证据不得试探发包。
3. 在 `ProtocolCatalog` 添加严格身份判断；在 `VendorBatteryQueries` 添加单次只读查询和纯解码函数。新协议必须在 helper 内执行并沿用上限；共用接收器保持相同 ChannelKey。
4. 添加纯解码 fixture 测试，覆盖坏帧、无响应、越界和充电编码；这些测试只证明解析，不证明实机通信。
5. 实机验证每种连接方式及固件：官方软件共存、外设关机但接收器存在、拔出、睡眠恢复、超时、连续刷新。通过后更新矩阵和准确结果；不能把其他型号或 Linux 通信结果视为本项目 Windows 已通过。

## 验证记录

- .NET 10.0.402 编译硬件工程成功，0 警告、0 错误。
- 2026-10-23 22:01:19（北京时间）：本机 `0B05:1ACE`、12 个 HID 接口合并为一张卡片；配对查询确认 `1B65`，显示 `ROG Harpe Ace Mini（OMNI）`。真实 HID 状态回复读取 **46%**、**2.4 GHz**、**已连接**、**未充电**。证据文件：`artifacts/actual-discovery.json`、`artifacts/rog-read-input.json`、`artifacts/rog-read-result.json`。本轮是主动查询，非模拟、非系统缓存。
- 本机另有 `37D7:6001`，总线缓存产品名 **Flydigi CD2**；上游明确 PID 高半字节 6 为充电底座。用户确认 Apex 5 当时未开机；本项目没有把底座误报为 Apex 5 在线，也没有向底座发送手柄命令。
- 普通ZEN65已由用户拔插确认2.4GHz接收器36B0:3002/serial19971217，3554:FA09属于其他设备。当前3002电量通路未打通，未套用36B0:0001原生分支。
- 用户切有线后，36B0:3025/FF60:0061/INOUT65真实AA查询成功；最终重读23:40:09返回100%、USB已连接、充电未知。证据 `research/zen65/wired-discovery.json`、`wired-read-input.json`、`wired-read-result.json`。固件ADC调度不因有线跳过，证明其为设备当前报告的ADC估算百分比；未将有歧义的状态字节0解释成未充电。
- 一次真实本机发现获得 13 个容器/实例分组，包含物理接收器和虚拟 HID。原生字段/容器归并可运行，不代表所有列出的外设都有电量支持。当前无确认的 Apex 5 实机电量结果；其 Windows 通信仍待验证。
- 不实现未知协议的 GATT 探索，不假定蓝牙设备一定有标准 Battery Service；本版本蓝牙支持为系统缓存。
- 没有 XInput 槽位到 PnP 物理设备的可靠证据，故没有启用 XInput 电量读取，避免将另一个手柄电量归给当前目标。
