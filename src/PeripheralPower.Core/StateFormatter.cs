using System.Globalization;

namespace PeripheralPower.Core;

public static class StateFormatter
{
    public static bool ShowDeviceTrayIcon(DeviceSnapshot state) => !state.Hidden && state.Connection==ConnectionState.Connected
        && state.Battery.IsValid && (state.LastValidBatteryAt.HasValue || state.BatteryObservedAt.HasValue);
    public static string Battery(DeviceSnapshot state)
    {
        var battery = state.Battery;
        var value = battery.IsValid ? battery.Kind switch
        {
            BatteryKind.Percentage => $"{(battery.IsApproximate ? "约 " : "")}{battery.Value}%",
            BatteryKind.Level => $"{battery.Value}/{battery.Maximum} 档",
            _ => "电量未知"
        } : battery.Kind == BatteryKind.Unsupported ? "电量暂不支持" : "电量未知";
        return battery.IsValid && state.IsHistorical ? $"上次电量 {value}" : value;
    }

    public static string Time(DeviceSnapshot state, DateTimeOffset now)
    {
        if (!state.Battery.IsValid && !state.Earbuds.IsValid) return "尚无有效读数";
        if (state.IsSystemCache)
        {
            return "系统缓存 · 采样时间未知";
        }
        return state.LastValidBatteryAt is { } time ? $"{Timestamp(time, now)} 更新" : "采样时间未知";
    }

    public static string Timestamp(DateTimeOffset time, DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(time, TimeZoneInfo.Local);
        var localNow = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local);
        return local.ToString(local.Date == localNow.Date ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    public static string TrayLine(DeviceSnapshot state, DateTimeOffset now) =>
        $"{state.DisplayName}：{Battery(state)}{(state.Earbuds.IsValid ? "；" + state.Earbuds.Text : "")}（{Time(state, now)}）";

    public static string TraySummary(IEnumerable<DeviceSnapshot> states, DateTimeOffset now)
    {
        var lines = states.Where(x => !x.Hidden).OrderBy(x => x.Order).Select(x => TrayLine(x, now)).ToArray();
        return lines.Length == 0 ? "外设电量：尚未添加设备" : string.Join(Environment.NewLine, lines);
    }

    // NOTIFYICONDATA.szTip has 128 UTF-16 code units including the terminator.
    public static string NativeTrayText(string summary)
    {
        if (summary.Length <= 127) return summary;
        var count = char.IsHighSurrogate(summary[125]) ? 125 : 126;
        return summary[..count] + "…";
    }

    public static string Connection(ConnectionState value) => value switch
    {
        ConnectionState.Connected => "已连接", ConnectionState.Disconnected => "已断开", _ => "连接状态未知"
    };
    public static string Transport(ConnectionTransport value) => value switch
    {
        ConnectionTransport.UsbWired => "USB 有线", ConnectionTransport.Receiver24Ghz => "2.4 GHz 接收器",
        ConnectionTransport.Bluetooth => "蓝牙", _ => "连接方式未知"
    };
    public static string Charging(ChargingState value) => value switch
    {
        ChargingState.Charging => "正在充电", ChargingState.NotCharging => "未充电", ChargingState.Full => "充电完成", _ => "充电状态未知"
    };
    public static string Status(ReadStatus value) => value switch
    {
        ReadStatus.Reading => "读取中", ReadStatus.Success => "正常", ReadStatus.Timeout => "读取超时",
        ReadStatus.Failed => "读取失败", ReadStatus.Unsupported => "暂不支持", ReadStatus.Cancelled => "已取消", _ => "等待刷新"
    };
}
