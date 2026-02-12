using System.Text;
using Confman.Api.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Confman.Tests;

/// <summary>
/// Unit tests for LocalBlobStore and BlobCompression.
/// Verifies content-addressed storage, atomic writes, idempotency,
/// hash validation, and path traversal protection.
/// </summary>
public class LocalBlobStoreTests : IDisposable
{
    private readonly string _tempPath;
    private readonly LocalBlobStore _store;

    public LocalBlobStoreTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"confman-blob-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempPath);

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataPath"] = _tempPath
            })
            .Build();

        var options = Options.Create(new BlobStoreOptions());
        _store = new LocalBlobStore(config, options, NullLogger<LocalBlobStore>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
            Directory.Delete(_tempPath, recursive: true);
    }

    #region Put/Get Round-Trip

    [Fact]
    public async Task PutFromStream_And_OpenRead_RoundTrip()
    {
        var content = "Hello, Blob Store!";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var blobId = await _store.PutFromStreamAsync(source, source.Length);

        Assert.NotNull(blobId);
        Assert.Matches("^[0-9a-f]{64}$", blobId);

        // Read back and decompress
        await using var readStream = await _store.OpenReadAsync(blobId);
        Assert.NotNull(readStream);

        var result = await BlobCompression.DecompressToStringAsync(readStream!);
        Assert.Equal(content, result);
    }

    [Fact]
    public async Task PutFromStream_LargeValue_RoundTrip()
    {
        // 100KB value — above inline threshold
        var content = new string('X', 100_000);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var blobId = await _store.PutFromStreamAsync(source, source.Length);
        Assert.NotNull(blobId);

        await using var readStream = await _store.OpenReadAsync(blobId);
        Assert.NotNull(readStream);

        var result = await BlobCompression.DecompressToStringAsync(readStream!);
        Assert.Equal(content, result);
    }

    #endregion

    #region Idempotency

    [Fact]
    public async Task PutFromStream_SameContentTwice_ReturnsSameBlobId()
    {
        var content = "duplicate content";

        using var source1 = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var blobId1 = await _store.PutFromStreamAsync(source1, source1.Length);

        using var source2 = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var blobId2 = await _store.PutFromStreamAsync(source2, source2.Length);

        Assert.Equal(blobId1, blobId2);
    }

    #endregion

    #region Exists

    [Fact]
    public async Task ExistsAsync_ReturnsTrue_WhenBlobStored()
    {
        var content = "exists check";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var blobId = await _store.PutFromStreamAsync(source, source.Length);

        Assert.True(await _store.ExistsAsync(blobId));
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalse_WhenBlobNotStored()
    {
        // Valid hex-64 that was never stored
        var fakeBlobId = new string('a', 64);
        Assert.False(await _store.ExistsAsync(fakeBlobId));
    }

    #endregion

    #region Delete

    [Fact]
    public async Task DeleteAsync_RemovesBlob()
    {
        var content = "to be deleted";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));

        var blobId = await _store.PutFromStreamAsync(source, source.Length);
        Assert.True(await _store.ExistsAsync(blobId));

        await _store.DeleteAsync(blobId);
        Assert.False(await _store.ExistsAsync(blobId));
    }

    [Fact]
    public async Task DeleteAsync_NonExistent_NoOp()
    {
        var fakeBlobId = new string('b', 64);
        await _store.DeleteAsync(fakeBlobId); // Should not throw
    }

    #endregion

    #region ListBlobIds

    [Fact]
    public async Task ListBlobIdsAsync_ReturnsAllStoredBlobs()
    {
        var blobIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            using var source = new MemoryStream(Encoding.UTF8.GetBytes($"blob-{i}"));
            blobIds.Add(await _store.PutFromStreamAsync(source, source.Length));
        }

        var listed = new List<string>();
        await foreach (var id in _store.ListBlobIdsAsync())
        {
            listed.Add(id);
        }

        Assert.Equal(blobIds.OrderBy(x => x), listed.OrderBy(x => x));
    }

    #endregion

    #region Concurrent Put

    [Fact]
    public async Task PutFromStream_ConcurrentSameBlobId_BothSucceed()
    {
        var content = "concurrent content";

        var tasks = Enumerable.Range(0, 5).Select(async _ =>
        {
            using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));
            return await _store.PutFromStreamAsync(source, source.Length);
        });

        var results = await Task.WhenAll(tasks);

        // All should return the same BlobId
        Assert.All(results, id => Assert.Equal(results[0], id));
        Assert.True(await _store.ExistsAsync(results[0]));
    }

    #endregion

    #region PutCompressed with Validation

    [Fact]
    public async Task PutCompressedAsync_ValidBlob_Succeeds()
    {
        // First, create a compressed blob manually
        var content = "compressed content test";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));
        using var compressed = new MemoryStream();
        var blobId = await BlobCompression.HashAndCompressAsync(source, compressed);

        // Now put the compressed blob
        compressed.Position = 0;
        await _store.PutCompressedAsync(blobId, compressed, compressed.Length);

        Assert.True(await _store.ExistsAsync(blobId));

        // Read back
        await using var readStream = await _store.OpenReadAsync(blobId);
        var result = await BlobCompression.DecompressToStringAsync(readStream!);
        Assert.Equal(content, result);
    }

    [Fact]
    public async Task PutCompressedAsync_InvalidHash_Throws()
    {
        var content = "some content";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));
        using var compressed = new MemoryStream();
        await BlobCompression.HashAndCompressAsync(source, compressed);

        // Use wrong blobId
        compressed.Position = 0;
        var wrongBlobId = new string('c', 64);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.PutCompressedAsync(wrongBlobId, compressed, compressed.Length));
    }

    #endregion

    #region BlobId Validation (Path Traversal Protection)

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("not-hex-at-all")]
    [InlineData("ABC123")] // uppercase — must be lowercase hex
    [InlineData("")]
    [InlineData("a")] // too short
    public async Task InvalidBlobId_ThrowsArgumentException(string badBlobId)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.ExistsAsync(badBlobId));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.OpenReadAsync(badBlobId));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.DeleteAsync(badBlobId));
    }

    #endregion

    #region BlobCompression

    [Fact]
    public async Task HashAndCompress_ProducesDeterministicBlobId()
    {
        var content = "deterministic hash test";

        using var source1 = new MemoryStream(Encoding.UTF8.GetBytes(content));
        using var dest1 = new MemoryStream();
        var hash1 = await BlobCompression.HashAndCompressAsync(source1, dest1);

        using var source2 = new MemoryStream(Encoding.UTF8.GetBytes(content));
        using var dest2 = new MemoryStream();
        var hash2 = await BlobCompression.HashAndCompressAsync(source2, dest2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public async Task Validate_CorrectHash_ReturnsTrue()
    {
        var content = "validate this";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));
        using var compressed = new MemoryStream();
        var blobId = await BlobCompression.HashAndCompressAsync(source, compressed);

        compressed.Position = 0;
        Assert.True(await BlobCompression.ValidateAsync(blobId, compressed));
    }

    [Fact]
    public async Task Validate_WrongHash_ReturnsFalse()
    {
        var content = "validate this";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));
        using var compressed = new MemoryStream();
        await BlobCompression.HashAndCompressAsync(source, compressed);

        compressed.Position = 0;
        var wrongHash = new string('d', 64);
        Assert.False(await BlobCompression.ValidateAsync(wrongHash, compressed));
    }

    #endregion

    #region Temp File Cleanup

    [Fact]
    public void Constructor_CleansUpOrphanTempFiles()
    {
        // Create some temp files in the blobs directory
        var blobsDir = Path.Combine(_tempPath, "blobs");
        Directory.CreateDirectory(blobsDir);
        File.WriteAllText(Path.Combine(blobsDir, ".tmp-orphan1"), "data");
        File.WriteAllText(Path.Combine(blobsDir, ".tmp-orphan2"), "data");

        // Create a new store — should clean up temp files
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataPath"] = _tempPath
            })
            .Build();

        _ = new LocalBlobStore(config, Options.Create(new BlobStoreOptions()), NullLogger<LocalBlobStore>.Instance);

        Assert.Empty(Directory.EnumerateFiles(blobsDir, ".tmp-*"));
    }

    #endregion
}
