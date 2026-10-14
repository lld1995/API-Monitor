using System.Security.Claims;
using System.Text.RegularExpressions;
using ApiMonitor.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ApiMonitor.Api.Controllers;

[ApiController, Route("api/auth")]
public sealed partial class AuthController(UserStore users) : ControllerBase
{
    [GeneratedRegex("^[A-Za-z0-9_.-]{3,32}$")]
    private static partial Regex UsernameRule();

    public sealed record RegisterRequest(string Username, string Password);
    public sealed record LoginRequest(string Username, string Password);

    [HttpPost("register"), EnableRateLimiting("auth")]
    public async Task<IActionResult> Register(RegisterRequest input)
    {
        var username = input.Username.Trim();
        if (!UsernameRule().IsMatch(username)) return BadRequest("用户名需为 3-32 位字母、数字或 _ . - 字符");
        if (input.Password.Length is < 6 or > 128) return BadRequest("密码长度应为 6 到 128 位");
        AppUser user;
        try { user = users.Create(username, input.Password); }
        catch (InvalidOperationException ex) { return BadRequest(ex.Message); }
        await SignIn(user);
        return Ok(new { user.Id, user.Username });
    }

    [HttpPost("login"), EnableRateLimiting("auth")]
    public async Task<IActionResult> Login(LoginRequest input)
    {
        var user = users.FindByName(input.Username.Trim());
        if (user is null || !Passwords.Verify(input.Password, user.PasswordHash)) return Unauthorized("用户名或密码不正确");
        await SignIn(user);
        return Ok(new { user.Id, user.Username });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("me"), Authorize]
    public IActionResult Me() => Ok(new { Id = long.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), Username = User.Identity!.Name });

    private async Task SignIn(AppUser user)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.Username) };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    }
}
