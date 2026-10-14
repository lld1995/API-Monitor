using System.Security.Claims;
using ApiMonitor.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApiMonitor.Api.Controllers;

[ApiController, Route("api/monitoring"), Authorize]
public sealed class MonitoringController(MonitorStore store, MonitorRunner runner, ProviderModelService modelService, NotifyChannelStore channels) : ControllerBase
{
    private long UserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static object Public(MonitorTask t) => new
    {
        t.Id, t.Name, t.Kind, t.Provider, t.Protocol, t.Url, t.Model, t.UseProxy, t.IntervalMinutes, t.NotifyChannelIds, t.Enabled, t.NextRunAt, t.LastRunAt, t.LastSuccess, t.LastMessage, t.AlertActive, t.IsRunning, t.RunningSince,
        t.ProxyUrl, t.ProxyProtocol, t.ProxyUsername, t.DbKind, t.DbHost, t.DbPort, t.DbName, t.DbUser,
        HasKey = t.EncryptedKey.Length > 0, HasDbPassword = t.EncryptedDbPassword.Length > 0, HasProxyPassword = t.EncryptedProxyPassword.Length > 0
    };

    [HttpGet]
    public IActionResult List() => Ok(store.List(UserId).Select(Public));

    [HttpPost]
    public IActionResult Create(MonitorInput input)
    {
        var error = MonitorValidation.Error(input); if (error is not null) return BadRequest(error);
        var channelError = ValidateChannels(input); if (channelError is not null) return BadRequest(channelError);
        try { var task = Build(input); task.UserId = UserId; return Ok(Public(store.Create(task))); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }
    [HttpPut("{id:long}")]
    public IActionResult Update(long id, MonitorInput input)
    {
        var old = store.Get(UserId, id); if (old is null) return NotFound();
        var checkedInput = input with { Key = string.IsNullOrEmpty(input.Key) && input.Kind is "llm" or "api" ? store.Decrypt(old.EncryptedKey) : input.Key, ProxyPassword = string.IsNullOrEmpty(input.ProxyPassword) && input.Kind == "proxy" && old.EncryptedProxyPassword.Length > 0 ? store.Decrypt(old.EncryptedProxyPassword) : input.ProxyPassword, DbPassword = string.IsNullOrEmpty(input.DbPassword) && input.Kind == "db" && old.EncryptedDbPassword.Length > 0 ? store.Decrypt(old.EncryptedDbPassword) : input.DbPassword };
        var error = MonitorValidation.Error(checkedInput); if (error is not null) return BadRequest(error);
        var channelError = ValidateChannels(checkedInput); if (channelError is not null) return BadRequest(channelError);
        try { var task = Build(checkedInput); task.Id = id; task.UserId = UserId; task.Enabled = old.Enabled; task.NextRunAt = old.NextRunAt; task.LastRunAt = old.LastRunAt; task.LastSuccess = old.LastSuccess; task.LastMessage = old.LastMessage; task.AlertActive = old.AlertActive;
            store.Update(task); return Ok(Public(task)); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
    }
    [HttpDelete("{id:long}")]
    public IActionResult Delete(long id) => store.Delete(UserId, id) ? NoContent() : NotFound();
    [HttpPost("{id:long}/enabled")]
    public IActionResult SetEnabled(long id, [FromBody] EnabledInput input)
    {
        var task = store.Get(UserId, id); if (task is null) return NotFound(); task.Enabled = input.Enabled;
        if (input.Enabled) task.NextRunAt = DateTime.UtcNow; store.Update(task); return Ok(Public(task));
    }
    [HttpPost("{id:long}/models")]
    public async Task<IActionResult> Models(long id, SavedModelListRequest input, CancellationToken ct)
    {
        var task = store.Get(UserId, id);
        if (task is null) return NotFound();
        if (task.Kind != "llm" || task.EncryptedKey.Length == 0) return BadRequest("该任务没有已保存的 LLM 密钥");
        if (task.Provider != input.Provider) return BadRequest("供应商已变更，请输入新供应商的 API Key");
        if (!ProviderCatalog.Get(input.Provider).IsLlm) return BadRequest("请选择 LLM 供应商");
        try
        {
            var key = store.Decrypt(task.EncryptedKey);
            var models = await modelService.GetModelsAsync(input.Provider, input.EndpointUrl, input.Protocol, key, input.UseProxy, ct);
            return Ok(new { models });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            return BadRequest(ex is InvalidOperationException ? ex.Message : "已保存的密钥无法解密，请重新填写 API Key 并保存");
        }
    }
    [HttpGet("{id:long}/runs")]
    public IActionResult Runs(long id) => store.Get(UserId, id) is null ? NotFound() : Ok(store.Runs(id));
    [HttpGet("runs/{runId:long}")]
    public IActionResult RunDetail(long runId)
    {
        var run = store.RunDetail(runId);
        if (run is null || store.Get(UserId, run.TaskId) is null) return NotFound();
        return Ok(run);
    }
    [HttpPost("{id:long}/run")]
    public async Task<IActionResult> Run(long id, CancellationToken ct)
    {
        if (store.Get(UserId, id) is null) return NotFound(); var result = await runner.RunAsync(id, ct);
        return result is null ? Conflict("该任务正在执行") : Ok(result);
    }
    private string? ValidateChannels(MonitorInput input)
    {
        var ids = input.NotifyChannelIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0) return null;
        return ids.Count(id => channels.Get(UserId, id) is null) > 0 ? "包含无效的通知渠道，请重新勾选" : null;
    }
    private MonitorTask Build(MonitorInput input)
    {
        var isKeyKind = input.Kind is "llm" or "api";
        var dbKind = input.Kind == "db" && Enum.TryParse<DbKind>(input.DbKind, true, out var parsed) ? parsed : DbKind.MySql;
        return new()
        {
            Name = input.Name.Trim(), Kind = input.Kind, Provider = isKeyKind ? input.Provider : null,
            Protocol = input.Kind == "llm" ? input.Protocol : null, Url = input.Url?.Trim() ?? "", Model = input.Model?.Trim() ?? "",
            EncryptedKey = isKeyKind ? store.Encrypt(input.Key!) : "",
            NotifyChannelIds = input.NotifyChannelIds?.Distinct().ToList() ?? [],
            UseProxy = input.UseProxy,
            ProxyUrl = input.Kind == "proxy" ? input.ProxyUrl?.Trim() ?? "" : "",
            ProxyProtocol = input.Kind == "proxy" ? input.ProxyProtocol?.ToLowerInvariant() ?? "tcp" : "tcp",
            ProxyUsername = input.Kind == "proxy" ? input.ProxyUsername?.Trim() ?? "" : "",
            EncryptedProxyPassword = input.Kind == "proxy" && !string.IsNullOrEmpty(input.ProxyPassword) ? store.Encrypt(input.ProxyPassword) : "",
            DbKind = dbKind.ToString().ToLowerInvariant(), DbHost = input.Kind == "db" ? input.DbHost?.Trim() ?? "" : "",
            DbPort = input.Kind == "db" ? (input.DbPort > 0 ? input.DbPort : DbCatalog.Get(dbKind).DefaultPort) : 0,
            DbName = input.Kind == "db" ? input.DbName?.Trim() ?? "" : "", DbUser = input.Kind == "db" ? input.DbUser?.Trim() ?? "" : "",
            EncryptedDbPassword = input.Kind == "db" && !string.IsNullOrEmpty(input.DbPassword) ? store.Encrypt(input.DbPassword) : "",
            IntervalMinutes = input.IntervalMinutes,
            NextRunAt = DateTime.UtcNow
        };
    }
}
public sealed record EnabledInput(bool Enabled);
public sealed record SavedModelListRequest(Provider Provider, LlmProtocol? Protocol, string? EndpointUrl, bool UseProxy);
