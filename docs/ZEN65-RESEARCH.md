> 历史记录说明：原始下载、探测记录及临时证据已归档至本地 .archive，不提交仓库。当前支持范围见 COMPATIBILITY.md。

# 普通 WOB ZEN65：2.4 GHz 电量协议调查

调查对象是普通 ZEN65，用户确认当前采用 2.4 GHz；不使用 ZEN65 RT 协议。研究离线副本只用于核实只读查询，未刷新固件、改变键位、灯光、工作模式或无线配对。

## 官方资料与本地证据

- [官方普通 ZEN65 支持页](https://www.wobkey.com/pages/support-for-zen-65)直接链接 `ZEN65_Driver.zip`、`ZEN_65_RGB_Firmware.zip`、`ZEN_65_RGB.zip`。包已下载到 `research/zen65/`，未执行其中安装程序。下载清单与 SHA256 在同目录 `sources.json`、`hashes.json`。
- [官方 Driver/Firmware Wiki](https://wiki.wobkey.com/en/Products/ZEN-65/Driver-Firmware)另外提供现版普通 RGB/NORGB VIA JSON 与 BIN；实际下载 RGB JSON 与 RGB BIN（72,848 字节）作静态核对。旧版 ZIP 中 RGB BIN 为 65,372 字节。版本不同，不能互换为设备实际固件，更没有向设备写入。
- 官方普通 RGB VIA JSON 的有线标识是 **36B0:3025**；VIA 页面要求有线连接。它不能直接说明 2.4 GHz 接收器的电量查询，也不代表 RT 型号。
- 用户当前正在使用的官方管理程序是 `C:\apps\zen65\ZEN65Device.exe`，卸载信息版本 `1.0.0.2`。这是 Qt5 原生程序，包含 `RgDeviceManage.dll` 和 `hidapi.dll`；仅分析离线 DLL 副本，原安装文件保留。
- 设备枚举得到候选 **3554:FA09 / MI_01&COL01 / Usage Page FF02 / Usage 0002 / Input 20 / Output 20**，以及 **36B0:3002 / MI_02&COL05 / Usage Page FF00 / Usage 0001 / Input 64 / Output 64**。后者还有 `MI_01 / FF60:0061 / Input 33 / Output 33` 的 VIA 接口。它们不是同一种传输，设备型号归属必须复核，不能把早期候选当作已证实的普通 ZEN65。
- DLL 导出符号包括 `RgHidDevice::HidRead24GPowerInfo`、`On24GDevicePowerTime`、`sgnDevice24GPowerInfo(HID_24G_POWER_INFO)`；UI 模块包含 `RgDeviceUI::set24GPowerData` 与 `setPowerTextInfo`，说明普通 ZEN65 官方软件存在专门的 2.4 GHz 电量路径。

第三方同平台协议只作为交叉核查线索，不能单凭相同 VID/PID 向普通 ZEN65 发送命令；实际发包前还必须通过本机普通 ZEN65 官方驱动函数确认请求、接口与字段语义。

## 边界与复核方法

1. 先定位官方 DLL 的精确函数与报告写入路径，区分状态查询和配置写入。
2. 沿官方 UI 解码逻辑确认百分比、充电位、在线位，未知标志保留原始诊断，绝不把整个标志字节转换成充电 bool。
3. 仅对本机已确认的普通 ZEN65 接收器、精确接口、精确报告长度启用适配；沿用独立 helper、共享访问、超时终止与通道串行。
4. 用官方软件显示和纯解码 fixture 分别交叉核查。只有收到有效实时回复才更新本体在线证据及成功读取时间。

## 许可与交付

官方驱动、固件和页面没有提供可确认的开源代码许可。下载副本、DLL、反汇编研究材料保留在本机 `research/zen65/`，不作为项目开源代码或发布安装包的一部分分发。可公开的项目适配只描述和独立实现已核实的协议事实，不嵌入官方 DLL，不依赖安装官方程序运行。

## 当前结果

官方驱动已完成静态复核，详见 [本地驱动分析](ZEN65-LOCAL.md)：`HidRead24GPowerInfo`使用command **0x30**，逻辑帧64字节、0xAA起始和16位求和校验。回调请求10字节，前两项是电量和状态。官方UI将状态0/1/2映射为未充电/充电中/充满，但后续固件检查发现该回复字节取自低电标志RAM，与真正充电状态RAM不同，存在语义冲突。因此产品充电一律标为**未知**，只保留状态原字节诊断，不照搬UI映射。

官方加密设备配置经离线静态解密确认有线 **36B0:3025**、原生驱动2.4GHz **36B0:0001**，后者使用 **FF87:0020** 和 **FF88:0021**。用户拔插已确认当前无线接收器是36B0:3002，其FF00/Report3F通路与此配置不一致，早期候选3554:FA09已排除。因此没有向3002或3554发送原生AA30，也没有用其他键盘的4A查询试探；有线3025的独立成功验证见下。

[WOB 中国官网普通 ZEN65 下载页](https://www.woblab.cn/col.jsp?id=125)明确区分“RGB 自研驱动固件”（63.84 KB）与“RGB-VIA 固件”（71.14 KB）。国际官网旧包和当前 Wiki 的 BIN 长度分别为 65,372 和 72,848 字节，与此两分支对应；静态可见蓝牙字符串 `ZEN 65 RGB-1/2/3`，VIA BIN 的 USB 设备描述符包含 36B0:3025。固件分支差异可能解释原生驱动与当前接收器接口不吻合，但尚不能仅凭此推断 AA30 在 VIA 分支可用。继续研究 VIA 分支的确凿只读状态入口。

当前有线原生分支的真实成功读数和边界见下；2.4GHz3002仍未读通，静态发现原生0001协议不代表当前接收器已支持。

## 官方网页驱动静态分析

WOB 中国官网导航链接 [wobwxe.com](https://www.wobwxe.com)。该站 TLS 证书已过期；仅为本机离线研究保存公共 HTML 和脚本，没有执行网页、授予 WebHID 权限、登录或发送设备命令。文件位于 `research/zen65/web-driver.html`、`web-js/`，不随发布包分发。

webpack 映射将普通 `./Zen65.js` 指向 module **16383**，与 `./Zen65RT.js` 的 **94873** 分开；普通模块在 `app~12245e1e.480c7eb9.js`。它明确使用 **36B0:3025 / FF60:0061**，提供 0xAA 格式的按键、灯光、宏读取，未导出电量读取方法。其他脚本中的 `getBatteryInfo` 属于 `Rainy98_2.4G`，不会移植到普通 ZEN65；RT 的地址读写协议也未使用。

网页缺少电量功能只说明该前端没有入口，不能证明当前固件一定没有只读电量指令，因此仍需结合对应固件分支的静态处理器分析。

## 额外的真实只读验证

在独立进程中读取候选接收器的 `HidP_GetValueCaps` 元数据，不读取用户输入内容。`36B0:3002 / FF00:0001` 实际是 **Report ID 3F、63 字节 payload**；`FF60:0061` 为 **Report ID 00、32 字节 payload**。这些精确结构进一步说明不能将原生驱动的 64 字节 AA 逻辑帧直接填入当前厂商接口。

2026-10-23 23:18:53（北京时间），仅向精确匹配 VID/PID、Usage、IN/OUT 33 字节的 VIA 接口发送 [QMK 官方定义](https://github.com/qmk/qmk_firmware/blob/master/quantum/via.h) 的固定只读 `id_get_protocol_version = 0x01` 请求。单次 I/O 1.2 秒后取消，外层独立进程限制 5 秒并已退出。没有得到有效回复，没有生成电量或在线结果；没有发送 AA30、4A、BB 转发或任何设置命令。

该结果与官方文档“VIA 需要有线连接”相符，但一次未回复不被当作永远不支持的证明。复现与证据：`research/zen65/dump-valuecaps.ps1`、`actual-valuecaps.json`、`query-via-version.ps1`、`actual-via-version.json`。

用户随后通过拔除/插回接收器确认：普通 ZEN65 的接收器确为 **36B0:3002 / serial 19971217**，3554:FA09 属于另一设备。用户插回并唤醒后重新枚举，23:27:29 成功以 GenericRead 只读句柄观察 FF00:0001 厂商状态通道 1.2 秒，收到 0 个报告，未发送任何命令。此前一次使用过期路径得到 Win32 2，已明确区分，未当作“无电量”。证据 `current-discovery.json`、`actual-passive-status.json`；有限无报告不被扩展成永远无支持。

## 有线原生通路的真实交互

用户于 2026-10-23 将键盘接入 USB 数据线并切为有线。重新枚举到 **ZEN 65 RGB / 36B0:3025 / MI_01 / FF60:0061 / IN 65 / OUT 65**，与普通 ZEN65 官方原生逻辑帧吻合；这与上文仅由下载 VIA BIN推测当前固件的假设不同，实际描述符应优先。

以真实元数据中唯一的输入/输出Report ID为前缀，执行官方已核实只读事务 **AA10→AA30（len10）→AA11**。首次23:33:20得到有效魔数、命令回显与55成功状态，返回百分比100、原始状态字节00。按上述字段冲突修正后，最终23:40:09再次真实读取 **100%、USB已连接、充电未知**；原字节00保留诊断，不当作未充电。最终证据在 `wired-read-result.json`，完整设备标识在 `wired-read-input.json`。

已核实原生固件真实ADC上游和有线调度：ADC采样换算并限制为0～100；主循环定时路径无条件调用采样，在其后才处理USB查询，因此有线没有跳过电量更新。该100%可作为设备当前报告的ADC估算百分比，不能解释成实验室物理容量测量。无线3002的不同Report3F/payload63通路尚未接入AA查询，不把有线成功扩展成2.4GHz已支持。

实现 `WobNativeBatteryQuery` 只启用官方身份匹配和完整 65 字节格式；拒绝 VIA 33 字节、3002 的 3F+63 字节及 3554 的 20 字节通路。AA10 发送尝试也处于 try/finally 内：即使开始回复丢失，仍有界尝试 AA11，保留原始错误并单独记录清理失败。未能完成清理不会发布成功读数。
