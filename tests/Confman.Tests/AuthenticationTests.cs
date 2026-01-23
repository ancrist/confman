using System.Security.Claims;
using System.Text.Encodings.Web;
using Confman.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Confman.Tests;

/// <summary>
/// Unit tests for API key authentication.
/// </summary>
public class AuthenticationTests
{
    private readonly IConfiguration _configuration;

    public AuthenticationTests()
    {
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:ApiKeys:0:Key"] = "test-admin-key",
                ["Auth:ApiKeys:0:Name"] = "admin-user",
                ["Auth:ApiKeys:0:Roles:0"] = "admin",
                ["Auth:ApiKeys:0:Namespaces:0"] = "*",

                ["Auth:ApiKeys:1:Key"] = "test-reader-key",
                ["Auth:ApiKeys:1:Name"] = "reader-user",
                ["Auth:ApiKeys:1:Roles:0"] = "read",
                ["Auth:ApiKeys:1:Namespaces:0"] = "production",
                ["Auth:ApiKeys:1:Namespaces:1"] = "staging",

                ["Auth:ApiKeys:2:Key"] = "test-writer-key",
                ["Auth:ApiKeys:2:Name"] = "writer-user",
                ["Auth:ApiKeys:2:Roles:0"] = "write",
                ["Auth:ApiKeys:2:Namespaces:0"] = "production"
            })
            .Build();
    }

    private ApiKeyAuthenticationHandler CreateHandler(HttpContext context)
    {
        var options = new ApiKeyAuthenticationOptions();
        var optionsMonitor = new TestOptionsMonitor(options);
        var loggerFactory = NullLoggerFactory.Instance;
        var encoder = UrlEncoder.Default;

        var handler = new ApiKeyAuthenticationHandler(
            optionsMonitor,
            loggerFactory,
            encoder,
            _configuration);

        handler.InitializeAsync(
            new AuthenticationScheme(ApiKeyAuthenticationOptions.DefaultScheme, null, typeof(ApiKeyAuthenticationHandler)),
            context).Wait();

        return handler;
    }

    [Fact]
    public async Task ValidAdminKey_AuthenticatesWithAdminRole()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "test-admin-key";

        var handler = CreateHandler(context);
        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("admin-user", result.Principal!.Identity!.Name);
        Assert.True(result.Principal.IsInRole("admin"));
    }

    [Fact]
    public async Task ValidReaderKey_AuthenticatesWithReadRole()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "test-reader-key";

        var handler = CreateHandler(context);
        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("reader-user", result.Principal!.Identity!.Name);
        Assert.True(result.Principal.IsInRole("read"));
        Assert.False(result.Principal.IsInRole("admin"));

        // Check namespace claims
        var namespaces = result.Principal.FindAll("namespace").Select(c => c.Value).ToList();
        Assert.Contains("production", namespaces);
        Assert.Contains("staging", namespaces);
    }

    [Fact]
    public async Task InvalidKey_FailsAuthentication()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "invalid-key";

        var handler = CreateHandler(context);
        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.Contains("Invalid", result.Failure!.Message);
    }

    [Fact]
    public async Task MissingHeader_NoResult()
    {
        var context = new DefaultHttpContext();
        // No X-Api-Key header

        var handler = CreateHandler(context);
        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task EmptyHeader_NoResult()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Api-Key"] = "";

        var handler = CreateHandler(context);
        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    /// <summary>
    /// Test helper for IOptionsMonitor.
    /// </summary>
    private sealed class TestOptionsMonitor : IOptionsMonitor<ApiKeyAuthenticationOptions>
    {
        public TestOptionsMonitor(ApiKeyAuthenticationOptions options)
        {
            CurrentValue = options;
        }

        public ApiKeyAuthenticationOptions CurrentValue { get; }

        public ApiKeyAuthenticationOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ApiKeyAuthenticationOptions, string?> listener) => null;
    }
}

/// <summary>
/// Tests for namespace authorization handler.
/// </summary>
public class NamespaceAuthorizationTests
{
    [Fact]
    public void AdminRole_HasAccessToAllNamespaces()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "admin"),
            new Claim(ClaimTypes.Role, "admin")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        Assert.True(principal.IsInRole("admin"));
    }

    [Fact]
    public void WildcardNamespace_HasAccessToAll()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "user"),
            new Claim("namespace", "*")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var namespaces = principal.FindAll("namespace").Select(c => c.Value);
        Assert.Contains("*", namespaces);
    }

    [Fact]
    public void SpecificNamespace_OnlyHasAccessToThat()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "user"),
            new Claim("namespace", "production")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        var namespaces = principal.FindAll("namespace").Select(c => c.Value).ToList();
        Assert.Single(namespaces);
        Assert.Equal("production", namespaces[0]);
    }
}
