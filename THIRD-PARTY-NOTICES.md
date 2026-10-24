# 来源与许可证

本项目按 GNU GPL version 3 发布（见 LICENSE）。界面与应用结构为本项目实现。未捆绑第三方驱动、厂商 SDK 或后台服务；随发布包含的 Microsoft .NET 运行时保留其发行目录中的 LICENSE.txt、ThirdPartyNotices.txt。

## G-Helper

- 来源：https://github.com/seerge/g-helper
- 许可证：GPL-3.0，https://github.com/seerge/g-helper/blob/main/LICENSE
- 用途：核实 ASUS Harpe Ace Mini 有线和 ROG OMNI 接口标识、只读电量查询及配对设备列表协议。本项目的 ASUS 状态查询参考此实现，整体采用 GPL-3.0 保持许可相容。
- 具体文件、字段依据和验证限制：docs/HARDWARE.md。

## OpenFlydigi

- 来源：https://github.com/mkaliaha/openflydigi
- 上游 backend 与 docs：MIT；协议说明（PROTOCOL.md）：CC0-1.0。保留以项目实际文件许可为准的来源说明。
- 用途：Apex 5 的精确身份、read_info 请求帧、回复型号、连接类型、电量档位和充电 nibble。未移植灯光、映射、固件或模式修改命令。

MIT License

Copyright (c) 2026 Mikalai Kaliaha

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## 其他参考资料

- WOB ZEN65 官方指南：https://wiki.wobkey.com/en/Products/ZEN-65/Quick-Start 。仅用于核实产品和连接方式；没有把 ZEN65 RT 的协议套用于普通 ZEN65。
- Microsoft Win32 / Windows 设备属性文档：SetTimer、Shell_NotifyIconW、SetupAPI、HID、ConfigManager。调用系统 API 不包含厂商驱动代码。
