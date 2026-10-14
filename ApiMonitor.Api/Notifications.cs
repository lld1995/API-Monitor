using System.Data.Common;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace ApiMonitor.Api;

public sealed class NotifyChannel
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>email 或 wanjitong。</summary>
    public string Type { get; set; } = "email";
    /// <summary>邮箱地址明文；万记通为加密后的 API Key。</summary>
    public string Target { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>按用户维护的通知渠道（邮箱 / 万记通），监控任务通过勾选渠道 ID 关联。</summary>
public sealed class NotifyChannelStore(Db db, MonitorStore store, ILogger<NotifyChannelStore> logger)
{
    private readonly object gate = new();
    public static readonly string[] Types = ["email", "wanjitong"];

    public void Initialize()
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS notify_channels (
                    {db.IdColumn},
                    user_id {(db.IsMySql ? "BIGINT" : "INTEGER")} NOT NULL,
                    name {(db.IsMySql ? "VARCHAR(100)" : "TEXT")} NOT NULL,
                    type {(db.IsMySql ? "VARCHAR(16)" : "TEXT")} NOT NULL,
                    target {db.TextType} NOT NULL,
                    created_at {(db.IsMySql ? "VARCHAR(64)" : "TEXT")} NOT NULL
                )
                """;
            cmd.ExecuteNonQuery();
            db.TryCreateIndex(connection, "idx_channels_user", "notify_channels", "user_id");
        }
    }

    public IReadOnlyList<NotifyChannel> List(long userId)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, name, type, target, created_at FROM notify_channels WHERE user_id = @user ORDER BY id DESC";
            Add(cmd, "@user", userId);
            using var reader = cmd.ExecuteReader();
            var items = new List<NotifyChannel>();
            while (reader.Read()) items.Add(Read(reader, userId));
            return items;
        }
    }

    public NotifyChannel? Get(long userId, long id)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT id, name, type, target, created_at FROM notify_channels WHERE id = @id AND user_id = @user";
            Add(cmd, "@id", id); Add(cmd, "@user", userId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? Read(reader, userId) : null;
        }
    }

    public NotifyChannel Create(NotifyChannel channel)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO notify_channels(user_id, name, type, target, created_at) VALUES (@user, @name, @type, @target, @at)";
            Add(cmd, "@user", channel.UserId); Add(cmd, "@name", channel.Name); Add(cmd, "@type", channel.Type);
            Add(cmd, "@target", channel.Target); Add(cmd, "@at", channel.CreatedAt.ToString("O"));
            cmd.ExecuteNonQuery();
            channel.Id = db.LastInsertId(connection);
            return channel;
        }
    }

    public bool Update(NotifyChannel channel)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE notify_channels SET name = @name, type = @type, target = @target WHERE id = @id AND user_id = @user";
            Add(cmd, "@name", channel.Name); Add(cmd, "@type", channel.Type); Add(cmd, "@target", channel.Target);
            Add(cmd, "@id", channel.Id); Add(cmd, "@user", channel.UserId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool Delete(long userId, long id)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM notify_channels WHERE id = @id AND user_id = @user";
            Add(cmd, "@id", id); Add(cmd, "@user", userId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>解析任务勾选的渠道；无效/已删除的 ID 自动忽略。ID 均来自自家任务 payload，内联拼接安全。</summary>
    public IReadOnlyList<NotifyChannel> ForTask(MonitorTask task)
    {
        var ids = task.NotifyChannelIds.Distinct().Where(id => id > 0).ToList();
        if (ids.Count == 0) return [];
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT id, name, type, target, created_at FROM notify_channels WHERE user_id = @user AND id IN ({string.Join(",", ids)})";
            Add(cmd, "@user", task.UserId);
            using var reader = cmd.ExecuteReader();
            var items = new List<NotifyChannel>();
            while (reader.Read()) items.Add(Read(reader, task.UserId));
            return items;
        }
    }

    private static NotifyChannel Read(DbDataReader reader, long userId) => new()
    {
        Id = Convert.ToInt64(reader.GetValue(0)),
        UserId = userId,
        Name = reader.GetString(1),
        Type = reader.GetString(2),
        Target = reader.GetString(3),
        CreatedAt = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind)
    };

    private static void Add(DbCommand cmd, string name, object value) { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p); }
}

/// <summary>状态变更时按任务勾选的渠道分发通知：邮箱走 SMTP，万记通走 REST 写入备忘。</summary>
public sealed class NotificationService(NotifyChannelStore channels, MonitorStore store, IHttpClientFactory clients, IConfiguration config, ILogger<NotificationService> logger)
{
    public async Task SendAsync(MonitorTask task, CheckResult result, bool recovered, CancellationToken ct)
    {
        foreach (var channel in channels.ForTask(task))
        {
            try
            {
                if (channel.Type == "wanjitong") await SendWanjitongAsync(channel, task, result, recovered, ct);
                else await SendEmailAsync(channel, task, result, recovered, ct);
            }
            catch (Exception ex) { logger.LogError(ex, "任务 {TaskId} 通过渠道 {ChannelName}(#{ChannelId}) 发送通知失败", task.Id, channel.Name, channel.Id); }
        }
    }

    private async Task SendEmailAsync(NotifyChannel channel, MonitorTask task, CheckResult result, bool recovered, CancellationToken ct)
    {
        var host = config["Smtp:Host"];
        var from = config["Smtp:From"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from)) throw new InvalidOperationException("请配置 Smtp:Host 与 Smtp:From 才能发送邮件通知");
        var title = recovered ? $"{task.Name} 已恢复" : $"{task.Name} 检测异常";
        using var message = new MailMessage { From = new MailAddress(from), Subject = $"[API Monitor {(recovered ? "恢复" : "告警")}] {title}", Body = Body(task, result, recovered) };
        message.To.Add(channel.Target);
        using var client = new SmtpClient(host, config.GetValue("Smtp:Port", 587)) { EnableSsl = config.GetValue("Smtp:EnableSsl", true) };
        if (!string.IsNullOrWhiteSpace(config["Smtp:Username"])) client.Credentials = new NetworkCredential(config["Smtp:Username"], config["Smtp:Password"]);
        await client.SendMailAsync(message, ct);
    }

    private async Task SendWanjitongAsync(NotifyChannel channel, MonitorTask task, CheckResult result, bool recovered, CancellationToken ct)
    {
        var endpoint = config["Wanjitong:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint)) endpoint = "https://www.wanjitong.cn/api/memos";
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", store.Decrypt(channel.Target));
        request.Content = new StringContent(JsonSerializer.Serialize(new { content = Body(task, result, recovered) }), Encoding.UTF8, "application/json");
        using var response = await clients.CreateClient("wanjitong").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"万记通通知失败 HTTP {(int)response.StatusCode}：{CheckDump.Clip(body, 300)}");
        }
    }

    private static string Body(MonitorTask task, CheckResult result, bool recovered) => new StringBuilder()
        .AppendLine($"【API Monitor {(recovered ? "恢复" : "告警")}】{task.Name}")
        .AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}")
        .AppendLine($"类型：{task.Kind.ToUpperInvariant()} · 状态：{(recovered ? "已恢复正常" : "由正常转为异常")}")
        .AppendLine($"结果：{result.Message}（耗时 {result.DurationMs}ms）")
        .ToString();
}
