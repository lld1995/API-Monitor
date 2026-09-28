using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
namespace ApiMonitor.Api;

public enum Provider { OpenAIOfficial = 0, LegacyAnthropic = 1, LegacyChatGPT = 2, Claude = 3, Ark = 4, Mimo = 5, GLM = 6, Bocha = 9, Tavily = 10, Brave = 11, Custom = 12 }
public enum LlmProtocol { OpenAI, Anthropic }
public sealed record CheckResult(bool Success, string Message, long DurationMs, DateTime? ExpiresAt = null, string? Usage = null);
public interface IProviderChecker { Provider Provider { get; } Task<CheckResult> CheckAsync(string key, string? endpointUrl, LlmProtocol? protocol, string? model, bool useProxy, CancellationToken ct); }
public sealed record ProviderDefinition(Provider Provider, string DisplayName, string? Endpoint, LlmProtocol? Protocol, bool IsLlm);
public static class ProviderCatalog
{
    public static readonly IReadOnlyList<ProviderDefinition> All = [
        new(Provider.OpenAIOfficial, "OpenAI", "https://api.openai.com/v1", LlmProtocol.OpenAI, true), new(Provider.Claude, "Claude", "https://api.anthropic.com/v1", LlmProtocol.Anthropic, true), new(Provider.Ark, "方舟", "https://ark.cn-beijing.volces.com/api/v3", LlmProtocol.OpenAI, true), new(Provider.Mimo, "Mimo", "https://api.xiaomimimo.com/v1", LlmProtocol.OpenAI, true), new(Provider.GLM, "GLM", "https://open.bigmodel.cn/api/paas/v4", LlmProtocol.OpenAI, true), new(Provider.Bocha, "Bocha", "https://api.bochaai.com/v1/web-search", null, false), new(Provider.Tavily, "Tavily", "https://api.tavily.com/search", null, false), new(Provider.Brave, "Brave", "https://api.search.brave.com/res/v1/web/search", null, false), new(Provider.Custom, "自定义 LLM 供应商", null, null, true)
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
        else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (requestMethod == HttpMethod.Post && req.Content is null) req.Content = new StringContent(JsonSerializer.Serialize(Payload(provider, selectedProtocol, model)), Encoding.UTF8, "application/json");
        try { using var response = await clients.CreateClient(useProxy ? "providers-proxy" : "providers").SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); var body = await response.Content.ReadAsStringAsync(ct); sw.Stop(); return new(response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "真实调用成功" : SafeErrorMessage(body, response.StatusCode), sw.ElapsedMilliseconds); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "供应商请求超时", sw.ElapsedMilliseconds); } catch (Exception ex) { return new(false, ex.Message[..Math.Min(ex.Message.Length, 160)], sw.ElapsedMilliseconds); }
    }
    public static string Endpoint(Provider p) => ProviderCatalog.DefaultEndpoint(p) ?? "";
private static object Payload(Provider p, LlmProtocol? protocol, string? model) => ProviderCatalog.IsLlm(p) ? (protocol == LlmProtocol.Anthropic ? new { model = model!.Trim(), max_tokens = 3, messages = new[] { new { role = "user", content = "Reply with OK only." } } } : new { model = model!.Trim(), messages = new[] { new { role = "user", content = "Reply with OK only." } }, max_tokens = 3 }) : p switch { Provider.Bocha => new { query = "api monitor health check", count = 1 }, Provider.Tavily => new { query = "api monitor health check", max_results = 1 }, Provider.Brave => new { q = "api monitor health check", count = 1 }, _ => new { } };
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
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "ApiMonitor/1.0");
            using var response = await clients.CreateClient(useProxy ? "http-check-proxy" : "http-check")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return new(response.StatusCode == HttpStatusCode.OK, $"HTTP {(int)response.StatusCode}", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(false, "HTTP 请求超时", sw.ElapsedMilliseconds); }
        catch (Exception ex) { return new(false, ex.Message[..Math.Min(ex.Message.Length, 160)], sw.ElapsedMilliseconds); }
    }
}

public sealed class ProviderModelService(IHttpClientFactory clients)
{
    public async Task<IReadOnlyList<string>> GetModelsAsync(Provider provider, string? endpointUrl, LlmProtocol? protocol, string key, bool useProxy, CancellationToken ct)
    {
        var definition = ProviderCatalog.Get(provider);
        if (!definition.IsLlm) throw new InvalidOperationException("普通 API 不支持模型列表");
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
