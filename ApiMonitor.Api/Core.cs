using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
namespace ApiMonitor.Api;

public enum Provider { OpenAIOfficial = 0, LegacyAnthropic = 1, LegacyChatGPT = 2, Claude = 3, Ark = 4, Mimo = 5, GLM = 6, Bocha = 9, Tavily = 10, Brave = 11, Custom = 12, Qianfan = 13, Serper = 14, Exa = 15 }
public enum LlmProtocol { OpenAI, Anthropic }
public sealed record CheckResult(bool Success, string Message, long DurationMs, DateTime? ExpiresAt = null, string? Usage = null, string? RequestDetail = null, string? ResponseDetail = null);
public interface IProviderChecker { Provider Provider { get; } Task<CheckResult> CheckAsync(string key, string? endpointUrl, LlmProtocol? protocol, string? model, bool useProxy, CancellationToken ct); }
public sealed record ProviderDefinition(Provider Provider, string DisplayName, string? Endpoint, LlmProtocol? Protocol, bool IsLlm);
public static class ProviderCatalog
{
    public static readonly IReadOnlyList<ProviderDefinition> All = [
        new(Provider.OpenAIOfficial, "OpenAI", "https://api.openai.com/v1", LlmProtocol.OpenAI, true), new(Provider.Claude, "Claude", "https://api.anthropic.com/v1", LlmProtocol.Anthropic, true), new(Provider.Ark, "方舟", "https://ark.cn-beijing.volces.com/api/v3", LlmProtocol.OpenAI, true), new(Provider.Mimo, "Mimo", "https://api.xiaomimimo.com/v1", LlmProtocol.OpenAI, true), new(Provider.GLM, "GLM", "https://open.bigmodel.cn/api/paas/v4", LlmProtocol.OpenAI, true), new(Provider.Bocha, "Bocha", "https://api.bochaai.com/v1/web-search", null, false), new(Provider.Tavily, "Tavily", "https://api.tavily.com/search", null, false), new(Provider.Brave, "Brave", "https://api.search.brave.com/res/v1/web/search", null, false), new(Provider.Qianfan, "百度千帆", "https://qianfan.baidubce.com/v2/ai_search/web_search", null, false), new(Provider.Serper, "Serper", "https://google.serper.dev/search", null, false), new(Provider.Exa, "Exa", "https://api.exa.ai/search", null, false), new(Provider.Custom, "自定义 LLM 供应商", null, null, true)
    ];
public static ProviderDefinition Get(Provider p) => p switch { Provider.LegacyAnthropic => All.Single(x => x.Provider == Provider.Claude), Provider.LegacyChatGPT => All.Single(x => x.Provider == Provider.OpenAIOfficial), _ => All.Single(x => x.Provider == p) };
    public static bool IsLlm(Provider p) => Get(p).IsLlm;
    public static LlmProtocol Protocol(Provider p) => Get(p).Protocol ?? throw new InvalidOperationException("该供应商没有 LLM 协议");
    public static string? DefaultEndpoint(Provider p) => Get(p).Endpoint;
}
public sealed class ProviderChecker(Provider provider, IHttpClientFactory clients, IConfiguration config) : IProviderChecker
{
    public Provider Provider => provider;
public async Task<CheckResult> CheckAsync(string key, string? endpointUrl, LlmProtocol? protocol, string? model, bool useProxy, CancellationToken ct)
    {
var sw = System.Diagnostics.Stopwatch.StartNew(); var definition = ProviderCatalog.Get(provider); var customEndpointAllowed = provider is Provider.Custom; var baseEndpoint = customEndpointAllowed && !string.IsNullOrWhiteSpace(endpointUrl) ? endpointUrl.TrimEnd('/') : definition.Endpoint?.TrimEnd('/'); var selectedProtocol = customEndpointAllowed ? protocol : definition.Protocol; var endpoint = definition.IsLlm ? $"{baseEndpoint}/{(selectedProtocol == LlmProtocol.Anthropic ? "messages" : "chat/completions")}" : baseEndpoint;
if (endpoint is null || (definition.IsLlm && selectedProtocol is null) || (definition.IsLlm && string.IsNullOrWhiteSpace(model)) || (!definition.IsLlm && (protocol is not null || !string.IsNullOrWhiteSpace(endpointUrl) || !string.IsNullOrWhiteSpace(model))) || (!customEndpointAllowed && !string.IsNullOrWhiteSpace(endpointUrl))) return new(false, "LLM 必须先获取并选择模型", 0);
if (useProxy && string.IsNullOrWhiteSpace(config["Proxy:Url"])) return new(false, "服务端未配置代理 URL", 0); if (customEndpointAllowed && !PublicCheckPolicy.IsLlmBaseEndpoint(endpointUrl!)) return new(false, "自定义 LLM URL 必须是 http(s)://主机/v1 基础地址，系统会自动追加协议路径", 0);
        if (!PublicCheckPolicy.IsSafeEndpoint(endpoint, customEndpointAllowed && !string.IsNullOrWhiteSpace(endpointUrl))) return new(false, "URL 不安全：仅允许公网 HTTPS 地址", 0);
        var requestMethod = provider is Provider.Brave ? HttpMethod.Get : HttpMethod.Post;
        if (provider is Provider.Brave)
        {
            endpoint = $"{endpoint}?q={Uri.EscapeDataString("api monitor health check")}&count=1";
        }
        using var req = new HttpRequestMessage(requestMethod, endpoint); req.Headers.Add("User-Agent", "ApiMonitor/1.0");
        if (definition.IsLlm && selectedProtocol == LlmProtocol.Anthropic) { req.Headers.Add("x-api-key", key); req.Headers.Add("anthropic-version", "2023-06-01"); }
        else if (provider is Provider.Tavily) req.Content = new StringContent(JsonSerializer.Serialize(new { query = "api monitor health check", max_results = 1, api_key = key }), Encoding.UTF8, "application/json");
        else if (provider is Provider.Brave) req.Headers.Add("X-Subscription-Token", key);
        else if (provider is Provider.Serper) req.Headers.Add("X-API-KEY", key);
        else if (provider is Provider.Exa) req.Headers.Add("x-api-key", key);
        else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (requestMethod == HttpMethod.Post && req.Content is null) req.Content = new StringContent(JsonSerializer.Serialize(Payload(provider, selectedProtocol, model)), Encoding.UTF8, "application/json");
        var requestDump = CheckDump.Redact(await CheckDump.RequestAsync(req), key);
        try { using var response = await clients.CreateClient(useProxy ? "providers-proxy" : "providers").SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); var body = await response.Content.ReadAsStringAsync(ct); sw.Stop(); return new(response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "真实调用成功" : SafeErrorMessage(body, response.StatusCode), sw.ElapsedMilliseconds, RequestDetail: requestDump, ResponseDetail: CheckDump.Redact(CheckDump.Response((int)response.StatusCode, response.ReasonPhrase, CheckDump.Clip(body, 8192)), key)); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "供应商请求超时", sw.ElapsedMilliseconds, RequestDetail: requestDump); } catch (Exception ex) { return new(false, ex.Message[..Math.Min(ex.Message.Length, 160)], sw.ElapsedMilliseconds, RequestDetail: requestDump); }
    }
    public static string Endpoint(Provider p) => ProviderCatalog.DefaultEndpoint(p) ?? "";
