using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Confman.Api.Storage.Blobs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Confman.Api.Auth;

/// <summary>
/// Options for cluster token authentication used by internal blob endpoints.
/// </summary>
public class ClusterTokenAuthOptions : AuthenticationSchemeOptions
{
    public const string DefaultScheme = "ClusterToken";
    public const string HeaderName = "X-Cluster-Token";
}

/// <summary>
/// Authentication handler that validates the X-Cluster-Token header for inter-node communication.
/// Uses constant-time comparison to prevent timing side-channel attacks.
/// </summary>
public class ClusterTokenAuthenticationHandler : AuthenticationHandler<ClusterTokenAuthOptions>
{
    private readonly IOptions<BlobStoreOptions> _blobOptions;

    public ClusterTokenAuthenticationHandler(
        IOptionsMonitor<ClusterTokenAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<BlobStoreOptions> blobOptions)
        : base(options, logger, encoder)
    {
        _blobOptions = blobOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ClusterTokenAuthOptions.HeaderName, out var tokenHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var providedToken = tokenHeader.ToString();
        if (string.IsNullOrWhiteSpace(providedToken))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var expectedToken = _blobOptions.Value.ClusterToken;
        if (string.IsNullOrEmpty(expectedToken))
        {
            Logger.LogWarning("ClusterToken not configured — rejecting internal request");
            return Task.FromResult(AuthenticateResult.Fail("Cluster token not configured"));
        }

        // Constant-time comparison to prevent timing side-channel attacks
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        var providedBytes = Encoding.UTF8.GetBytes(providedToken);

        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes))
        {
            Logger.LogWarning("Invalid cluster token from {RemoteIp}",
                Context.Connection.RemoteIpAddress);
            return Task.FromResult(AuthenticateResult.Fail("Invalid cluster token"));
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "cluster-internal"),
            new Claim(ClaimTypes.Role, "cluster"),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
