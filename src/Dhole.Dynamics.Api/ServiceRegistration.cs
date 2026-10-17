using System.Text;
using Dhole.Dynamics.Application;
using Dhole.Dynamics.Infrastructure;
using Dhole.Dynamics.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Dhole.Dynamics.Api;

public static class ServiceRegistration
{
    public static IServiceCollection AddDholeApiSecurity(this IServiceCollection services, IConfiguration config)
    {
        string Require(string key) => config[key] is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Missing required server configuration: {key}");

        var secret = Require("Jwt:SecretKey");
        if (Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("Jwt:SecretKey must be at least 32 bytes long.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = Require("Jwt:Issuer"),
                ValidateAudience = true, ValidAudience = Require("Jwt:Audience"),
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
                ValidateLifetime = true, RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromMinutes(1)
            };
        });
        services.AddAuthorization(options =>
        {
            options.AddPolicy("Connections", p => p.RequireAuthenticatedUser()
                .RequireAssertion(ctx => ScopeSecurity.HasScope(ctx.User, "dynamics.connections.manage")));
            options.AddPolicy("QuoteCreate", p => p.RequireAuthenticatedUser()
                .RequireAssertion(ctx => ScopeSecurity.HasScope(ctx.User, "dynamics.quotes.create")));
            options.AddPolicy("QuoteRead", p => p.RequireAuthenticatedUser()
                .RequireAssertion(ctx => ScopeSecurity.HasScope(ctx.User, "dynamics.quotes.read")));
        });
        return services;
    }

    public static IServiceCollection AddDholeApiInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Dynamics")
            ?? throw new InvalidOperationException("Missing required ConnectionStrings:Dynamics");
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<ISecretProtector, Encryption>();
        services.AddScoped<IConnectionRepository, ConnectionRepository>();
        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddScoped<QuoteApplicationService>();
        services.AddHttpClient<IDataverseClient, DataverseClient>(http => http.Timeout = TimeSpan.FromSeconds(60));
        return services;
    }
}