private static object Payload(Provider p, LlmProtocol? protocol, string? model) => ProviderCatalog.IsLlm(p) ? (protocol == LlmProtocol.Anthropic ? new { model = model!.Trim(), max_tokens = 3, messages = new[] { new { role = "user", content = "Reply with OK only." } } } : new { model = model!.Trim(), messages = new[] { new { role = "user", content = "Reply with OK only." } }, max_tokens = 3 }) : p switch { Provider.Bocha => new { query = "api monitor health check", count = 1 }, Provider.Tavily => new { query = "api monitor health check", max_results = 1 }, Provider.Brave => new { q = "api monitor health check", count = 1 }, Provider.Qianfan => new { messages = new[] { new { role = "user", content = "Reply with OK only." } } }, Provider.Serper => new { q = "api monitor health check", num = 1 }, Provider.Exa => new { query = "api monitor health check", numResults = 1 }, _ => new { } };
public static string SafeErrorMessage(string body, HttpStatusCode code) { try { using var d = JsonDocument.Parse(body); if (d.RootElement.TryGetProperty("error", out var e)) { if (e.ValueKind == JsonValueKind.String) return $"HTTP {(int)code}: {e.GetString()}"; if (e.TryGetProperty("message", out var m)) return $"HTTP {(int)code}: {m.GetString()}"; if (e.TryGetProperty("detail", out var detail)) return $"HTTP {(int)code}: {detail.GetString()}"; } if (d.RootElement.TryGetProperty("message", out var topMessage)) return $"HTTP {(int)code}: {topMessage.GetString()}"; if (d.RootElement.TryGetProperty("detail", out var topDetail)) return $"HTTP {(int)code}: {topDetail.GetString()}"; } catch { } return $"HTTP {(int)code}: 供应商拒绝请求（请检查请求方式、参数和代理配置）"; }
}

