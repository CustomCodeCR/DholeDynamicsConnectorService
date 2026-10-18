using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Infrastructure;
using Dhole.Dynamics.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Dhole.Dynamics.Api;

public static class ServiceRegistration
{
    public const string AdminCookie = "AdminSession";
    public const string AdminOidc = "AdminOidc";
    public const string MachineBearer = "MachineBearer";

    public static IServiceCollection AddDholeApiSecurity(this IServiceCollection services, IConfiguration config, IHostEnvironment environment)
    {
        var adminAuthority = config["AdminOidc:Authority"];
        var adminId = config["AdminOidc:ClientId"];
        var adminSecret = config["AdminOidc:ClientSecret"];
        var machineAuthority = config["M2m:Authority"];
        var machineAudience = config["M2m:Audience"];
        var publicOrigin = config["Admin:PublicOrigin"];
        if (!string.IsNullOrWhiteSpace(publicOrigin) &&
            (!Uri.TryCreate(publicOrigin, UriKind.Absolute, out var publicUri)
                || publicUri.Scheme != "https" || publicUri.UserInfo.Length > 0
                || publicUri.Query.Length > 0 || publicUri.Fragment.Length > 0
                || publicUri.AbsolutePath != "/"))
            throw new InvalidOperationException("Admin public origin must be a single HTTPS origin.");
        var adminConfigured = IsHttpsAuthority(adminAuthority) && !string.IsNullOrWhiteSpace(adminId)
            && !string.IsNullOrWhiteSpace(adminSecret);
        var machineConfigured = IsHttpsAuthority(machineAuthority) && !string.IsNullOrWhiteSpace(machineAudience);
        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing") && (!adminConfigured || !machineConfigured))
            throw new InvalidOperationException("AdminOidc and M2m external providers must be configured in non-development environments.");
        if (!string.IsNullOrWhiteSpace(adminAuthority) && !IsHttpsAuthority(adminAuthority))
            throw new InvalidOperationException("Administrator identity authority must be HTTPS.");
        if (!string.IsNullOrWhiteSpace(machineAuthority) && !IsHttpsAuthority(machineAuthority))
            throw new InvalidOperationException("Machine identity authority must be HTTPS.");

        var auth = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = AdminCookie;
            options.DefaultChallengeScheme = AdminCookie;
        }).AddCookie(AdminCookie, options =>
        {
            options.Cookie.Name = "__Host-dhole-dynamics-admin";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            options.SlidingExpiration = true;
            options.Events.OnRedirectToLogin = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        }).AddJwtBearer(MachineBearer, options =>
        {
            options.MapInboundClaims = false;
            options.RequireHttpsMetadata = true;
            if (machineConfigured)
            {
                options.Authority = machineAuthority;
                options.Audience = machineAudience;
            }
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidateAudience = true, ValidateIssuerSigningKey = true,
                ValidateLifetime = true, RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                ValidAudience = machineAudience,
                ValidIssuer = machineConfigured ? machineAuthority!.TrimEnd('/') : "https://disabled.invalid",
                // Authority metadata supplies signing keys; no shared HMAC secret or fallback.
            };
            options.Events = new JwtBearerEvents
            {
                OnTokenValidated = context =>
                {
                    var principal = context.Principal!;
                    // App-only Entra token: delegated scopes and human user tokens must not gain machine access.
                    if (principal.HasClaim(c => c.Type == "scp")
                        || (principal.FindFirst("idtyp")?.Value != "app"
                            && !principal.HasClaim(c => c.Type is "roles" or "role"))
                        || string.IsNullOrWhiteSpace(ScopeSecurity.ClientId(principal))
                        || context.SecurityToken is null)
                    {
                        context.Fail("A registered application token is required.");
                        return Task.CompletedTask;
                    }
                    if (principal.Identity is ClaimsIdentity identity)
                    {
                        foreach (var supplied in identity.FindAll("connector:issuer").ToArray()) identity.RemoveClaim(supplied);
                        identity.AddClaim(new Claim("connector:issuer", context.SecurityToken.Issuer));
                    }
                    return Task.CompletedTask;
                }
            };
        });

        if (adminConfigured)
        {
            if (string.IsNullOrWhiteSpace(publicOrigin))
                throw new InvalidOperationException("Admin:PublicOrigin HTTPS callback origin is required.");
            auth.AddOpenIdConnect(AdminOidc, options =>
            {
                options.Authority = adminAuthority;
                options.ClientId = adminId;
                options.ClientSecret = adminSecret;
                options.SignInScheme = AdminCookie;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.RequireHttpsMetadata = true;
                options.CallbackPath = "/signin-oidc";
                options.Events.OnRedirectToIdentityProvider = ctx =>
                {
                    ctx.ProtocolMessage.RedirectUri = publicOrigin!.TrimEnd('/') + "/signin-oidc";
                    return Task.CompletedTask;
                };
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidateAudience = true, ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    NameClaimType = "name",
                    RoleClaimType = "roles"
                };
            });
        }

        services.AddAuthorization(options =>
        {
            options.AddPolicy("Admin", p => p.AddAuthenticationSchemes(AdminCookie)
                .RequireAuthenticatedUser()
                .RequireAssertion(c => ScopeSecurity.HasAdminRole(c.User, "dynamics.admin")
                    || ScopeSecurity.HasAdminRole(c.User, "dynamics.security.admin")));
            options.AddPolicy("SecurityAdmin", p => p.AddAuthenticationSchemes(AdminCookie)
                .RequireAuthenticatedUser()
                .RequireAssertion(c => ScopeSecurity.HasAdminRole(c.User, "dynamics.security.admin")));
            options.AddPolicy("M2M", p => p.AddAuthenticationSchemes(MachineBearer)
                .RequireAuthenticatedUser()
                .RequireClaim("connector:issuer")
                .RequireAssertion(c => !string.IsNullOrWhiteSpace(ScopeSecurity.ClientId(c.User))));
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("AdminRate", http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 100, Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0, AutoReplenishment = true
                    }));
        });

        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__Host-dhole-dynamics-csrf";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.HttpOnly = true;
        });
        services.AddCors(options => options.AddPolicy("AdminSameOrigin", policy =>
        {
            var origins = config.GetSection("Admin:CorsOrigins").Get<string[]>() ?? [];
            if (origins.Length > 0)
            {
                if (origins.Any(origin => !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                    || uri.Scheme != "https" || uri.AbsolutePath != "/" || uri.UserInfo.Length > 0))
                    throw new InvalidOperationException("Admin CORS origins must be explicit HTTPS origins.");
                policy.WithOrigins(origins).WithMethods("GET", "POST", "PUT", "DELETE")
                    .WithHeaders("Content-Type", "X-CSRF-TOKEN").AllowCredentials();
            }
        }));
        return services;
    }

    private static bool IsHttpsAuthority(string? authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.IsDefaultPort;

    public static IServiceCollection AddDholeApiInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Dynamics")
            ?? throw new InvalidOperationException("Missing required ConnectionStrings:Dynamics");
        services.AddSingleton(NpgsqlDataSource.Create(DatabaseIsolation.Validate(connectionString, true)));
        services.AddSingleton<ISecretProtector, Encryption>();
        services.AddScoped<IConnectionRepository, ConnectionRepository>();
        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddScoped<ClientAccessRepository>();
        services.AddScoped<MetadataRepository>();
        services.AddScoped<ReadAccessRepository>();
        services.AddScoped<AdminConsoleRepository>();
        services.AddScoped<AdminRecordService>();
        services.AddScoped<WriteAccessService>();
        services.AddScoped<WriteLedger>();
        services.AddScoped<DeletePolicyRepository>();
        services.AddScoped<GenericWriteService>();
        services.AddScoped<TemplateRepository>();
        services.AddScoped<LegacyMigrationService>();
        services.AddScoped<ExternalReferenceRepository>();
        services.AddScoped<QuoteLinkReconciliationService>();
        services.AddScoped<TemplateOrchestrator>();
        services.AddScoped<ActionPolicyRepository>();
        services.AddHttpClient<BoundActionExecutor>(http => http.Timeout=TimeSpan.FromSeconds(45))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {AllowAutoRedirect=false,UseCookies=false});
        services.AddHttpClient<DataverseWriteTransport>(http => http.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            { AllowAutoRedirect = false, UseCookies = false });
        services.AddScoped<SavedQueryRepository>();
        services.AddSingleton<ReadCursorProtector>();
        services.AddHttpClient<ReadSearchService>(h => h.Timeout = TimeSpan.FromSeconds(45))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddScoped<MetadataRefreshService>();
        services.AddHostedService<MetadataScheduler>();
        services.AddHttpClient<IMetadataDiscoveryClient, DataverseMetadataDiscovery>(h => h.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddScoped<QuoteApplicationService>();
        services.AddSingleton<DataverseTokenProvider>();
        services.AddSingleton<IDataverseAccessTokenProvider>(sp => sp.GetRequiredService<DataverseTokenProvider>());
        services.AddHttpClient<IDataverseClient, DataverseClient>(http => http.Timeout = TimeSpan.FromSeconds(60))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false
            });
        return services;
    }
}
