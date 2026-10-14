using System.Text;

namespace ApiMonitor.Api;

public sealed class MonitorRunner(MonitorStore store, IServiceScopeFactory scopes, NotificationService notifications, ILogger<MonitorRunner> logger)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> running = new();

    public async Task<CheckResult?> RunAsync(long id, CancellationToken ct)
    {
        if (!running.TryAdd(id, 0)) return null;
        store.SetRunning(id, true);
        try
        {
            var task = store.Get(id);
            if (task is null) return null;
            CheckResult result;
            try
            {
                using var scope = scopes.CreateScope();
                if (task.Kind == "http") result = await scope.ServiceProvider.GetRequiredService<HttpUrlChecker>().CheckAsync(task.Url, task.UseProxy, ct);
else if (task.Kind == "proxy")
                {
                    var proxyPassword = task.EncryptedProxyPassword.Length > 0 ? store.Decrypt(task.EncryptedProxyPassword) : "";
                    var targetUrl = string.IsNullOrWhiteSpace(task.Url) || string.Equals(task.Url.TrimEnd('/'), task.ProxyUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ? ProxyChecker.DefaultTarget : task.Url;
                    result = await scope.ServiceProvider.GetRequiredService<ProxyChecker>().CheckAsync(task.ProxyUrl, targetUrl, task.ProxyUsername, proxyPassword, task.ProxyProtocol, ct);
                }
                else if (task.Kind == "db")
                {
                    var dbKind = Enum.TryParse<DbKind>(task.DbKind, true, out var parsed) ? parsed : DbKind.MySql;
                    var password = task.EncryptedDbPassword.Length > 0 ? store.Decrypt(task.EncryptedDbPassword) : "";
                    result = await scope.ServiceProvider.GetRequiredService<DatabaseChecker>().CheckAsync(dbKind, task.DbHost, task.DbPort, task.DbName, task.DbUser, password, ct);
                }
                else
                {
                    var checker = scope.ServiceProvider.GetRequiredService<IEnumerable<IProviderChecker>>().Single(x => x.Provider == task.Provider);
                    result = await checker.CheckAsync(store.Decrypt(task.EncryptedKey), task.Url, task.Protocol, task.Model, task.UseProxy, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "监控任务 {TaskId} 检测异常", id); result = new(false, "检测失败：" + ex.Message, 0); }
            var wasFailing = task.AlertActive;
            store.Complete(task, result, DateTime.UtcNow);
            // 仅在状态翻转（正常→异常、异常→正常）且勾选了通知渠道时，按渠道分发通知。
            var nowFailing = !result.Success;
            if (nowFailing != wasFailing && task.NotifyChannelIds.Count > 0)
                await notifications.SendAsync(task, result, recovered: !nowFailing, ct);
            return result;
        }
        finally { store.SetRunning(id, false); running.TryRemove(id, out _); }
    }
}

public sealed class MonitorScheduler(MonitorStore store, MonitorRunner runner, ILogger<MonitorScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            do
            {
                foreach (var task in store.ClaimDue(DateTime.UtcNow))
                {
                    try { await runner.RunAsync(task.Id, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception ex) { logger.LogError(ex, "监控任务 {TaskId} 执行失败", task.Id); }
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
