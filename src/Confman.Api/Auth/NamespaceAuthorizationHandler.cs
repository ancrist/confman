using Microsoft.AspNetCore.Authorization;

namespace Confman.Api.Auth;

/// <summary>
/// Requirement for namespace access authorization.
/// </summary>
public class NamespaceAccessRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Handler that checks if the user has access to the requested namespace.
/// Admins have access to all namespaces.
/// </summary>
public class NamespaceAuthorizationHandler : AuthorizationHandler<NamespaceAccessRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        NamespaceAccessRequirement requirement)
    {
        // Admins can access everything
        if (context.User.IsInRole("admin"))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // Get the namespace from the route
        if (context.Resource is HttpContext httpContext)
        {
            var requestedNamespace = httpContext.Request.RouteValues["namespace"]?.ToString();

            if (string.IsNullOrEmpty(requestedNamespace))
            {
                // No namespace in route, allow (for list operations, etc.)
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            // Check if user has access to this namespace
            var allowedNamespaces = context.User.FindAll("namespace").Select(c => c.Value).ToList();

            // Wildcard allows all
            if (allowedNamespaces.Contains("*"))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }

            // Exact match or prefix match (for hierarchical namespaces)
            if (allowedNamespaces.Any(ns =>
                requestedNamespace.Equals(ns, StringComparison.OrdinalIgnoreCase) ||
                requestedNamespace.StartsWith(ns + "/", StringComparison.OrdinalIgnoreCase)))
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }
        }

        // Access denied
        return Task.CompletedTask;
    }
}

/// <summary>
/// Extension methods for registering authentication and authorization.
/// </summary>
public static class AuthExtensions
{
    public static IServiceCollection AddConfmanAuth(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthentication(ApiKeyAuthenticationOptions.DefaultScheme)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationOptions.DefaultScheme, _ => { });

        services.AddAuthorization(options =>
        {
            // Default policy requires authentication
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .Build();

            // Admin policy for management operations
            options.AddPolicy("Admin", policy =>
                policy.RequireRole("admin"));

            // ReadOnly policy for read operations
            options.AddPolicy("ReadOnly", policy =>
                policy.RequireAuthenticatedUser()
                    .RequireAssertion(ctx =>
                        ctx.User.IsInRole("admin") ||
                        ctx.User.IsInRole("read") ||
                        ctx.User.IsInRole("write")));

            // Write policy for write operations
            options.AddPolicy("Write", policy =>
                policy.RequireAuthenticatedUser()
                    .RequireAssertion(ctx =>
                        ctx.User.IsInRole("admin") ||
                        ctx.User.IsInRole("write")));

            // Namespace access policy
            options.AddPolicy("NamespaceAccess", policy =>
                policy.RequireAuthenticatedUser()
                    .AddRequirements(new NamespaceAccessRequirement()));
        });

        services.AddSingleton<IAuthorizationHandler, NamespaceAuthorizationHandler>();

        return services;
    }
}
