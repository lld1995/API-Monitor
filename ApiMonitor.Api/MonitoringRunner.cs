using System.Net;
using System.Net.Mail;

namespace ApiMonitor.Api;

public interface IAlertChannel
{
    Task SendAsync(MonitorTask task, CheckResult result, CancellationToken ct);
}

public sealed class EmailAlertChannel(IConfiguration config) : IAlertChannel
{
    public async Task SendAsync(MonitorTask task, CheckResult result, CancellationToken ct)
    {
        if (task.Emails.Count == 0) return;
        var host = config["Smtp:Host"];
        var from = config["Smtp:From"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from)) throw new InvalidOperationException("请配置 Smtp:Host 与 Smtp:From 才能发送告警");
        using var message = new MailMessage { From = new MailAddress(from), Subject = $"[API Monitor 告警] {task.Name}", Body = $"检测任务：{task.Name}\n时间：{DateTime.UtcNow:O}\n类型：{task.Kind}\n目标：{Target(task)}\n结果：{result.Message}\n耗时：{result.DurationMs} ms" };
        foreach (var email in task.Emails) message.To.Add(email);
        using var client = new SmtpClient(host, config.GetValue("Smtp:Port", 587)) { EnableSsl = config.GetValue("Smtp:EnableSsl", true) };
        if (!string.IsNullOrWhiteSpace(config["Smtp:Username"])) client.Credentials = new NetworkCredential(config["Smtp:Username"], config["Smtp:Password"]);
        await client.SendMailAsync(message, ct);
    }

    private static string Target(MonitorTask task) => task.Kind switch
    {
        "proxy" => $"{task.ProxyUrl} → {(string.IsNullOrWhiteSpace(task.Url) ? ProxyChecker.DefaultTarget : task.Url)}",
        "db" => $"{task.DbKind}://{task.DbHost}:{task.DbPort}{(string.IsNullOrWhiteSpace(task.DbName) ? "" : "/" + task.DbName)}",
        _ => task.Url
    };
}

public sealed class MonitorRunner(MonitorStore store, IServiceScopeFactory scopes, IEnumerable<IAlertChannel> alerts, ILogger<MonitorRunner> logger)
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
            store.Complete(task, result, DateTime.UtcNow);
            if (!result.Success && !task.AlertActive)
                foreach (var channel in alerts)
                    try { await channel.SendAsync(task, result, ct); }
                    catch (Exception ex) { logger.LogError(ex, "监控任务 {TaskId} 告警发送失败", id); }
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
