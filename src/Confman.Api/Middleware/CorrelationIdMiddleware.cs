using Serilog.Context;

namespace Confman.Api.Middleware;

/// <summary>
/// Middleware that generates or extracts correlation IDs for request tracing.
/// </summary>
public class CorrelationIdMiddleware
{
    private const string CorrelationIdHeader = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Get or generate correlation ID
        var correlationId = context.Request.Headers[CorrelationIdHeader].FirstOrDefault()
            ?? Guid.NewGuid().ToString("N");

        // Add to response headers
        context.Response.Headers[CorrelationIdHeader] = correlationId;

        // Store in HttpContext items for access in controllers
        context.Items["CorrelationId"] = correlationId;

        // Push to Serilog context for structured logging
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}

/// <summary>
/// Extension methods for correlation ID middleware.
/// </summary>
public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<CorrelationIdMiddleware>();
    }
}
