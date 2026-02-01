using System.Text.Json;
using Confman.Api.Middleware;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Confman.Tests;

/// <summary>
/// Unit tests for ReadBarrierMiddleware.
/// Uses NSubstitute to mock IRaftCluster since the interface hierarchy is deep.
/// </summary>
public class ReadBarrierMiddlewareTests
{
    private readonly IConfiguration _defaultConfig;

    public ReadBarrierMiddlewareTests()
    {
        _defaultConfig = BuildConfig("reject", true, 5000);
    }

    private static IConfiguration BuildConfig(string failureMode, bool enabled = true, int timeoutMs = 5000)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReadBarrier:Enabled"] = enabled.ToString(),
                ["ReadBarrier:FailureMode"] = failureMode,
                ["ReadBarrier:TimeoutMs"] = timeoutMs.ToString()
            })
            .Build();
    }

    private static ReadBarrierMiddleware CreateMiddleware(Action? onNext = null)
    {
        RequestDelegate next = _ =>
        {
            onNext?.Invoke();
            return Task.CompletedTask;
        };

        return new ReadBarrierMiddleware(next, NullLoggerFactory.Instance);
    }

    private static DefaultHttpContext CreateHttpContext(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<T?> ReadResponseBody<T>(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await JsonSerializer.DeserializeAsync<T>(context.Response.Body, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private static IRaftCluster CreateCluster(Exception? barrierException = null)
    {
        var cluster = Substitute.For<IRaftCluster>();

        if (barrierException is not null)
        {
            cluster.ApplyReadBarrierAsync(Arg.Any<CancellationToken>())
                .Returns(_ => throw barrierException);
        }
        else
        {
            cluster.ApplyReadBarrierAsync(Arg.Any<CancellationToken>())
                .Returns(ValueTask.CompletedTask);
        }

        return cluster;
    }

    // -- Happy path --------------------------------------------------------

    [Fact]
    public async Task GetApiPath_BarrierSucceeds_RequestProceeds()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(onNext: () => nextCalled = true);
        var context = CreateHttpContext(HttpMethods.Get, "/api/v1/configs");
        var cluster = CreateCluster();

        await middleware.InvokeAsync(context, cluster, _defaultConfig);

        Assert.True(nextCalled);
        await cluster.Received(1).ApplyReadBarrierAsync(Arg.Any<CancellationToken>());
    }

    // -- Failure modes -----------------------------------------------------

    [Fact]
    public async Task GetApiPath_BarrierFails_RejectMode_Returns503()
    {
        var middleware = CreateMiddleware();
        var context = CreateHttpContext(HttpMethods.Get, "/api/v1/namespaces");
        var cluster = CreateCluster(barrierException: new InvalidOperationException("No quorum"));
        var config = BuildConfig("reject");

        await middleware.InvokeAsync(context, cluster, config);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    [Fact]
    public async Task GetApiPath_BarrierFails_StaleMode_RequestProceeds()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(onNext: () => nextCalled = true);
        var context = CreateHttpContext(HttpMethods.Get, "/api/v1/namespaces");
        var cluster = CreateCluster(barrierException: new InvalidOperationException("No quorum"));
        var config = BuildConfig("stale");

        await middleware.InvokeAsync(context, cluster, config);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task GetApiPath_BarrierFails_TimeoutMode_Returns504()
    {
        var middleware = CreateMiddleware();
        var context = CreateHttpContext(HttpMethods.Get, "/api/v1/namespaces");
        var cluster = CreateCluster(barrierException: new InvalidOperationException("No quorum"));
        var config = BuildConfig("timeout");

        await middleware.InvokeAsync(context, cluster, config);

        Assert.Equal(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
    }

    [Fact]
    public async Task GetApiPath_BarrierTimeout_RejectMode_Returns503()
    {
        var middleware = CreateMiddleware();
        var context = CreateHttpContext(HttpMethods.Get, "/api/v1/namespaces");
        var cluster = CreateCluster(barrierException: new OperationCanceledException());
        var config = BuildConfig("reject");

        await middleware.InvokeAsync(context, cluster, config);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    // -- Method and path filtering -----------------------------------------

    [Fact]
    public async Task PutApiPath_BypassesBarrier()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(onNext: () => nextCalled = true);
        var context = CreateHttpContext(HttpMethods.Put, "/api/v1/namespaces/prod/config/key");
        var cluster = CreateCluster();

        await middleware.InvokeAsync(context, cluster, _defaultConfig);

        Assert.True(nextCalled);
        await cluster.DidNotReceive().ApplyReadBarrierAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetHealthPath_BypassesBarrier()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(onNext: () => nextCalled = true);
        var context = CreateHttpContext(HttpMethods.Get, "/health");
        var cluster = CreateCluster();

        await middleware.InvokeAsync(context, cluster, _defaultConfig);

        Assert.True(nextCalled);
        await cluster.DidNotReceive().ApplyReadBarrierAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HeadApiPath_BarrierApplied()
    {
        var middleware = CreateMiddleware();
        var context = CreateHttpContext(HttpMethods.Head, "/api/v1/namespaces");
        var cluster = CreateCluster();

        await middleware.InvokeAsync(context, cluster, _defaultConfig);

        await cluster.Received(1).ApplyReadBarrierAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteApiPath_BypassesBarrier()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(onNext: () => nextCalled = true);
        var context = CreateHttpContext(HttpMethods.Delete, "/api/v1/namespaces/prod/config/key");
        var cluster = CreateCluster();

        await middleware.InvokeAsync(context, cluster, _defaultConfig);

        Assert.True(nextCalled);
        await cluster.DidNotReceive().ApplyReadBarrierAsync(Arg.Any<CancellationToken>());
    }

    // -- Configuration -----------------------------------------------------

    [Fact]
    public async Task GetApiPath_BarrierDisabled_BypassesBarrier()
    {
        var nextCalled = false;
        var middleware = CreateMiddleware(onNext: () => nextCalled = true);
        var context = CreateHttpContext(HttpMethods.Get, "/api/v1/namespaces");
        var cluster = CreateCluster();
        var config = BuildConfig("reject", enabled: false);

        await middleware.InvokeAsync(context, cluster, config);

        Assert.True(nextCalled);
        await cluster.DidNotReceive().ApplyReadBarrierAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidFailureMode_FallsBackToReject()
    {
        var middleware = CreateMiddleware();
        var context = CreateHttpContext(HttpMethods.Get, "/api/v1/namespaces");
        var cluster = CreateCluster(barrierException: new InvalidOperationException("No quorum"));
        var config = BuildConfig("invalid_typo");

        await middleware.InvokeAsync(context, cluster, config);

        // Falls back to reject -> 503
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    }

    // -- Response format ---------------------------------------------------

    [Fact]
    public async Task ResponseIncludesRetryAfterHeader()
    {
        var middleware = CreateMiddleware();
        var context = CreateHttpContext(HttpMethods.Get, "/api/v1/namespaces");
        var cluster = CreateCluster(barrierException: new InvalidOperationException("No quorum"));
        var config = BuildConfig("reject");

        await middleware.InvokeAsync(context, cluster, config);

        Assert.Equal("2", context.Response.Headers["Retry-After"].ToString());
    }

    [Fact]
    public async Task ResponseIsProblemDetails()
    {
        var middleware = CreateMiddleware();
        var context = CreateHttpContext(HttpMethods.Get, "/api/v1/namespaces");
        var cluster = CreateCluster(barrierException: new InvalidOperationException("No quorum"));
        var config = BuildConfig("reject");

        await middleware.InvokeAsync(context, cluster, config);

        Assert.Equal("application/problem+json", context.Response.ContentType);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);
        var body = doc.RootElement;
        Assert.Equal("Read barrier failed", body.GetProperty("title").GetString());
        Assert.Equal(503, body.GetProperty("status").GetInt32());
        Assert.Contains("reject", body.GetProperty("detail").GetString());
    }
}
