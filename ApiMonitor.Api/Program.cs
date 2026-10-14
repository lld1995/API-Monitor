using System.Threading.RateLimiting;
using ApiMonitor.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("providers", c => { c.Timeout = TimeSpan.FromSeconds(15); c.DefaultRequestHeaders.Add("Accept", "application/json"); });
builder.Services.AddHttpClient("providers-proxy", c => { c.Timeout = TimeSpan.FromSeconds(15); c.DefaultRequestHeaders.Add("Accept", "application/json"); }).ConfigurePrimaryHttpMessageHandler(sp => new HttpClientHandler { UseProxy = true, Proxy = new System.Net.WebProxy(sp.GetRequiredService<IConfiguration>()["Proxy:Url"] ?? throw new InvalidOperationException("Proxy:Url is required when proxy client is used")) });
builder.Services.AddHttpClient("http-check", c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddHttpClient("http-check-proxy", c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(sp => new HttpClientHandler { AllowAutoRedirect = false, UseProxy = true, Proxy = new System.Net.WebProxy(sp.GetRequiredService<IConfiguration>()["Proxy:Url"] ?? throw new InvalidOperationException("Proxy:Url is required when proxy client is used")) });
builder.Services.AddHttpClient("db-check", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient("wanjitong", c => { c.Timeout = TimeSpan.FromSeconds(15); c.DefaultRequestHeaders.Add("Accept", "application/json"); });
foreach (var p in Enum.GetValues<Provider>()) builder.Services.AddScoped<IProviderChecker>(_ => new ProviderChecker(p, _.GetRequiredService<IHttpClientFactory>(), _.GetRequiredService<IConfiguration>())); builder.Services.AddScoped<ProviderModelService>(); builder.Services.AddScoped<HttpUrlChecker>(); builder.Services.AddScoped<ProxyChecker>(); builder.Services.AddScoped<DatabaseChecker>();
builder.Services.AddSingleton<Db>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<MonitorStore>();
builder.Services.AddSingleton<NotifyChannelStore>();
builder.Services.AddSingleton<NotificationService>();
builder.Services.AddSingleton<MonitorRunner>();
builder.Services.AddHostedService<MonitorScheduler>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "am_auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
    // 纯 API 场景：未登录直接回 401，不要 302 跳转。
    o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
builder.Services.AddRateLimiter(o => o
    .AddPolicy("public", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }))
    .AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));
builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter())); builder.Services.AddOpenApi();
var app = builder.Build();
app.Services.GetRequiredService<Db>().EnsureDatabase();
var seededAdmin = app.Services.GetRequiredService<UserStore>().Initialize();
app.Services.GetRequiredService<MonitorStore>().Initialize();
app.Services.GetRequiredService<NotifyChannelStore>().Initialize();
if (seededAdmin is not null) app.Services.GetRequiredService<MonitorStore>().AdoptOrphans(seededAdmin.Id);
app.UseDefaultFiles(); app.UseStaticFiles();
app.UseRateLimiter(); app.MapOpenApi();
app.UseAuthentication(); app.UseAuthorization(); app.MapControllers(); app.MapFallbackToFile("index.html"); app.Run();
public partial class Program { }
