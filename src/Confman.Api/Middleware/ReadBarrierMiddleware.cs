using System.Diagnostics;
using System.Text.Json;
using DotNext.Net.Cluster.Consensus.Raft;

namespace Confman.Api.Middleware;

/// <summary>
/// Middleware that applies a Raft read barrier before GET/HEAD requests to /api paths.
/// Ensures linearizable reads by confirming the local state machine is up-to-date
/// with the cluster's committed state before serving data.
/// </summary>
public class ReadBarrierMiddleware
{
    private const string ApiPathPrefix = "/api";
    private const int DefaultTimeoutMs = 5000;
    private const string DefaultFailureMode = "reject";
    private const int RetryAfterSeconds = 2;

    private static readonly JsonSerializerOptions ProblemDetailsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<ReadBarrierMiddleware> _logger;

    public ReadBarrierMiddleware(RequestDelegate next, ILoggerFactory loggerFactory)
    {
        _next = next;
        _logger = loggerFactory.CreateLogger<ReadBarrierMiddleware>();
    }

    public async Task InvokeAsync(HttpContext context, IRaftCluster cluster, IConfiguration configuration)
    {
        if (!ShouldApplyBarrier(context))
        {
            await _next(context);
            return;
        }

        var enabled = configuration.GetValue("ReadBarrier:Enabled", true);
        if (!enabled)
        {
            await _next(context);
            return;
        }

        var timeoutMs = configuration.GetValue("ReadBarrier:TimeoutMs", DefaultTimeoutMs);
        var failureMode = configuration.GetValue("ReadBarrier:FailureMode", DefaultFailureMode) ?? DefaultFailureMode;

        if (!IsValidFailureMode(failureMode))
        {
            _logger.LogWarning("Unrecognized ReadBarrier:FailureMode '{FailureMode}', falling back to 'reject'", failureMode);
            failureMode = DefaultFailureMode;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        var stopwatch = Stopwatch.StartNew();
        var barrierSucceeded = false;

        try
        {
            await cluster.ApplyReadBarrierAsync(timeoutCts.Token);
            stopwatch.Stop();
            barrierSucceeded = true;

            _logger.LogDebug("Read barrier succeeded in {ElapsedMs}ms for {Method} {Path}",
                stopwatch.ElapsedMilliseconds, context.Request.Method, context.Request.Path);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected — nothing to do
            _logger.LogDebug("Client disconnected during read barrier for {Method} {Path}",
                context.Request.Method, context.Request.Path);
            return;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            _logger.LogWarning(ex, "Read barrier failed after {ElapsedMs}ms for {Method} {Path} (mode: {FailureMode})",
                stopwatch.ElapsedMilliseconds, context.Request.Method, context.Request.Path, failureMode);

            if (failureMode != "stale")
            {
                await HandleBarrierFailureAsync(context, failureMode, ex);
                return;
            }

            // Stale mode: proceed to serve potentially-stale data
        }

        if (barrierSucceeded || failureMode == "stale")
        {
            await _next(context);
        }
    }

    private static bool ShouldApplyBarrier(HttpContext context)
    {
        var method = context.Request.Method;
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method))
        {
            return false;
        }

        return context.Request.Path.StartsWithSegments(ApiPathPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidFailureMode(string mode)
    {
        return mode is "reject" or "stale" or "timeout";
    }

    private async Task HandleBarrierFailureAsync(HttpContext context, string failureMode, Exception exception)
    {
        if (failureMode == "stale")
        {
            await _next(context);
            return;
        }

        var statusCode = failureMode == "timeout"
            ? StatusCodes.Status504GatewayTimeout
            : StatusCodes.Status503ServiceUnavailable;

        var problemDetails = new
        {
            type = statusCode == 503
                ? "https://tools.ietf.org/html/rfc7231#section-6.6.4"
                : "https://tools.ietf.org/html/rfc7231#section-6.6.5",
            title = "Read barrier failed",
            status = statusCode,
            detail = $"{exception.GetType().Name}: {exception.Message}. Configured failure mode: {failureMode}.",
            traceId = context.Items["CorrelationId"]?.ToString()
        };

        context.Response.StatusCode = statusCode;
        context.Response.Headers["Retry-After"] = RetryAfterSeconds.ToString();

        await context.Response.WriteAsJsonAsync(problemDetails, ProblemDetailsJsonOptions, "application/problem+json", context.RequestAborted);
    }
}

/// <summary>
/// Extension methods for read barrier middleware registration.
/// </summary>
public static class ReadBarrierMiddlewareExtensions
{
    public static IApplicationBuilder UseReadBarrier(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ReadBarrierMiddleware>();
    }
}