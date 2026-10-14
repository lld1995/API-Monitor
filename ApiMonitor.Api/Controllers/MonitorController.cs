using ApiMonitor.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ApiMonitor.Api.Controllers;

[ApiController, Route("api")]
public sealed class MonitorController(IEnumerable<IProviderChecker> checkers, ProviderModelService modelService, HttpUrlChecker httpChecker, ProxyChecker proxyChecker, DatabaseChecker dbChecker) : ControllerBase
{
    [HttpPost("public-check"), EnableRateLimiting("public")]
    public async Task<ActionResult<CheckResult>> PublicCheck(PublicCheckRequest input, CancellationToken ct)
    {
var definition = ProviderCatalog.Get(input.Provider); if (input.Provider == Provider.Custom && input.Protocol is null) return BadRequest("自定义供应商必须指定协议");
        if (!PublicCheckPolicy.IsAllowed(input.Provider, input.Key)) return BadRequest("供应商或 Key 格式不合法");
        if (!definition.IsLlm && input.Protocol is not null) return BadRequest("搜索 API 不需要协议");
var result = await checkers.Single(x => x.Provider == input.Provider).CheckAsync(input.Key, input.EndpointUrl, input.Protocol, input.Model, input.UseProxy, ct); return Ok(result);
    }

    [HttpPost("http-check"), EnableRateLimiting("public")]
    public async Task<ActionResult<CheckResult>> HttpCheck(HttpCheckRequest input, CancellationToken ct)
    {
        if (!HttpUrlChecker.IsValidUrl(input.Url)) return BadRequest("请输入有效的 HTTP(S) URL（不含用户信息或片段）");
        return Ok(await httpChecker.CheckAsync(input.Url, input.UseProxy, ct));
    }

    [HttpPost("proxy-check"), EnableRateLimiting("public")]
    public async Task<ActionResult<CheckResult>> ProxyCheck(ProxyCheckRequest input, CancellationToken ct)
    {
        if (!ProxyChecker.IsValidProxy(input.ProxyUrl)) return BadRequest("代理地址格式不正确，示例：http://127.0.0.1:7890 或 socks5://127.0.0.1:1080");
        if (!string.IsNullOrWhiteSpace(input.TargetUrl) && !ProxyChecker.IsValidTarget(input.TargetUrl)) return BadRequest("目标地址必须是有效的 HTTP(S) URL");
        return Ok(await proxyChecker.CheckAsync(input.ProxyUrl, input.TargetUrl, null, null, input.Protocol, ct));
    }

    [HttpPost("db-check"), EnableRateLimiting("public")]
    public async Task<ActionResult<CheckResult>> DbCheck(DbCheckRequest input, CancellationToken ct)
    {
        if (!Enum.TryParse<DbKind>(input.DbKind, true, out var kind)) return BadRequest("不支持的数据库类型");
        if (!DatabaseChecker.IsValidHost(input.Host)) return BadRequest("请填写有效的数据库主机地址");
        var port = input.Port > 0 ? input.Port : DbCatalog.Get(kind).DefaultPort;
        if (!DatabaseChecker.IsValidPort(port)) return BadRequest("端口应在 1 到 65535 之间");
        if (kind == DbKind.MySql && string.IsNullOrWhiteSpace(input.User)) return BadRequest("请填写数据库用户名");
        return Ok(await dbChecker.CheckAsync(kind, input.Host, port, input.Database, input.User, input.Password, ct));
    }

    [HttpPost("models"), EnableRateLimiting("public")]
    public async Task<ActionResult<object>> Models(ModelListRequest input, CancellationToken ct)
    {
        var definition = ProviderCatalog.Get(input.Provider);
        if (!definition.IsLlm) return BadRequest("普通 API 不支持模型列表");
        if (input.Provider == Provider.Custom && input.Protocol is null) return BadRequest("自定义供应商必须指定协议");
        if (!PublicCheckPolicy.IsAllowed(input.Provider, input.Key)) return BadRequest("Key 格式不合法");
        try { return Ok(new { models = await modelService.GetModelsAsync(input.Provider, input.EndpointUrl, input.Protocol, input.Key, input.UseProxy, ct) }); }
        catch (Exception ex) { return BadRequest(ex.Message); }
    }
}
public sealed record HttpCheckRequest(string Url, bool UseProxy = false);
public sealed record ProxyCheckRequest(string ProxyUrl, string? TargetUrl = null, string? Protocol = "tcp");
public sealed record DbCheckRequest(string DbKind, string Host, int Port = 0, string? Database = null, string? User = null, string? Password = null);
public sealed record ModelListRequest(Provider Provider, string Key, string? EndpointUrl = null, LlmProtocol? Protocol = null, bool UseProxy = false);
public sealed record PublicCheckRequest(Provider Provider, string Key, string? EndpointUrl = null, LlmProtocol? Protocol = null, string? Model = null, bool UseProxy = false);