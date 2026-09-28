using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ApiMonitor.Api;

public sealed class MonitorTask
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "http";
    public Provider? Provider { get; set; }
    public LlmProtocol? Protocol { get; set; }
    public string Url { get; set; } = "";
    public string Model { get; set; } = "";
    public string EncryptedKey { get; set; } = "";
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
    public List<string> Emails { get; set; } = [];
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
    string? Url, string? Model, string? Key, bool UseProxy, int IntervalMinutes, List<string>? Emails,
    string? ProxyUrl = null, string? ProxyProtocol = "tcp", string? ProxyUsername = null, string? ProxyPassword = null,
    string? DbKind = null, string? DbHost = null, int DbPort = 0, string? DbName = null, string? DbUser = null, string? DbPassword = null);
public sealed record MonitorRun(long Id, long TaskId, DateTime CreatedAt, bool Success, string Message, long DurationMs);

public static class MonitorValidation
{
    public static string? Error(MonitorInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100) return "请填写不超过 100 字的任务名称";
        if (input.IntervalMinutes is < 1 or > 1440) return "检测间隔应为 1 到 1440 分钟";
        if (input.Emails is { Count: > 10 } || input.Emails?.Any(e => e.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(e, out var a) || a.Address != e.Trim()) == true) return "邮箱地址不合法（最多 10 个）";
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

public sealed class MonitorStore(IConfiguration config)
{
    private readonly string connectionString = config.GetConnectionString("Default") ?? "Data Source=api-monitor.db";
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Initialize()
    {
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS monitor_tasks (id INTEGER PRIMARY KEY AUTOINCREMENT, payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS monitor_runs (id INTEGER PRIMARY KEY AUTOINCREMENT, task_id INTEGER NOT NULL, created_at TEXT NOT NULL, success INTEGER NOT NULL, message TEXT NOT NULL, duration_ms INTEGER NOT NULL); CREATE INDEX IF NOT EXISTS idx_runs_task ON monitor_runs(task_id, id DESC);";
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    private static MonitorTask Decode(string payload) => JsonSerializer.Deserialize<MonitorTask>(payload, Json)!;
    private static MonitorTask? Find(SqliteConnection db, long id)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM monitor_tasks WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string json ? Decode(json) : null;
    }
    private static void Save(SqliteConnection db, MonitorTask task)
    {
        using var cmd = db.CreateCommand(); cmd.CommandText = "UPDATE monitor_tasks SET payload=$payload WHERE id=$id";
        cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(task, Json)); cmd.Parameters.AddWithValue("$id", task.Id); cmd.ExecuteNonQuery();
    }
    public IReadOnlyList<MonitorTask> List()
    {
        lock (gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM monitor_tasks ORDER BY id DESC";
            using var reader = cmd.ExecuteReader(); var items = new List<MonitorTask>(); while (reader.Read()) items.Add(Decode(reader.GetString(0))); return items; }
    }
    public MonitorTask? Get(long id) { lock (gate) { using var db = Open(); return Find(db, id); } }
    public MonitorTask Create(MonitorTask task)
    {
        lock (gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO monitor_tasks(payload) VALUES ($payload); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(task, Json)); task.Id = (long)cmd.ExecuteScalar()!; Save(db, task); return task; }
    }
    public bool Update(MonitorTask task) { lock (gate) { using var db = Open(); if (Find(db, task.Id) is null) return false; Save(db, task); return true; } }
    public bool Delete(long id)
    {
        lock (gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "DELETE FROM monitor_tasks WHERE id=$id; DELETE FROM monitor_runs WHERE task_id=$id";
            cmd.Parameters.AddWithValue("$id", id); return cmd.ExecuteNonQuery() > 0; }
    }
    public IReadOnlyList<MonitorTask> ClaimDue(DateTime now)
    {
        lock (gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT payload FROM monitor_tasks";
            using var reader = cmd.ExecuteReader(); var due = new List<MonitorTask>(); while (reader.Read()) { var task = Decode(reader.GetString(0)); if (task.Enabled && task.NextRunAt <= now) due.Add(task); }
            reader.Close(); foreach (var task in due) { task.NextRunAt = now.AddMinutes(task.IntervalMinutes); Save(db, task); } return due; }
    }
    public void SetRunning(long id, bool value, DateTime? now = null)
    {
        lock (gate) { using var db = Open(); var current = Find(db, id); if (current is null) return;
            current.IsRunning = value; current.RunningSince = value ? now ?? DateTime.UtcNow : null; Save(db, current); }
    }
    public void Complete(MonitorTask task, CheckResult result, DateTime now)
    {
lock (gate) { using var db = Open(); var current = Find(db, task.Id); if (current is null) return;
            current.LastRunAt = now; current.LastSuccess = result.Success; current.LastMessage = result.Message.Length > 500 ? result.Message[..500] : result.Message;
            current.IsRunning = false; current.RunningSince = null; current.AlertActive = !result.Success; Save(db, current);
using var cmd = db.CreateCommand(); cmd.CommandText = "INSERT INTO monitor_runs(task_id,created_at,success,message,duration_ms) VALUES ($id,$at,$success,$message,$duration)";
            cmd.Parameters.AddWithValue("$id", task.Id); cmd.Parameters.AddWithValue("$at", now.ToString("O")); cmd.Parameters.AddWithValue("$success", result.Success ? 1 : 0);
cmd.Parameters.AddWithValue("$message", current.LastMessage); cmd.Parameters.AddWithValue("$duration", result.DurationMs); cmd.ExecuteNonQuery(); }
    }
    public IReadOnlyList<MonitorRun> Runs(long taskId)
    {
        lock (gate) { using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,task_id,created_at,success,message,duration_ms FROM monitor_runs WHERE task_id=$id ORDER BY id DESC LIMIT 50";
            cmd.Parameters.AddWithValue("$id", taskId); using var reader = cmd.ExecuteReader(); var items = new List<MonitorRun>();
            while (reader.Read()) items.Add(new(reader.GetInt64(0),reader.GetInt64(1),DateTime.Parse(reader.GetString(2),null,System.Globalization.DateTimeStyles.RoundtripKind),reader.GetInt64(3)==1,reader.GetString(4),reader.GetInt64(5))); return items; }
    }
    public string Encrypt(string value)
    {
        var secret = config["Security:KeyEncryptionSecret"];
        if (string.IsNullOrWhiteSpace(secret) || secret.StartsWith("replace-with-", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("请通过环境变量 Security__KeyEncryptionSecret 配置独立加密密钥");
        var nonce = RandomNumberGenerator.GetBytes(12); var plain = Encoding.UTF8.GetBytes(value); var cipher = new byte[plain.Length]; var tag = new byte[16];
        using var aes = new AesGcm(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), 16); aes.Encrypt(nonce, plain, cipher, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(cipher).ToArray());
    }
    public string Decrypt(string value)
    {
        var secret = config["Security:KeyEncryptionSecret"] ?? throw new InvalidOperationException("缺少加密密钥");
        var data = Convert.FromBase64String(value); var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), 16); aes.Decrypt(data.AsSpan(0,12),data.AsSpan(28),data.AsSpan(12,16),plain);
        return Encoding.UTF8.GetString(plain);
    }
}
