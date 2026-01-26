using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Confman.Api.Auth;

/// <summary>
/// Options for API key authentication.
/// </summary>
public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
}

/// <summary>
/// Authentication handler that validates API keys from the X-Api-Key header.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly IConfiguration _configuration;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Check for API key header
        if (!Request.Headers.TryGetValue(ApiKeyAuthenticationOptions.HeaderName, out var apiKeyHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var providedApiKey = apiKeyHeader.ToString();
        if (string.IsNullOrWhiteSpace(providedApiKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Look up the API key in configuration
        var apiKeys = _configuration.GetSection("Auth:ApiKeys").Get<ApiKeyConfig[]>() ?? [];

        var matchingKey = apiKeys.FirstOrDefault(k =>
            string.Equals(k.Key, providedApiKey, StringComparison.Ordinal));

        if (matchingKey is null)
        {
            Logger.LogWarning("Invalid API key provided from {RemoteIp}",
                Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        }

        // Build claims from the matching API key configuration
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, matchingKey.Name),
            new(ClaimTypes.NameIdentifier, matchingKey.Name),
            new("api_key_name", matchingKey.Name)
        };

        // Add role claims
        foreach (var role in matchingKey.Roles ?? [])
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        // Add allowed namespaces as claims
        foreach (var ns in matchingKey.Namespaces ?? [])
        {
            claims.Add(new Claim("namespace", ns));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        Logger.LogDebug("Authenticated API key: {Name} with roles: {Roles}",
            matchingKey.Name, string.Join(", ", matchingKey.Roles ?? []));

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

/// <summary>
/// Configuration for an API key.
/// </summary>
public class ApiKeyConfig
{
    public required string Key { get; set; }
    public required string Name { get; set; }
    public string[] Roles { get; set; } = [];
    public string[] Namespaces { get; set; } = ["*"];
}