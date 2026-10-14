using System.Net.Mail;
using System.Security.Claims;
using ApiMonitor.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApiMonitor.Api.Controllers;

[ApiController, Route("api/notifications"), Authorize]
public sealed class NotificationsController(NotifyChannelStore channels, MonitorStore store) : ControllerBase
{
    private long UserId => long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public sealed record NotificationInput(string Name, string Type, string Target);

    private static object Public(NotifyChannel c, MonitorStore store) => new
    {
        c.Id, c.Name, c.Type,
        Target = c.Type == "wanjitong" ? Masked(c, store) : c.Target,
        c.CreatedAt
    };
    private static string Masked(NotifyChannel c, MonitorStore store)
    {
        try { var key = store.Decrypt(c.Target); return "****" + (key.Length > 4 ? key[^4..] : ""); }
        catch { return "••••••••"; }
    }

    [HttpGet]
    public IActionResult List() => Ok(channels.List(UserId).Select(c => Public(c, store)));

    [HttpPost]
    public IActionResult Create(NotificationInput input)
    {
        var (channel, error) = Build(UserId, input, null);
        if (error is not null) return BadRequest(error);
        return Ok(Public(channels.Create(channel!), store));
    }

    [HttpPut("{id:long}")]
    public IActionResult Update(long id, NotificationInput input)
    {
        var old = channels.Get(UserId, id); if (old is null) return NotFound();
        var (channel, error) = Build(UserId, input, old);
        if (error is not null) return BadRequest(error);
        channel!.Id = id;
        return channels.Update(channel) ? Ok(Public(channel, store)) : NotFound();
    }

    [HttpDelete("{id:long}")]
    public IActionResult Delete(long id) => channels.Delete(UserId, id) ? NoContent() : NotFound();

    private (NotifyChannel?, string?) Build(long userId, NotificationInput input, NotifyChannel? old)
    {
        var name = input.Name.Trim();
        if (name.Length is < 1 or > 50) return (null, "渠道名称应为 1 到 50 个字符");
        var type = input.Type.Trim().ToLowerInvariant();
        if (!NotifyChannelStore.Types.Contains(type)) return (null, "渠道类型仅支持 email（邮箱）或 wanjitong（万记通）");
        var target = input.Target.Trim();
        if (type == "email")
        {
            if (string.IsNullOrEmpty(target) && old is { Type: "email" })
                return (new NotifyChannel { UserId = userId, Name = name, Type = type, Target = old.Target, CreatedAt = old.CreatedAt }, null);
            if (target.Length > 254 || !MailAddress.TryCreate(target, out var address) || address.Address != target)
                return (null, "邮箱地址不合法");
            return (new NotifyChannel { UserId = userId, Name = name, Type = type, Target = target, CreatedAt = old?.CreatedAt ?? DateTime.UtcNow }, null);
        }
        // 万记通：编辑时留空表示保持原 Key 不变。
        if (string.IsNullOrEmpty(target) && old is { Type: "wanjitong" })
            return (new NotifyChannel { UserId = userId, Name = name, Type = type, Target = old.Target, CreatedAt = old.CreatedAt }, null);
        if (target.Length is < 8 or > 512 || target.Contains('\n') || target.Contains('\r')) return (null, "万记通 API Key 应为 8 到 512 个字符");
        return (new NotifyChannel { UserId = userId, Name = name, Type = type, Target = store.Encrypt(target), CreatedAt = old?.CreatedAt ?? DateTime.UtcNow }, null);
    }
}
