using System.Text;
using Confman.Api.Models;
using Confman.Api.Services;
using Confman.Api.Storage.Blobs;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Confman.Tests;

/// <summary>
/// Unit tests for BlobValueResolver.
/// Verifies inline vs blob path resolution and peer fetch with deduplication.
/// </summary>
public class BlobValueResolverTests
{
    private readonly IBlobStore _blobStore;
    private readonly IRaftCluster _cluster;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<BlobStoreOptions> _options;

    public BlobValueResolverTests()
    {
        _blobStore = Substitute.For<IBlobStore>();
        _cluster = Substitute.For<IRaftCluster>();
        _cluster.Members.Returns(new List<IRaftClusterMember>());

        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _options = Options.Create(new BlobStoreOptions
        {
            ClusterToken = "test-token",
        });
    }

    [Fact]
    public async Task ResolveAsync_InlineEntry_ReturnsValueDirectly()
    {
        var entry = new ConfigEntry
        {
            Namespace = "ns",
            Key = "key",
            Value = "inline-value",
            UpdatedBy = "author",
        };

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(entry);

        Assert.Equal("inline-value", result);
        // Should not touch blob store for inline entries
        await _blobStore.DidNotReceive().OpenReadAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_BlobBackedEntry_LocalHit_ReturnsDecompressedValue()
    {
        var blobId = new string('a', 64);
        var entry = new ConfigEntry
        {
            Namespace = "ns",
            Key = "key",
            BlobId = blobId,
            UpdatedBy = "author",
        };

        // Create a compressed blob in memory
        using var compressedStream = new MemoryStream();
        using (var sourceStream = new MemoryStream(Encoding.UTF8.GetBytes("blob-value")))
        {
            await BlobCompression.HashAndCompressAsync(sourceStream, compressedStream);
        }

        _blobStore.OpenReadAsync(blobId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var ms = new MemoryStream(compressedStream.ToArray());
                return Task.FromResult<Stream?>(ms);
            });

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(entry);

        Assert.Equal("blob-value", result);
    }

    [Fact]
    public async Task ResolveAsync_BlobBackedEntry_LocalMiss_NoPeers_ReturnsNull()
    {
        var blobId = new string('b', 64);
        var entry = new ConfigEntry
        {
            Namespace = "ns",
            Key = "key",
            BlobId = blobId,
            UpdatedBy = "author",
        };

        _blobStore.OpenReadAsync(blobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(null));

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(entry);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_InlineEntry_NullValue_ReturnsNull()
    {
        // Edge case: inline entry with null value (shouldn't happen in practice, but handle gracefully)
        var entry = new ConfigEntry
        {
            Namespace = "ns",
            Key = "key",
            Value = null,
            UpdatedBy = "author",
        };

        var resolver = CreateResolver();
        var result = await resolver.ResolveAsync(entry);

        Assert.Null(result);
    }

    private BlobValueResolver CreateResolver() =>
        new(_blobStore, _cluster, _httpClientFactory, _options,
            NullLogger<BlobValueResolver>.Instance);
}
