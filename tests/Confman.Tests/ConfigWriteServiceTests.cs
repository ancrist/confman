using Confman.Api.Cluster;
using Confman.Api.Cluster.Commands;
using Confman.Api.Services;
using Confman.Api.Storage.Blobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Confman.Tests;

/// <summary>
/// Unit tests for ConfigWriteService.
/// Verifies threshold-based routing between inline and blob paths.
/// </summary>
public class ConfigWriteServiceTests
{
    private readonly IBlobStore _blobStore;
    private readonly IBlobReplicator _blobReplicator;
    private readonly IRaftService _raft;
    private readonly IOptions<BlobStoreOptions> _options;

    public ConfigWriteServiceTests()
    {
        _blobStore = Substitute.For<IBlobStore>();
        _blobReplicator = Substitute.For<IBlobReplicator>();
        _raft = Substitute.For<IRaftService>();
        _raft.ReplicateAsync(Arg.Any<ICommand>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _options = Options.Create(new BlobStoreOptions
        {
            Enabled = true,
            InlineThresholdBytes = 64,
            ClusterToken = "test-token",
        });

        _blobStore.PutFromStreamAsync(Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new string('a', 64));
    }

    [Fact]
    public async Task WriteAsync_SmallValue_UsesInlinePath()
    {
        var service = CreateService();

        var result = await service.WriteAsync("ns", "key", "small", "string", "author");

        Assert.True(result.Success);
        // Should NOT interact with blob store
        await _blobStore.DidNotReceive().PutFromStreamAsync(
            Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _blobReplicator.DidNotReceive().ReplicateAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Should replicate a SetConfigCommand (not blob ref)
        await _raft.Received(1).ReplicateAsync(
            Arg.Is<ICommand>(c => c is SetConfigCommand), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_LargeValue_UsesBlobPath()
    {
        var service = CreateService();
        var largeValue = new string('x', 100); // 100 bytes > 64 byte threshold

        var result = await service.WriteAsync("ns", "key", largeValue, "string", "author");

        Assert.True(result.Success);
        // Should interact with blob store and replicator
        await _blobStore.Received(1).PutFromStreamAsync(
            Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
        await _blobReplicator.Received(1).ReplicateAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Should replicate a SetConfigBlobRefCommand
        await _raft.Received(1).ReplicateAsync(
            Arg.Is<ICommand>(c => c is SetConfigBlobRefCommand), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_BlobReplicationFails_ReturnsErrorResult()
    {
        _blobReplicator.ReplicateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new BlobReplicationException("quorum unreachable")));

        var service = CreateService();
        var largeValue = new string('x', 100);

        var result = await service.WriteAsync("ns", "key", largeValue, "string", "author");

        Assert.False(result.Success);
        Assert.Contains("quorum", result.ErrorDetail);
        // Should NOT commit to Raft after blob failure
        await _raft.DidNotReceive().ReplicateAsync(
            Arg.Any<ICommand>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_RaftFailsAfterBlobQuorum_ReturnsErrorWithGhostWarning()
    {
        _raft.ReplicateAsync(Arg.Any<ICommand>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var service = CreateService();
        var largeValue = new string('x', 100);

        var result = await service.WriteAsync("ns", "key", largeValue, "string", "author");

        Assert.False(result.Success);
        Assert.Contains("Raft", result.ErrorDetail);
    }

    [Fact]
    public async Task WriteAsync_BlobStoreDisabled_AlwaysUsesInlinePath()
    {
        var disabledOptions = Options.Create(new BlobStoreOptions
        {
            Enabled = false,
            InlineThresholdBytes = 64,
            ClusterToken = "test-token",
        });
        var service = new ConfigWriteService(
            _blobStore, _blobReplicator, _raft, disabledOptions,
            NullLogger<ConfigWriteService>.Instance);

        var largeValue = new string('x', 100);

        var result = await service.WriteAsync("ns", "key", largeValue, "string", "author");

        Assert.True(result.Success);
        await _blobStore.DidNotReceive().PutFromStreamAsync(
            Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_ExactlyAtThreshold_UsesBlobPath()
    {
        // Exactly 64 bytes = threshold → blob path
        var exactValue = new string('x', 64);
        var service = CreateService();

        var result = await service.WriteAsync("ns", "key", exactValue, "string", "author");

        Assert.True(result.Success);
        await _blobStore.Received(1).PutFromStreamAsync(
            Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WriteAsync_OneBelowThreshold_UsesInlinePath()
    {
        // 63 bytes < 64 byte threshold → inline path
        var smallValue = new string('x', 63);
        var service = CreateService();

        var result = await service.WriteAsync("ns", "key", smallValue, "string", "author");

        Assert.True(result.Success);
        await _blobStore.DidNotReceive().PutFromStreamAsync(
            Arg.Any<Stream>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    private ConfigWriteService CreateService() =>
        new(_blobStore, _blobReplicator, _raft, _options,
            NullLogger<ConfigWriteService>.Instance);
}