public sealed class HttpUrlChecker(IHttpClientFactory clients, IConfiguration config)
{
    public static bool IsValidUrl(string? url) => !string.IsNullOrWhiteSpace(url) && url.Length <= 500 &&
        PublicCheckPolicy.IsSafeEndpoint(url, true) && Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.IsNullOrEmpty(uri.Fragment);

    public async Task<CheckResult> CheckAsync(string url, bool useProxy, CancellationToken ct)
    {
        if (!IsValidUrl(url)) return new(false, "请输入有效的 HTTP(S) URL（不含用户信息或片段）", 0);
        if (useProxy && string.IsNullOrWhiteSpace(config["Proxy:Url"])) return new(false, "服务端未配置代理 URL", 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var requestDump = $"GET {url}\nUser-Agent: ApiMonitor/1.0";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "ApiMonitor/1.0");
            using var response = await clients.CreateClient(useProxy ? "http-check-proxy" : "http-check")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var snippet = await CheckDump.ReadSnippetAsync(response, 4096, ct);
            return new(response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}", sw.ElapsedMilliseconds,
                RequestDetail: requestDump, ResponseDetail: CheckDump.Response((int)response.StatusCode, response.ReasonPhrase, snippet));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "HTTP 请求超时", sw.ElapsedMilliseconds, RequestDetail: requestDump); }
        catch (Exception ex) { return new(false, ex.Message[..Math.Min(ex.Message.Length, 160)], sw.ElapsedMilliseconds, RequestDetail: requestDump); }
    }
}

public sealed class ProviderModelService(IHttpClientFactory clients)
{
    public async Task<IReadOnlyList<string>> GetModelsAsync(Provider provider, string? endpointUrl, LlmProtocol? protocol, string key, bool useProxy, CancellationToken ct)
    {
        var definition = ProviderCatalog.Get(provider);
        if (!definition.IsLlm) throw new InvalidOperationException("搜索 API 不支持模型列表");
var selectedProtocol = provider == Provider.Custom ? protocol : definition.Protocol;
        if (selectedProtocol is null) throw new InvalidOperationException("必须指定 LLM 协议");
        var baseUrl = provider == Provider.Custom ? endpointUrl?.TrimEnd('/') : definition.Endpoint?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl) || (provider == Provider.Custom && !PublicCheckPolicy.IsLlmBaseEndpoint(baseUrl))) throw new InvalidOperationException("LLM 基础 URL 必须填写到 /v1 等基础路径");
        var modelsUrl = $"{baseUrl}/models";
        if (!PublicCheckPolicy.IsSafeEndpoint(modelsUrl, provider == Provider.Custom)) throw new InvalidOperationException("URL 不安全");
        using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
        request.Headers.Add("User-Agent", "ApiMonitor/1.0");
        if (selectedProtocol == LlmProtocol.Anthropic) { request.Headers.Add("x-api-key", key); request.Headers.Add("anthropic-version", "2023-06-01"); }
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await clients.CreateClient(useProxy ? "providers-proxy" : "providers").SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ProviderChecker.SafeErrorMessage(body, response.StatusCode));
        var models = ParseModels(body);
        if (models.Count == 0) throw new InvalidOperationException("模型列表为空或响应格式不支持");
        return models;
    }

    public static List<string> ParseModels(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return data.EnumerateArray().Select(x => x.TryGetProperty("id", out var id) ? id.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList()!;
    }
}

