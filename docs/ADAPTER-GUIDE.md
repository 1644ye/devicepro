# 新增设备适配

先在目标连接模式下导出诊断，保存 VID/PID、Usage Page/Usage、MI/COL、报告长度、产品描述、Container ID、实例父链及固件/协议版本。不同模式分别取证；名字相似不构成协议匹配依据。

1. 在 `ProtocolCatalog` 添加精确接口匹配。共享接收器先用有来源的只读身份查询确认具体型号。收到其他型号、未知帧或改变的报告长度时拒绝发送后续命令。
2. 在 Hardware 实现状态查询和独立纯函数解码器，或新增适配类。所有设备 API 仅在 `ProbeRunner` 隔离进程内执行；不要在 UI、Win32 消息回调或托盘悬停里调用。
3. 只允许有依据的状态查询。每次使用共享、可取消的异步句柄，并验证当前会话 VID/PID/Usage。明确报告长度、响应帧、型号、请求类型、取值范围和充电字段；不能拿编码哨兵值换算电量。
4. 使用 `DeviceReadResult` 分别返回 Battery、Connection、Transport、Charging、Status、Source 和 Error。成功非缓存查询设置 `BatterySampledAt`/`BatteryObservedAt`；系统缓存只设置观察时间和 `IsSystemCache=true`，实际采样未知。协议未提供充电则保持 Unknown。
5. 未适配模式使用 Windows 属性兜底或 Unsupported。接收器不回复时超时/未知，不把接收器存在当作本体在线。控制器槽位不作为身份。
6. 将通信通道填写到 `DeviceDescriptor.ChannelKey`，共用接收器要使用同一个键。所有窗口、事件、周期和手动操作都调用 `RefreshCoordinator.RequestRefresh`，不建立第二套调度。
7. 为解码器添加正常、截断、错误型号、错误范围、充电编码和断连 fixture。仅测试入口可返回模拟值；正式应用不能默认展示 fixture。
8. 用实机分别验证有线、2.4G、蓝牙、关机、拔出、恢复、官方软件占用、超时和固件版本。更新兼容性表，未验证项明确标注“待验证”。记录上游文件链接、许可与所参考协议字节来源。

`IsolatedHardwareBackend` 限制最多 3 个探测子进程，并使用固定数量的通道闸门。设备同步元数据 API 不能可靠取消时，父进程在超时/取消后终止整个探测进程并等退出；若无法终止则停用后续硬件查询直到重启，避免不断生成失控线程。不要移除这条边界，也不要仅对底层阻塞任务使用 `Task.WhenAny`。

Core 刷新协调器是单个活动批次加最多一个待处理请求。多个目标独立更新，共用身份只读一次，共用通道串行，session generation 不匹配的迟到结果会被丢弃。没有 Sleep/Delay 刷新循环；周期由独立 Win32 宿主 `WM_TIMER` 提交。
