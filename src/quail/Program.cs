using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Quail;
using Quail.Data;
using Quail.Endpoints;
using Quail.Services;
using ZLinq;

// Register code pages so MimeKit can decode non-UTF8 charsets under InvariantGlobalization
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

var builder = WebApplication.CreateSlimBuilder(args);

// Source-generated JSON serialization
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

// Database
builder.Services.AddSingleton<QuailDataStore>();

// Services
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<EmailService>();

// Email protocol servers via Kestrel
var mailServerOptions = new MailServerOptions();
builder.Configuration.GetSection(MailServerOptions.SectionName).Bind(mailServerOptions);

var seedAdminOptions = new SeedAdminOptions();
builder.Configuration.GetSection(SeedAdminOptions.SectionName).Bind(seedAdminOptions);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    // Limit request body size to 10MB
    kestrel.Limits.MaxRequestBodySize = 10 * 1024 * 1024;

    foreach (var ep in mailServerOptions.Http.AsValueEnumerable().Where(e => e.Enabled))
        ListenEndpoint(kestrel, ep);

    foreach (var ep in mailServerOptions.Smtp.AsValueEnumerable().Where(e => e.Enabled))
        ListenEndpoint(kestrel, ep, opts => opts.UseConnectionHandler<SmtpConnectionHandler>());

    foreach (var ep in mailServerOptions.Pop3.AsValueEnumerable().Where(e => e.Enabled))
        ListenEndpoint(kestrel, ep, opts => opts.UseConnectionHandler<Pop3ConnectionHandler>());

    foreach (var ep in mailServerOptions.Imap.AsValueEnumerable().Where(e => e.Enabled))
        ListenEndpoint(kestrel, ep, opts => opts.UseConnectionHandler<ImapConnectionHandler>());

    static void ListenEndpoint(Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions kestrel,
        ListenEndpoint ep, Action<Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions>? handler = null)
    {
        Action<Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions> configure = opts =>
        {
            handler?.Invoke(opts);
            if (ep.Ssl)
            {
                if (!string.IsNullOrEmpty(ep.CertificatePath))
                    opts.UseHttps(ep.CertificatePath, ep.CertificatePassword ?? "");
                else
                    opts.UseHttps();
            }
        };

        if (ep.Host is "*" or "0.0.0.0")
            kestrel.ListenAnyIP(ep.Port, configure);
        else if (ep.Host is "localhost" or "127.0.0.1")
            kestrel.ListenLocalhost(ep.Port, configure);
        else
            kestrel.Listen(System.Net.IPAddress.Parse(ep.Host), ep.Port, configure);
    }
});

// Authentication: Cookie + JWT dual scheme
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("JWT key not configured");
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "CookieOrJwt";
    options.DefaultChallengeScheme = "CookieOrJwt";
})
.AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
{
    options.Cookie.Name = "quail_auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromDays(7);
    options.Events.OnRedirectToLogin = ctx =>
    {
        ctx.Response.StatusCode = 401;
        return Task.CompletedTask;
    };
})
.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "quail",
        ValidAudience = builder.Configuration["Jwt:Audience"] ?? "quail",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };
})
.AddPolicyScheme("CookieOrJwt", "Cookie or JWT", options =>
{
    options.ForwardDefaultSelector = context =>
    {
        var authHeader = context.Request.Headers.Authorization.AsValueEnumerable().FirstOrDefault();
        if (authHeader?.StartsWith("Bearer ") == true)
            return JwtBearerDefaults.AuthenticationScheme;
        return CookieAuthenticationDefaults.AuthenticationScheme;
    };
});

builder.Services.AddAuthorization();

// Rate limiting (per-IP)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("auth", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    options.AddPolicy("send", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// CORS for development
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

var app = builder.Build();

// Ensure database schema exists
using (var scope = app.Services.CreateScope())
{
    var dataStore = scope.ServiceProvider.GetRequiredService<QuailDataStore>();
    await dataStore.InitializeAsync();

    // Seed configured admin user
    if (seedAdminOptions.Enabled)
    {
        if (string.IsNullOrWhiteSpace(seedAdminOptions.Username) ||
            string.IsNullOrWhiteSpace(seedAdminOptions.Email) ||
            string.IsNullOrWhiteSpace(seedAdminOptions.Password))
        {
            throw new InvalidOperationException("SeedAdmin credentials are not fully configured");
        }

        var authService = scope.ServiceProvider.GetRequiredService<AuthService>();
        if (!await dataStore.UserExistsAsync(seedAdminOptions.Username, seedAdminOptions.Email))
        {
            await authService.RegisterAsync(seedAdminOptions.Username, seedAdminOptions.Email, seedAdminOptions.Password);
        }
    }
}

app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Serve static files (Svelte build output)
app.UseDefaultFiles();
app.UseStaticFiles();

// API endpoints
app.MapAuthEndpoints();
app.MapEmailEndpoints();
app.MapMailboxEndpoints();

// SPA fallback - serve index.html for non-API routes
app.MapFallbackToFile("index.html");

app.Run();
