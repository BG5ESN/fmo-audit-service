namespace EmqxMonitor;

/// <summary>OTA 更新端点（check / apply 启动后台任务 / progress 前端轮询）</summary>
public static class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this WebApplication app)
    {
        // ---- 版本与更新（OTA）----

        // GET /api/update/check — 检查更新（当前/最新/模式）
        app.MapGet("/api/update/check", async () =>
        {
            var mode = UpdateService.DetectMode();
            var (cur, latest, has, err) = await UpdateService.CheckAsync();
            return Results.Json(new
            {
                ok = true,
                current = cur,
                latest,
                has_update = has,
                update_mode = UpdateService.ModeName(mode),
                docker_hint = mode == UpdateMode.Docker
                    ? "当前为 Docker 部署，不支持自更新。请使用: docker pull 新镜像 && docker compose up -d"
                    : null,
                error = err,
            });
        });

        // POST /api/update/apply — 启动更新（立即返回，进度由 /api/update/progress 轮询；成功后以非 0 退出码退出触发自动重启）
        app.MapPost("/api/update/apply", (UpdateProgressTracker tracker) =>
        {
            if (UpdateService.DetectMode() == UpdateMode.Docker)
                return Results.Json(new { ok = false, error = "容器内不支持自更新，请使用 docker pull 更新镜像" });

            // 后台执行下载+替换，进度写入 tracker 供前端轮询；退出码非 0 触发 systemd/计划任务自动重启
            tracker.Reset();
            _ = Task.Run(async () =>
            {
                var (err, msg, replaced) = await UpdateService.ApplyAsync(p => tracker.Report(p));
                if (err != null)
                {
                    tracker.Report(new UpdateProgress { Stage = "error", Message = err });
                }
                else if (!replaced)
                {
                    // 已是最新版本 → 不退出
                    tracker.Report(new UpdateProgress { Stage = "done", Message = msg });
                }
                else
                {
                    // 替换脚本已就绪（ApplyAsync 内部已报 ready），延迟退出让脚本覆盖二进制。
                    // 退出码分平台：Windows 脚本会主动 schtasks /run 重启任务，这里正常退出（0），
                    // 避免 1 分钟后 RestartCount 又拉起一个实例抢端口；Linux 非 0 触发 systemd Restart=on-failure
                    await Task.Delay(1500);
                    Environment.Exit(OperatingSystem.IsWindows() ? 0 : 1);
                }
            });
            return Results.Json(new { ok = true, started = true });
        });

        // GET /api/update/progress — 查询更新进度（网页 300ms 轮询）
        app.MapGet("/api/update/progress", (UpdateProgressTracker tracker) =>
        {
            var p = tracker.Snapshot();
            return Results.Json(new
            {
                ok = true,
                stage = p.Stage,
                percent = p.Percent,
                bytes_read = p.BytesRead,
                total_bytes = p.TotalBytes,
                message = p.Message,
            });
        });
    }
}
