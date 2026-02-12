using System.Net;
using Confman.Api.Storage.Blobs;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Confman.Tests;

/// <summary>
/// Unit tests for PeerBlobReplicator.
/// Uses mocked IRaftCluster, IBlobStore, and HttpClient to verify quorum logic.
/// </summary>
public class PeerBlobReplicatorTests : IDisposable
{
    private readonly IBlobStore _blobStore;
    private readonly IRaftCluster _cluster;
    private readonly IOptions<BlobStoreOptions> _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TestHttpMessageHandler _httpHandler;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _blobId = new('a', 64);

    public PeerBlobReplicatorTests()
    {
        _blobStore = Substitute.For<IBlobStore>();
        _blobStore.OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(new MemoryStream(new byte[] { 1, 2, 3 })));

        _cluster = Substitute.For<IRaftCluster>();
        _options = Options.Create(new BlobStoreOptions
        {
            ClusterToken = "test-token",
        });

        _lifetime = Substitute.For<IHostApplicationLifetime>();
        _lifetime.ApplicationStopping.Returns(CancellationToken.None);

        _httpHandler = new TestHttpMessageHandler();
        _httpClientFactory = Substitute.For<IHttpClientFactory>();
        _httpClientFactory.CreateClient("BlobReplication")
            .Returns(_ => new HttpClient(_httpHandler));
    }

    public void Dispose()
    {
        _httpHandler.Dispose();
    }

    [Fact]
    public async Task ReplicateAsync_SingleNodeCluster_ReturnsImmediately()
    {
        SetupClusterMembers([]); // No remote members

        var replicator = CreateReplicator();
        await replicator.ReplicateAsync(_blobId); // Should not throw
    }

    [Fact]
    public async Task ReplicateAsync_BothFollowersRespond_Succeeds()
    {
        SetupClusterMembers([
            new Uri("http://localhost:6200"),
            new Uri("http://localhost:6300"),
        ]);

        _httpHandler.ResponseFactory = _ =>
            new HttpResponseMessage(HttpStatusCode.OK);

        var replicator = CreateReplicator();
        await replicator.ReplicateAsync(_blobId);
    }

    [Fact]
    public async Task ReplicateAsync_OneFollowerResponds_QuorumMet()
    {
        SetupClusterMembers([
            new Uri("http://localhost:6200"),
            new Uri("http://localhost:6300"),
        ]);

        var callCount = 0;
        _httpHandler.ResponseFactory = _ =>
        {
            var count = Interlocked.Increment(ref callCount);
            if (count == 1)
                return new HttpResponseMessage(HttpStatusCode.OK);
            // Second follower times out (simulate by delaying)
            throw new TaskCanceledException("Simulated timeout");
        };

        var replicator = CreateReplicator();
        // Quorum = 2 (leader + 1 follower). One ACK is enough.
        await replicator.ReplicateAsync(_blobId);
    }

    [Fact]
    public async Task ReplicateAsync_NoFollowersRespond_ThrowsBlobReplicationException()
    {
        SetupClusterMembers([
            new Uri("http://localhost:6200"),
            new Uri("http://localhost:6300"),
        ]);

        _httpHandler.ResponseFactory = _ =>
            throw new HttpRequestException("Connection refused");

        var replicator = CreateReplicator();
        await Assert.ThrowsAsync<BlobReplicationException>(() =>
            replicator.ReplicateAsync(_blobId));
    }

    [Fact]
    public async Task ReplicateAsync_FollowerReturns204_CountsAsAck()
    {
        // 204 = blob already exists (idempotent) — counts as ACK
        SetupClusterMembers([
            new Uri("http://localhost:6200"),
            new Uri("http://localhost:6300"),
        ]);

        _httpHandler.ResponseFactory = _ =>
            new HttpResponseMessage(HttpStatusCode.NoContent);

        var replicator = CreateReplicator();
        await replicator.ReplicateAsync(_blobId);
    }

    private PeerBlobReplicator CreateReplicator() =>
        new(
            _cluster,
            _blobStore,
            _httpClientFactory,
            _options,
            NullLogger<PeerBlobReplicator>.Instance,
            _lifetime);

    private void SetupClusterMembers(Uri[] remoteUris)
    {
        var members = new List<IRaftClusterMember>();

        // Add local member
        var localMember = Substitute.For<IRaftClusterMember>();
        localMember.IsRemote.Returns(false);
        members.Add(localMember);

        // Add remote members
        foreach (var uri in remoteUris)
        {
            var remoteMember = Substitute.For<IRaftClusterMember>();
            remoteMember.IsRemote.Returns(true);
            remoteMember.EndPoint.Returns(new UriEndPoint(uri));
            members.Add(remoteMember);
        }

        _cluster.Members.Returns(members);
    }

    /// <summary>
    /// Test HTTP handler that allows configuring responses per request.
    /// </summary>
    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> ResponseFactory { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = ResponseFactory(request);
            return Task.FromResult(response);
        }
    }
}
