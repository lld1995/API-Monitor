using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ApiMonitor.Api;

public sealed class MonitorTask
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "http";
    public Provider? Provider { get; set; }
    public LlmProtocol? Protocol { get; set; }
    public string Url { get; set; } = "";
    public string Model { get; set; } = "";
    public string EncryptedKey { get; set; } = "";
    public List<long> NotifyChannelIds { get; set; } = [];
    public string ProxyUrl { get; set; } = "";
    public string ProxyProtocol { get; set; } = "tcp";
    public string ProxyUsername { get; set; } = "";
    public string EncryptedProxyPassword { get; set; } = "";
    public string DbKind { get; set; } = "mysql";
    public string DbHost { get; set; } = "";
    public int DbPort { get; set; }
    public string DbName { get; set; } = "";
    public string DbUser { get; set; } = "";
    public string EncryptedDbPassword { get; set; } = "";
    public bool UseProxy { get; set; }
    public int IntervalMinutes { get; set; } = 5;
    public bool Enabled { get; set; } = true;
    public DateTime NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public bool? LastSuccess { get; set; }
    public string LastMessage { get; set; } = "";
    public bool AlertActive { get; set; }
    public bool IsRunning { get; set; }
    public DateTime? RunningSince { get; set; }
}

public sealed record MonitorInput(string Name, string Kind, Provider? Provider, LlmProtocol? Protocol,
    string? Url, string? Model, string? Key, bool UseProxy, int IntervalMinutes, List<long>? NotifyChannelIds,
    string? ProxyUrl = null, string? ProxyProtocol = "tcp", string? ProxyUsername = null, string? ProxyPassword = null,
    string? DbKind = null, string? DbHost = null, int DbPort = 0, string? DbName = null, string? DbUser = null, string? DbPassword = null);
public sealed record MonitorRun(long Id, long TaskId, DateTime CreatedAt, bool Success, string Message, long DurationMs, string? Request = null, string? Response = null);

public static class MonitorValidation
{
    public static string? Error(MonitorInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100) return "请填写不超过 100 字的任务名称";
        if (input.IntervalMinutes is < 1 or > 1440) return "检测间隔应为 1 到 1440 分钟";
        if (input.NotifyChannelIds is { Count: > 20 } || input.NotifyChannelIds?.Any(c => c <= 0) == true) return "通知渠道不合法（最多勾选 20 个）";
        if (input.Kind == "http") return HttpUrlChecker.IsValidUrl(input.Url) ? null : "请输入有效的 HTTP(S) URL";
if (input.Kind == "proxy")
        {
            if (!ProxyChecker.IsValidProxy(input.ProxyUrl)) return "请填写有效的代理地址，示例：http://127.0.0.1:7890 或 socks5://127.0.0.1:1080";
            var proxyIsSocks5 = input.ProxyUrl!.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase) || input.ProxyUrl.StartsWith("socks5h://", StringComparison.OrdinalIgnoreCase);
            if (input.ProxyProtocol is not ("tcp" or "udp")) return "代理检测协议只能选择 TCP 或 UDP";
            if (input.ProxyProtocol == "udp" && !proxyIsSocks5) return "HTTP 代理不支持 UDP 检测，UDP 仅适用于 SOCKS5";
            return null;
        }
        if (input.Kind == "db")
        {
            if (input.DbKind is null || !Enum.TryParse<DbKind>(input.DbKind, true, out var dbKind)) return "请选择数据库类型";
            if (!DatabaseChecker.IsValidHost(input.DbHost)) return "请填写有效的数据库主机地址";
            var port = input.DbPort > 0 ? input.DbPort : DbCatalog.Get(dbKind).DefaultPort;
            if (!DatabaseChecker.IsValidPort(port)) return "端口应在 1 到 65535 之间";
            if (dbKind == DbKind.MySql && string.IsNullOrWhiteSpace(input.DbUser)) return "请填写数据库用户名";
            return null;
        }
        if (input.Kind is not ("llm" or "api") || input.Provider is null || !Enum.IsDefined(input.Provider.Value)) return "不支持的检测类型或供应商";
        var definition = ProviderCatalog.Get(input.Provider.Value);
        if (definition.IsLlm != (input.Kind == "llm")) return "供应商与检测类型不匹配";
        if (!PublicCheckPolicy.IsAllowed(input.Provider.Value, input.Key ?? "")) return "API Key 格式不合法";
        if (definition.IsLlm && string.IsNullOrWhiteSpace(input.Model)) return "请选择模型";
        if (input.Provider == ApiMonitor.Api.Provider.Custom && (input.Protocol is null || !PublicCheckPolicy.IsLlmBaseEndpoint(input.Url ?? ""))) return "自定义供应商必须填写协议和有效的基础 URL";
        if (input.Provider != ApiMonitor.Api.Provider.Custom && !string.IsNullOrWhiteSpace(input.Url)) return "内置供应商使用固定 URL";
        return null;
    }
}

