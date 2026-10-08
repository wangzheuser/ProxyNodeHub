using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using ProxyNodeHub;
using ProxyNodeHub.Web;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;
var directory = Path.GetFullPath(config["DATA_DIR"] ?? "/data");
Directory.CreateDirectory(directory);
// A data volume belongs to exactly one running host, including during migration.
using var writerLock = new FileStream(Path.Combine(directory, "writer.lock"), FileMode.OpenOrCreate,
    FileAccess.ReadWrite, FileShare.None);
var password = config["ADMIN_PASSWORD"];
if (config["ADMIN_PASSWORD_FILE"] is { Length: > 0 } passwordFile)
{
    if (!string.IsNullOrEmpty(password)) throw new InvalidOperationException("Set only ADMIN_PASSWORD or ADMIN_PASSWORD_FILE.");
    password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
}
if (password is not { Length: >= 12 and <= 1024 })
    throw new InvalidOperationException("ADMIN_PASSWORD (or ADMIN_PASSWORD_FILE) must contain 12–1024 characters.");
var passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
password = null;
var credentialVersion = Convert.ToHexString(passwordHash);

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.IncludeFields = true);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")))
    .SetApplicationName("ProxyNodeHub.Web");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "proxynodehub.session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(12);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    o.Events.OnValidatePrincipal = c =>
    {
        if (c.Principal?.FindFirstValue("credential-version") != credentialVersion) c.RejectPrincipal();
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.Name = "proxynodehub.csrf";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // ponytail: one administrator, a global login budget avoids unbounded IP state.
    o.AddFixedWindowLimiter("login", limits =>
    { limits.PermitLimit = 10; limits.Window = TimeSpan.FromMinutes(1); limits.QueueLimit = 0; });
});
builder.Services.AddSingleton(new StateStore(directory, new(
    config.GetValue("REFRESH_HOURS", 6), config.GetValue("REPO_COUNT", 10),
    config.GetValue("INACTIVE_DAYS", 7), config.GetValue("AUTO_REFRESH", true))));
builder.Services.AddSingleton(p => new ConnectionStore(directory, config, p.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddSingleton(new FeatureLibrary(directory));
builder.Services.AddSingleton<DiscoveryWorker>();
builder.Services.AddHostedService(p => p.GetRequiredService<DiscoveryWorker>());
builder.Services.AddSingleton<SubsCheckClient>();
builder.Services.AddSingleton<SubscriptionExporter>();

var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});
app.UseExceptionHandler(error => error.Run(async context =>
{
    var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, message) = ex switch
    {
        ArgumentException a => (400, a.Message),
        KeyNotFoundException k => (404, k.Message),
        InvalidDataException d => (422, d.Message),
        HttpRequestException => (502, "上游请求失败，请检查网络、服务地址或 API Key。"),
        OperationCanceledException => (504, "请求已取消或超时，请稍后重试。"),
        _ => (500, "操作失败，请检查服务日志和数据卷权限。")
    };
    await Results.Problem(statusCode: status, title: message).ExecuteAsync(context);
}));
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException)
        {
            await Results.Problem(statusCode: 400, title: "会话校验失败，请刷新页面后重试。").ExecuteAsync(context);
            return;
        }
    }
    await next(context);
});

app.MapGet("/api/session", (HttpContext context, IAntiforgery csrf) =>
    Results.Ok(new { authenticated = context.User.Identity?.IsAuthenticated == true,
        csrfToken = csrf.GetAndStoreTokens(context).RequestToken }));
app.MapPost("/api/login", async (LoginRequest request, HttpContext context) =>
{
    if (request.Password is not { Length: > 0 and <= 1024 } ||
        !CryptographicOperations.FixedTimeEquals(passwordHash, SHA256.HashData(Encoding.UTF8.GetBytes(request.Password))))
        return Results.Problem(statusCode: 401, title: "密码不正确。");
    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "admin"),
        new Claim("credential-version", credentialVersion) }, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
    return Results.NoContent();
}).RequireRateLimiting("login");
app.MapPost("/api/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();
app.MapGet("/live", () => Results.Text("alive\n"));
app.MapGet("/health", (StateStore store) => store.IsFresh ? Results.Text("ready\n")
    : Results.Problem(statusCode: 503, title: "尚无结果，或结果已超过25小时。"));
app.MapGet("/subscriptions.txt", (StateStore store) => store.State.Current is { } snapshot
    ? Results.Text(string.Join('\n', snapshot.Subscriptions) + "\n", "text/plain; charset=utf-8")
    : Results.Problem(statusCode: 503, title: "尚无有效订阅，请等待首次发现完成。"));
app.MapGet("/status", (StateStore store, DiscoveryWorker worker) => Results.Ok(new
{
    generatedAt = store.State.Current?.GeneratedAt, subscriptions = store.State.Current?.Subscriptions.Length ?? 0,
    fresh = store.IsFresh, running = worker.Attempt.Running, nextRunAt = worker.NextRunAt
}));
app.MapProxyNodeApi();
app.MapDiscoveryApi();
app.MapCheckerApi();
await app.RunAsync();

internal sealed record LoginRequest(string? Password);
