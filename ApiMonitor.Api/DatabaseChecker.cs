using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace ApiMonitor.Api;

public enum DbKind { MySql = 0, TDengine = 1, Milvus = 2, TuGraph = 3 }

public sealed record DbDefinition(DbKind Kind, string DisplayName, int DefaultPort, string Hint);

public static class DbCatalog
{
    public static readonly IReadOnlyList<DbDefinition> All = [
        new(DbKind.MySql, "MySQL / OceanBase / Manticore", 3306, "MySQL 协议兼容，支持 MySQL、OceanBase、ManticoreSearch"),
        new(DbKind.TDengine, "TDengine", 6041, "使用 REST 接口 /rest/sql 检测，默认端口 6041"),
        new(DbKind.Milvus, "Milvus", 19530, "使用 gRPC 端口健康检查，默认端口 19530"),
        new(DbKind.TuGraph, "TuGraph", 7070, "使用 HTTP 接口检测，默认端口 7070")
    ];

    public static DbDefinition Get(DbKind kind) => All.Single(x => x.Kind == kind);
}

/// <summary>数据库连通性检测：MySQL 协议走真实连接，其余走各自 HTTP/TCP 探活。</summary>
public sealed class DatabaseChecker(IHttpClientFactory clients)
{
    public static bool IsValidHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) && host.Length <= 255 &&
        !host.Contains(' ') && !host.Contains('/') && !host.Contains('\\');

    public static bool IsValidPort(int port) => port is > 0 and <= 65535;

    public async Task<CheckResult> CheckAsync(DbKind kind, string host, int port, string? database, string? user, string? password, CancellationToken ct)
    {
        if (!IsValidHost(host)) return new(false, "请填写有效的数据库主机地址", 0);
        if (!IsValidPort(port)) return new(false, "端口应在 1 到 65535 之间", 0);
        return kind switch
        {
            DbKind.MySql => await CheckMySqlAsync(host, port, database, user, password, ct),
            DbKind.TDengine => await CheckTDengineAsync(host, port, database, user, password, ct),
DbKind.Milvus => await CheckMilvusAsync(host, port, user, password, ct),
            DbKind.TuGraph => await CheckTuGraphAsync(host, port, user, password, ct),
            _ => new(false, "不支持的数据库类型", 0)
        };
    }

    private static async Task<CheckResult> CheckMySqlAsync(string host, int port, string? database, string? user, string? password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user)) return new(false, "请填写数据库用户名", 0);
        var sw = Stopwatch.StartNew();
        try
        {
            var builder = new MySqlConnectionStringBuilder
            {
                Server = host, Port = (uint)port, UserID = user, Password = password ?? "",
                ConnectionTimeout = 10, DefaultCommandTimeout = 10, Pooling = false,
                SslMode = MySqlSslMode.Preferred
            };
            if (!string.IsNullOrWhiteSpace(database)) builder.Database = database;
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = new MySqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(ct);
            sw.Stop();
            var version = connection.ServerVersion;
            return new(true, $"连接成功，服务端版本 {version}", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "数据库连接超时", sw.ElapsedMilliseconds); }
        catch (MySqlException ex) { return new(false, $"MySQL 错误 {ex.ErrorCode}：{Trim(ex.Message)}", sw.ElapsedMilliseconds); }
        catch (Exception ex) { return new(false, Trim(ex.Message), sw.ElapsedMilliseconds); }
    }

    private async Task<CheckResult> CheckTDengineAsync(string host, int port, string? database, string? user, string? password, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var url = $"http://{host}:{port}/rest/sql{(string.IsNullOrWhiteSpace(database) ? "" : "/" + Uri.EscapeDataString(database))}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Add("User-Agent", "ApiMonitor/1.0");
            if (!string.IsNullOrWhiteSpace(user))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password ?? ""}")));
            request.Content = new StringContent("SELECT server_version()", Encoding.UTF8, "text/plain");
            using var response = await clients.CreateClient("db-check").SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();
            if (!response.IsSuccessStatusCode) return new(false, $"HTTP {(int)response.StatusCode}：{Trim(body)}", sw.ElapsedMilliseconds);
            return new(true, "TDengine REST 接口可用", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "TDengine 请求超时", sw.ElapsedMilliseconds); }
        catch (Exception ex) { return new(false, Trim(ex.Message), sw.ElapsedMilliseconds); }
    }

    private async Task<CheckResult> CheckMilvusAsync(string host, int port, string? user, string? password, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(user)) return new(false, "请填写 Milvus 用户名", 0);
        if (string.IsNullOrEmpty(password)) return new(false, "请填写 Milvus 密码", 0);
        try
        {
            var url = $"http://{host}:{port}/v2/vectordb/collections/list";
            var client = clients.CreateClient("db-check");
            // 先用相同接口做无凭据对照；未开启鉴权的 Milvus 会对任意账号密码返回 code=0。
            using (var anonymous = new HttpRequestMessage(HttpMethod.Post, url))
            {
                anonymous.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var anonymousResponse = await client.SendAsync(anonymous, ct);
                var anonymousBody = await anonymousResponse.Content.ReadAsStringAsync(ct);
                if (IsMilvusSuccess(anonymousResponse, anonymousBody))
                    return new(true, "Milvus 连接成功（服务未启用鉴权，未验证用户名和密码）", sw.ElapsedMilliseconds);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $"{user}:{password}");
            request.Headers.Add("User-Agent", "ApiMonitor/1.0");
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!IsMilvusSuccess(response, body))
                return new(false, $"Milvus 认证或请求失败（HTTP {(int)response.StatusCode}）：{Trim(body)}", sw.ElapsedMilliseconds);
            return new(true, "Milvus 连接及认证成功", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "Milvus 请求超时", sw.ElapsedMilliseconds); }
        catch (Exception ex) { return new(false, Trim(ex.Message), sw.ElapsedMilliseconds); }
    }

    private static bool IsMilvusSuccess(HttpResponseMessage response, string body)
    {
        if (!response.IsSuccessStatusCode) return false;
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var value) && value == 0;
        }
        catch (JsonException) { return false; }
    }

    private async Task<CheckResult> CheckTuGraphAsync(string host, int port, string? user, string? password, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var url = $"http://{host}:{port}/";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "ApiMonitor/1.0");
            if (!string.IsNullOrWhiteSpace(user))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password ?? ""}")));
            using var response = await clients.CreateClient("db-check").SendAsync(request, ct);
            sw.Stop();
            // TuGraph 根路径返回 200/401/403 都说明服务在线
            var online = response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            return new(online, online ? $"TuGraph 服务在线（HTTP {(int)response.StatusCode}）" : $"HTTP {(int)response.StatusCode}", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "TuGraph 请求超时", sw.ElapsedMilliseconds); }
        catch (Exception ex) { return new(false, Trim(ex.Message), sw.ElapsedMilliseconds); }
    }

    private static string Trim(string message) => message[..Math.Min(message.Length, 160)];
}
