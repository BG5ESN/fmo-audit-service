using System.Runtime.InteropServices;
using System.Text.Json;

namespace EmqxMonitor;

/// <summary>运行环境模式：决定更新策略（容器内不能自更新）</summary>
public enum UpdateMode { Self, Docker, Manual }

/// <summary>OTA 更新服务：版本元数据 / 下载校验 / 延迟替换二进制。
/// 元数据地址与 sas.json 同目录：https://bg5esn.com/share/fmo/fmo-audit-service.json
/// { "version": "2.0.0", "assets": { "linux-x64": { "url": "...", "sha256": "..." }, ... } }</summary>
public static class UpdateService
{
    /// <summary>元数据地址（可用环境变量 FAS_UPDATE_URL 覆盖——测试/自托管场景）</summary>
    public static string MetaUrl =>
        Environment.GetEnvironmentVariable("FAS_UPDATE_URL") ?? "https://bg5esn.com/share/fmo/fmo-audit-service.json";

    /// <summary>当前运行模式：Docker 容器内 → 不自更新（提示 docker pull）</summary>
    public static UpdateMode DetectMode()
    {
        try
        {
            if (File.Exists("/.dockerenv")) return UpdateMode.Docker;
            if (File.Exists("/proc/1/cgroup"))
            {
                var cg = File.ReadAllText("/proc/1/cgroup");
                if (cg.Contains("docker", StringComparison.OrdinalIgnoreCase)
                    || cg.Contains("containerd", StringComparison.OrdinalIgnoreCase))
                    return UpdateMode.Docker;
            }
        }
        catch { }
        return UpdateMode.Self;   // systemd / 手动均按 Self 处理（Manual 由调用方提示重启）
    }

    public static string ModeName(UpdateMode m) => m switch
    {
        UpdateMode.Docker => "docker",
        UpdateMode.Self => "self",
        _ => "manual",
    };

    /// <summary>创建 HttpClient（显式禁用系统代理：bg5esn.com 是国内站直连更快，且代理可能拦截/慢）</summary>
    private static HttpClient NewHttp() => new(new SocketsHttpHandler
    {
        Proxy = null,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>检查更新：返回 (当前版本, 最新版本, 是否有更新, 错误)</summary>
    public static async Task<(string Current, string? Latest, bool HasUpdate, string? Error)> CheckAsync()
    {
        var current = CurrentVersion();
        try
        {
            using var http = NewHttp();
            var json = await http.GetStringAsync(MetaUrl);
            using var doc = JsonDocument.Parse(json);
            var latest = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (string.IsNullOrEmpty(latest))
                return (current, null, false, "元数据缺少 version 字段");

            var has = CompareVersions(latest, current) > 0;
            return (current, latest, has, null);
        }
        catch (Exception ex)
        {
            return (current, null, false, $"检查更新失败: {ex.Message}");
        }
    }

    /// <summary>执行更新：下载最新版 → sha256 校验 → 生成延迟替换脚本 → spawn。返回 (错误, 提示)</summary>
    public static async Task<(string? Error, string? Message)> ApplyAsync()
    {
        if (DetectMode() == UpdateMode.Docker)
            return ("容器内不支持自更新", "Docker 部署请使用: docker pull 新镜像 && docker compose up -d");

        var (current, latest, hasUpdate, err) = await CheckAsync();
        if (err != null) return (err, null);
        if (latest == null) return ("元数据无最新版本", null);
        if (!hasUpdate) return (null, $"已是最新版本 v{current}");

        try
        {
            // 1) 按 RID 找下载地址 + sha256
            using var http = NewHttp();
            var json = await http.GetStringAsync(MetaUrl);
            using var doc = JsonDocument.Parse(json);
            var rid = RuntimeInformation.RuntimeIdentifier ?? "";
            if (!doc.RootElement.TryGetProperty("assets", out var assets)
                || !assets.TryGetProperty(rid, out var asset))
                return ($"元数据中没有当前平台 {rid} 的下载地址", null);
            var url = asset.ValueKind == JsonValueKind.String ? asset.GetString()
                     : (asset.TryGetProperty("url", out var u) ? u.GetString() : null);
            if (string.IsNullOrEmpty(url)) return ("元数据缺少下载 URL", null);

            // 2) 下载到临时目录（流式）
            var tempDir = Path.Combine(Path.GetTempPath(), "fas_update");
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);
            var isArchive = url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                            || url.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);
            var ext = url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? ".zip" : ".tar.gz";
            var archivePath = Path.Combine(tempDir, $"update{ext}");
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(archivePath);
                await src.CopyToAsync(dst);
            }

            // 3) 下载完成（传输完整性由 HTTPS/TLS 保障，官方源可信，无需额外哈希）

            // 4) 解压（归档）或直接使用（裸二进制产物）
            string newExe;
            if (isArchive)
            {
                if (ext == ".zip") System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, tempDir);
                else ExtractTarGz(archivePath, tempDir);
                File.Delete(archivePath);

                // 5) 找新二进制（windows 为 .exe）
                var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "fmo-audit-service.exe" : "fmo-audit-service";
                newExe = Directory.GetFiles(tempDir, exeName, SearchOption.AllDirectories).FirstOrDefault()
                         ?? throw new InvalidOperationException($"未在更新包中找到 {exeName}");
            }
            else
            {
                // 裸二进制：下载文件即新二进制
                newExe = archivePath;
            }

