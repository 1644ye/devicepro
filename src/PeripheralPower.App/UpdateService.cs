using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace PeripheralPower.App;

public static class UpdateService
{
    static bool busy;
    public static async Task CheckAsync(AppController controller, bool manual = false)
    {
        if (busy || controller.IsExiting) return;
        busy = true;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PeripheralPower-Updater/1.2");
            using var json = JsonDocument.Parse(await client.GetStringAsync("https://api.github.com/repos/1644ye/devicepro/releases/latest"));
            var release = json.RootElement;
            var tag = release.GetProperty("tag_name").GetString()!;
            if (!Version.TryParse(tag.TrimStart('v'), out var version)) throw new IOException("发布版本号无效");
            if (version <= Assembly.GetExecutingAssembly().GetName().Version)
            {
                if (manual) MessageBox.Show("当前已是最新版本。", "软件更新");
                return;
            }
            var asset = release.GetProperty("assets").EnumerateArray().First(a => a.GetProperty("name").GetString() == "PeripheralPower-win-x64.zip");
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (url.Scheme != "https" || url.Host != "github.com" || !url.AbsolutePath.StartsWith("/1644ye/devicepro/releases/download/", StringComparison.Ordinal)) throw new IOException("更新地址不属于本项目");
            if (MessageBox.Show($"发现新版本 {tag}，是否下载并重启安装？设备设置将保留。", "软件更新", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var staging = Path.Combine(Path.GetTempPath(), "PeripheralPower-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var zip = Path.Combine(staging, "update.zip");
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var output = File.Create(zip);
                await response.Content.CopyToAsync(output);
            }
            var payload = Path.Combine(staging, "payload");
            ZipFile.ExtractToDirectory(zip, payload);
            if (!File.Exists(Path.Combine(payload, "PeripheralPower.exe")) || !File.Exists(Path.Combine(payload, "PeripheralPower.dll"))) throw new IOException("更新包缺少程序文件");
            string Quote(string s) => "'" + s.Replace("'", "''") + "'";
            var script = Path.Combine(staging, "install.ps1");
            await File.WriteAllTextAsync(script, $$"""
                $ErrorActionPreference = 'Stop'
                $destination = {{Quote(AppContext.BaseDirectory)}}
                $payload = {{Quote(payload)}}
                $backup = {{Quote(Path.Combine(staging, "backup"))}}
                $log = {{Quote(Path.Combine(controller.DataDirectory, "update-error.txt"))}}
                Wait-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
                try {
                    New-Item -ItemType Directory -Path $backup | Out-Null
                    Get-ChildItem -LiteralPath $payload -File | ForEach-Object {
                        $old = Join-Path $destination $_.Name
                        if (Test-Path -LiteralPath $old) { Copy-Item -LiteralPath $old -Destination $backup }
                    }
                    Get-ChildItem -LiteralPath $payload -File | Copy-Item -Destination $destination -Force
                } catch {
                    $_ | Out-String | Set-Content -LiteralPath $log
                    Get-ChildItem -LiteralPath $backup -File | Copy-Item -Destination $destination -Force
                }
                Start-Process -FilePath (Join-Path $destination 'PeripheralPower.exe') -ArgumentList '--tray' -WindowStyle Hidden
                """);
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"") { UseShellExecute = false, CreateNoWindow = true });
            await controller.ExitAsync();
        }
        catch (Exception ex)
        {
            new Core.RollingLog().Write("update", ex.Message);
            if (manual) MessageBox.Show("更新未完成：" + ex.Message, "软件更新");
        }
        finally { busy = false; }
    }
}