public sealed class MonitorStore(IConfiguration config, Db db)
{
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Initialize()
    {
        lock (gate)
        {
            using var connection = db.Open();
            Execute(connection, $"""
                CREATE TABLE IF NOT EXISTS monitor_tasks (
                    {db.IdColumn},
                    user_id {(db.IsMySql ? "BIGINT" : "INTEGER")} NOT NULL DEFAULT 0,
                    payload {db.TextType} NOT NULL
                )
                """);
            Execute(connection, $"""
                CREATE TABLE IF NOT EXISTS monitor_runs (
                    {db.IdColumn},
                    task_id {(db.IsMySql ? "BIGINT" : "INTEGER")} NOT NULL,
                    created_at {(db.IsMySql ? "VARCHAR(64)" : "TEXT")} NOT NULL,
                    success {(db.IsMySql ? "TINYINT" : "INTEGER")} NOT NULL,
                    message TEXT NOT NULL,
                    duration_ms {(db.IsMySql ? "BIGINT" : "INTEGER")} NOT NULL,
                    request {db.TextNotNull},
                    response {db.TextNotNull}
                )
                """);
            db.TryCreateIndex(connection, "idx_runs_task", "monitor_runs", "task_id, id");
            db.TryCreateIndex(connection, "idx_tasks_user", "monitor_tasks", "user_id");
            // 旧库补列：user_id（用户归属）、request/response（记录详情）。
            db.TryAddColumn(connection, "monitor_tasks", "user_id", (db.IsMySql ? "BIGINT" : "INTEGER") + " NOT NULL DEFAULT 0");
            db.TryAddColumn(connection, "monitor_runs", "request", db.TextNotNull);
            db.TryAddColumn(connection, "monitor_runs", "response", db.TextNotNull);
        }
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>把无归属（user_id=0）的存量任务划给首个种子管理员。</summary>
    public int AdoptOrphans(long userId)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE monitor_tasks SET user_id = @user WHERE user_id = 0";
            Add(cmd, "@user", userId);
            return cmd.ExecuteNonQuery();
        }
    }

    private static MonitorTask Decode(string payload) => JsonSerializer.Deserialize<MonitorTask>(payload, Json)!;

    private static MonitorTask? Find(DbConnection connection, long id)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT payload, user_id FROM monitor_tasks WHERE id = @id";
        Add(cmd, "@id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        var task = Decode(reader.GetString(0));
        task.UserId = Convert.ToInt64(reader.GetValue(1));
        return task;
    }

    private static void Save(DbConnection connection, MonitorTask task)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE monitor_tasks SET payload = @payload WHERE id = @id";
        Add(cmd, "@payload", JsonSerializer.Serialize(task, Json));
        Add(cmd, "@id", task.Id);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<MonitorTask> List(long userId)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT payload FROM monitor_tasks WHERE user_id = @user ORDER BY id DESC";
            Add(cmd, "@user", userId);
            using var reader = cmd.ExecuteReader();
            var items = new List<MonitorTask>();
            while (reader.Read()) items.Add(Decode(reader.GetString(0)));
            return items;
        }
    }

    /// <summary>按用户取任务；找不到或不属于该用户返回 null。</summary>
    public MonitorTask? Get(long userId, long id)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT payload FROM monitor_tasks WHERE id = @id AND user_id = @user";
            Add(cmd, "@id", id);
            Add(cmd, "@user", userId);
            return cmd.ExecuteScalar() is string json ? Decode(json) : null;
        }
    }

    /// <summary>不限用户取任务（后台调度/执行用）。</summary>
    public MonitorTask? Get(long id)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT payload, user_id FROM monitor_tasks WHERE id = @id";
            Add(cmd, "@id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var task = Decode(reader.GetString(0));
            task.UserId = Convert.ToInt64(reader.GetValue(1));
            return task;
        }
    }

    public MonitorTask Create(MonitorTask task)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO monitor_tasks(user_id, payload) VALUES (@user, @payload)";
            Add(cmd, "@user", task.UserId);
            Add(cmd, "@payload", JsonSerializer.Serialize(task, Json));
            cmd.ExecuteNonQuery();
            task.Id = db.LastInsertId(connection);
            Save(connection, task);
            return task;
        }
    }

    public bool Update(MonitorTask task) { lock (gate) { using var connection = db.Open(); if (Find(connection, task.Id) is null) return false; Save(connection, task); return true; } }
    public bool Delete(long userId, long id)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var runs = connection.CreateCommand();
            runs.CommandText = "DELETE FROM monitor_runs WHERE task_id = @id";
            Add(runs, "@id", id);
            runs.ExecuteNonQuery();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM monitor_tasks WHERE id = @id AND user_id = @user";
            Add(cmd, "@id", id);
            Add(cmd, "@user", userId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<MonitorTask> ClaimDue(DateTime now)
    {
        lock (gate)
        {
            using var connection = db.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT payload FROM monitor_tasks";
            using var reader = cmd.ExecuteReader();
            var due = new List<MonitorTask>();
            while (reader.Read()) { var task = Decode(reader.GetString(0)); if (task.Enabled && task.NextRunAt <= now) due.Add(task); }
            reader.Close();
            foreach (var task in due) { task.NextRunAt = now.AddMinutes(task.IntervalMinutes); Save(connection, task); }
            return due;
        }
    }

    public void SetRunning(long id, bool value, DateTime? now = null)
    {
        lock (gate) { using var connection = db.Open(); var current = Find(connection, id); if (current is null) return;
            current.IsRunning = value; current.RunningSince = value ? now ?? DateTime.UtcNow : null; Save(connection, current); }
    }

    public void Complete(MonitorTask task, CheckResult result, DateTime now)
    {
        lock (gate) { using var connection = db.Open(); var current = Find(connection, task.Id); if (current is null) return;
            current.LastRunAt = now; current.LastSuccess = result.Success; current.LastMessage = result.Message.Length > 500 ? result.Message[..500] : result.Message;
            current.IsRunning = false; current.RunningSince = null; current.AlertActive = !result.Success; Save(connection, current);
            using var cmd = connection.CreateCommand(); cmd.CommandText = "INSERT INTO monitor_runs(task_id,created_at,success,message,duration_ms,request,response) VALUES (@id,@at,@success,@message,@duration,@request,@response)";
            cmd.Parameters.Add(Create(cmd, "@id", task.Id)); cmd.Parameters.Add(Create(cmd, "@at", now.ToString("O"))); cmd.Parameters.Add(Create(cmd, "@success", result.Success ? 1 : 0));
            cmd.Parameters.Add(Create(cmd, "@message", current.LastMessage)); cmd.Parameters.Add(Create(cmd, "@duration", result.DurationMs));
            cmd.Parameters.Add(Create(cmd, "@request", CheckDump.Clip(result.RequestDetail))); cmd.Parameters.Add(Create(cmd, "@response", CheckDump.Clip(result.ResponseDetail))); cmd.ExecuteNonQuery(); }
    }

    public IReadOnlyList<MonitorRun> Runs(long taskId)
    {
        // 列表只取请求/响应的开头做预览，完整内容走 RunDetail，避免 50 条全量内容拖慢轮询。
        lock (gate) { using var connection = db.Open(); using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT id,task_id,created_at,success,message,duration_ms,substr(request,1,160),substr(response,1,160) FROM monitor_runs WHERE task_id=@id ORDER BY id DESC LIMIT 50";
            Add(cmd, "@id", taskId); using var reader = cmd.ExecuteReader(); var items = new List<MonitorRun>();
            while (reader.Read()) items.Add(new(reader.GetInt64(0),reader.GetInt64(1),DateTime.Parse(reader.GetString(2),null,System.Globalization.DateTimeStyles.RoundtripKind),reader.GetBoolean(3),reader.GetString(4),reader.GetInt64(5),reader.GetString(6),reader.GetString(7))); return items; }
    }
    public MonitorRun? RunDetail(long runId)
    {
        lock (gate) { using var connection = db.Open(); using var cmd = connection.CreateCommand(); cmd.CommandText = "SELECT id,task_id,created_at,success,message,duration_ms,request,response FROM monitor_runs WHERE id=@id";
            Add(cmd, "@id", runId); using var reader = cmd.ExecuteReader();
            return reader.Read() ? new MonitorRun(reader.GetInt64(0),reader.GetInt64(1),DateTime.Parse(reader.GetString(2),null,System.Globalization.DateTimeStyles.RoundtripKind),reader.GetBoolean(3),reader.GetString(4),reader.GetInt64(5),reader.GetString(6),reader.GetString(7)) : null; }
    }
    public string Encrypt(string value)
    {
        var secret = config["Security:KeyEncryptionSecret"];
        if (string.IsNullOrWhiteSpace(secret) || secret.StartsWith("replace-with-", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请通过环境变量 Security__KeyEncryptionSecret 配置独立加密密钥");
        var nonce = RandomNumberGenerator.GetBytes(12); var plain = Encoding.UTF8.GetBytes(value); var cipher = new byte[plain.Length]; var tag = new byte[16];
        using var aes = new AesGcm(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), 16); aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag.Concat(cipher)).ToArray());
    }
    public string Decrypt(string value)
    {
        var secret = config["Security:KeyEncryptionSecret"] ?? throw new InvalidOperationException("缺少加密密钥");
        var data = Convert.FromBase64String(value); var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), 16); aes.Decrypt(data.AsSpan(0,12),data.AsSpan(28),data.AsSpan(12,16),plain);
        return Encoding.UTF8.GetString(plain);
    }

    private static void Add(DbCommand cmd, string name, object value) => cmd.Parameters.Add(Create(cmd, name, value));
    private static DbParameter Create(DbCommand cmd, string name, object value) { var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; return p; }
}
