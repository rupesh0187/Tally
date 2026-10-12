using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using TallyNext.Api.Endpoints;
using TallyNext.Api.Infrastructure;
using TallyNext.Infrastructure.Server;
using TallyNext.Infrastructure.Server.Auth;
using TallyNext.Infrastructure.Server.Persistence;
using TallyNext.Infrastructure.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, logger) => logger
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddTallyNextServer(builder.Configuration, builder.Environment);

// ---- JWT signing key: required outside Development/Testing; never committed.
var jwtSettings = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var signingKey = jwtSettings.SigningKey;
if (string.IsNullOrWhiteSpace(signingKey))
{
    if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
    {
        throw new InvalidOperationException("Jwt:SigningKey is not configured. Set the Jwt__SigningKey environment variable (at least 32 bytes).");
    }

    signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    Log.Warning("No Jwt:SigningKey configured; using an ephemeral development key (tokens are invalidated on restart)");
}

builder.Services.PostConfigure<JwtOptions>(o => o.SigningKey = signingKey);
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = TokenService.CreateSigningKey(signingKey),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = TallyClaims.Email,
        };
        options.Events = new JwtBearerEvents
        {
            // Disabled users and revoked devices lose access at the next request, not when the token expires.
            OnTokenValidated = async context =>
            {
                var userId = TallyClaims.UserIdOf(context.Principal);
                var auth = context.HttpContext.RequestServices.GetRequiredService<AuthService>();
                if (userId is null || !await auth.IsPrincipalStillValidAsync(userId.Value, TallyClaims.DeviceIdOf(context.Principal), context.HttpContext.RequestAborted))
                {
                    context.Fail("The account or device is no longer active.");
                }
            },
        };
    });

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthEndpoints.RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimiting:AuthPermitsPerMinute", 20),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi(o => o.AddDocumentTransformer<BearerSecuritySchemeTransformer>());
builder.Services.AddHealthChecks();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<ServerDatabaseInitializer>().InitializeAsync();
    if (app.Environment.IsDevelopment())
    {
        await scope.ServiceProvider.GetRequiredService<DevelopmentSeeder>().SeedAsync();
    }
}

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseSerilogRequestLogging();

var exposeOpenApi = app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("OpenApi:Expose");
if (exposeOpenApi)
{
    // Static documentation UI; registered before authorization so the fallback policy does not apply to it.
    app.UseSwaggerUI(o =>
    {
        o.SwaggerEndpoint("/openapi/v1.json", "TallyNext API v1");
        o.RoutePrefix = "swagger";
    });
}

app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

if (exposeOpenApi)
{
    app.MapOpenApi().AllowAnonymous();
}

app.MapHealthChecks("/health").AllowAnonymous();
app.MapAuthEndpoints();
app.MapAccountingEndpoints();
app.MapSyncEndpoints();

await app.RunAsync();

/// <summary>Entry point (public for WebApplicationFactory-based tests).</summary>
public partial class Program;