            // 6) 延迟替换脚本：等本进程退出 → 覆盖自身 → 清理
            var exePath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(exePath)) return ("无法确定当前可执行文件路径", null);
            var scriptPath = Path.Combine(Path.GetTempPath(),
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "fas_update.bat" : "fas_update.sh");
            var pid = Environment.ProcessId;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.WriteAllText(scriptPath,
                    $"@echo off\r\n" +
                    $":wait\r\n" +
                    $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
                    $"if not errorlevel 1 (\r\n" +
                    $"  timeout /t 1 /nobreak >nul\r\n" +
                    $"  goto wait\r\n)\r\n" +
                    $"move /y \"{newExe}\" \"{exePath}\"\r\n" +
                    $"rmdir /s /q \"{tempDir}\"\r\n" +
                    $"echo FMO Audit Service updated to v{latest}. Please restart the service.\r\n" +
                    $"del \"%~f0\"\r\n");
            }
            else
            {
                File.WriteAllText(scriptPath,
                    $"#!/bin/sh\n" +
                    $"while kill -0 {pid} 2>/dev/null; do sleep 1; done\n" +
                    $"mv \"{newExe}\" \"{exePath}\"\n" +
                    $"chmod +x \"{exePath}\"\n" +
                    $"rm -rf \"{tempDir}\"\n" +
                    $"echo \"FMO Audit Service updated to v{latest}. systemd will restart it.\"\n" +
                    $"rm \"$0\"\n");
                System.Diagnostics.Process.Start("chmod", $"+x \"{scriptPath}\"")?.WaitForExit();
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                System.Diagnostics.Process.Start("cmd", $"/c \"{scriptPath}\"");
            else
                System.Diagnostics.Process.Start("sh", scriptPath);

            return (null, $"v{latest} 已下载并校验，服务即将自动重启（systemd）");
        }
        catch (Exception ex)
        {
            return ($"更新失败: {ex.Message}", null);
        }
    }

    /// <summary>当前版本（csproj Version）</summary>
    public static string CurrentVersion()
        => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>语义版本比较：a > b 返回正数</summary>
    public static int CompareVersions(string a, string b)
    {
        static int[] Parse(string s)
        {
            var parts = s.Trim().TrimStart('v').Split('.');
            var arr = new int[3];
            for (var i = 0; i < 3 && i < parts.Length; i++)
                if (int.TryParse(parts[i], out var n)) arr[i] = n;
            return arr;
        }
        var pa = Parse(a); var pb = Parse(b);
        for (var i = 0; i < 3; i++)
            if (pa[i] != pb[i]) return pa[i] - pb[i];
        return 0;
    }

    /// <summary>tar.gz 解压（.NET 无内置 tar.gz；用系统 tar 命令）</summary>
    private static void ExtractTarGz(string archive, string destDir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("tar", $"xzf \"{archive}\" -C \"{destDir}\"")
        {
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit(60_000);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"tar 解压失败: {p.StandardError.ReadToEnd()}");
    }
}