public static class PublicCheckPolicy
{
public static bool IsAllowed(Provider provider, string key) => key.Length is >= 10 and <= 512 && !key.Contains('\n') && !key.Contains('\r') && Enum.IsDefined(provider);
    public static bool IsLlmBaseEndpoint(string value) => Uri.TryCreate(value?.TrimEnd('/'), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) && !uri.AbsolutePath.Contains("chat/completions", StringComparison.OrdinalIgnoreCase) && !uri.AbsolutePath.EndsWith("/messages", StringComparison.OrdinalIgnoreCase);
    public static bool IsSafeEndpoint(string value, bool customAllowed) { if (!customAllowed) return true; if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo)) return false; return true; }
    private static bool IsPrivateIpv4(IPAddress ip) { var b = ip.GetAddressBytes(); return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254); }
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}

/// <summary>把检测过程的请求与响应整理成可读文本，敏感凭据一律脱敏。</summary>
public static class CheckDump
{
    public const int StoreLimit = 32_768;
    private static readonly string[] SecretHeaders = ["authorization", "x-api-key", "x-subscription-token", "proxy-authorization"];

    public static string Clip(string? text, int limit = StoreLimit) =>
        string.IsNullOrEmpty(text) || text.Length <= limit ? text ?? "" : text[..limit] + $"\n…（内容过长已截断，原始长度 {text.Length} 字符）";

    public static string Secret(string? value) => string.IsNullOrEmpty(value) ? "（未设置）" : "****" + (value.Length <= 4 ? "" : value[^4..]);

    /// <summary>把文本中出现的原始密钥整体替换为脱敏形式，防止凭据经请求体或错误信息泄露。</summary>
    public static string Redact(string text, string? secret) =>
        string.IsNullOrEmpty(secret) || secret.Length < 6 || !text.Contains(secret, StringComparison.Ordinal)
            ? text
            : text.Replace(secret, "****" + secret[^4..], StringComparison.Ordinal);

    /// <summary>去掉 URL 中的用户凭据，用于展示代理地址等可能内嵌账号密码的链接。</summary>
    public static string SafeUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped)
        : url;

    public static async Task<string> RequestAsync(HttpRequestMessage request)
    {
        var sb = new StringBuilder();
        sb.Append(request.Method.Method).Append(' ').Append(request.RequestUri);
        foreach (var (name, values) in request.Headers)
            sb.Append('\n').Append(name).Append(": ").Append(MaskHeader(name, string.Join(", ", values)));
        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
                sb.Append('\n').Append(name).Append(": ").Append(string.Join(", ", values));
            var body = await request.Content.ReadAsStringAsync();
            if (body.Length > 0) sb.Append("\n\n").Append(Pretty(body));
        }
        return sb.ToString();
    }

    public static string Response(int? status, string? reason, string? body)
    {
        var head = status is null ? "" : $"HTTP {status}{(string.IsNullOrWhiteSpace(reason) ? "" : " " + reason)}\n\n";
        return head + (string.IsNullOrWhiteSpace(body) ? "（响应体为空）" : Pretty(body));
    }

    public static string Pretty(string body)
    {
        try { using var doc = JsonDocument.Parse(body); return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }); }
        catch (JsonException) { return body; }
    }

    /// <summary>最多读取 maxBytes 字节的响应体用于展示；流式响应在 5 秒内读不满即放弃，避免拖住检测。</summary>
    public static async Task<string> ReadSnippetAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[maxBytes];
            var read = 0;
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
                if (count == 0) break;
                read += count;
            }
            var text = Encoding.UTF8.GetString(buffer, 0, read).TrimEnd('\uFFFD');
            return read == buffer.Length ? text + "\n…（响应体已截断）" : text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return "（响应体读取超时，已跳过）"; }
        catch (Exception ex) when (ex is IOException or HttpRequestException) { return $"（响应体读取失败：{ex.Message}）"; }
    }

    private static string MaskHeader(string name, string value)
    {
        if (!SecretHeaders.Contains(name.ToLowerInvariant())) return value;
        var space = value.IndexOf(' ');
        return space > 0 ? $"{value[..space]} {Secret(value[(space + 1)..])}" : Secret(value);
    }
}
