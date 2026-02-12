using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Confman.Api.Storage.Blobs;

/// <summary>
/// File-based content-addressed blob store.
/// Layout: {dataPath}/blobs/{blobId[0..2]}/{blobId}
/// Atomic writes: temp file → fsync → rename.
/// Thread-safe via atomic File.Move + IOException catch for concurrent same-BlobId writes.
/// </summary>
public sealed partial class LocalBlobStore : IBlobStore
{
    private readonly string _blobsRoot;
    private readonly long _maxDecompressedBytes;
    private readonly ILogger<LocalBlobStore> _logger;

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex BlobIdPattern();

    public LocalBlobStore(IConfiguration configuration, IOptions<BlobStoreOptions> options, ILogger<LocalBlobStore> logger)
    {
        _logger = logger;
        _maxDecompressedBytes = options.Value.MaxDecompressedSizeBytes;
        var dataPath = configuration["Storage:DataPath"] ?? "./data";
        _blobsRoot = Path.Combine(dataPath, "blobs");
        Directory.CreateDirectory(_blobsRoot);

        // Clean up orphan temp files from previous crashes
        CleanupTempFiles();
    }

    public Task<bool> ExistsAsync(string blobId, CancellationToken ct = default)
    {
        ValidateBlobId(blobId);
        var exists = File.Exists(BlobPath(blobId));
        _logger.LogDebug("Blob exists check {BlobId}: {Exists}", blobId[..8], exists);
        return Task.FromResult(exists);
    }

    public async Task<string> PutFromStreamAsync(Stream source, long contentLength, CancellationToken ct = default)
    {
        // Write to temp file with single-pass hash+compress
        var tempPath = TempPath();
        string blobId;

        try
        {
            // Don't preallocate: compressed size is unpredictable (could be 100× smaller)
            await using (var fs = CreateWriteStream(tempPath, estimatedSize: 0))
            {
                blobId = await BlobCompression.HashAndCompressAsync(source, fs, ct);
                // fsync the file data to disk
                fs.Flush(flushToDisk: true);
            }

            ValidateBlobId(blobId);
            var finalPath = BlobPath(blobId);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

            // Atomic rename. If another concurrent write already placed the file, catch IOException.
            try
            {
                File.Move(tempPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Same content already stored by concurrent write — delete our temp file
                File.Delete(tempPath);
                _logger.LogDebug("Blob {BlobId} already exists (concurrent write)", blobId[..8]);
            }

            _logger.LogDebug("Stored blob {BlobId} ({Bytes} bytes compressed)", blobId[..8], new FileInfo(finalPath).Length);
            return blobId;
        }
        catch
        {
            // Cleanup temp file on any failure
            TryDeleteFile(tempPath);
            throw;
        }
    }

    public async Task PutCompressedAsync(string blobId, Stream source, long contentLength, CancellationToken ct = default)
    {
        ValidateBlobId(blobId);

        // Skip if already exists (idempotent)
        var finalPath = BlobPath(blobId);
        if (File.Exists(finalPath))
        {
            _logger.LogDebug("Blob {BlobId} already exists, skipping put", blobId[..8]);
            return;
        }

        var tempPath = TempPath();
        try
        {
            // Copy compressed stream to temp file
            await using (var fs = CreateWriteStream(tempPath, contentLength))
            {
                await source.CopyToAsync(fs, ct);
                fs.Flush(flushToDisk: true);
            }

            // Validate hash by decompressing and computing SHA256
            await using (var readStream = File.OpenRead(tempPath))
            {
                var valid = await BlobCompression.ValidateAsync(blobId, readStream, _maxDecompressedBytes, ct);
                if (!valid)
                {
                    throw new InvalidOperationException(
                        $"Blob hash validation failed: expected {blobId}");
                }
            }

            // Atomic rename
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            try
            {
                File.Move(tempPath, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                File.Delete(tempPath);
                _logger.LogDebug("Blob {BlobId} already exists (concurrent write)", blobId[..8]);
            }

            _logger.LogDebug("Stored compressed blob {BlobId}", blobId[..8]);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    public Task<Stream?> OpenReadAsync(string blobId, CancellationToken ct = default)
    {
        ValidateBlobId(blobId);

        var path = BlobPath(blobId);
        if (!File.Exists(path))
        {
            _logger.LogDebug("Blob {BlobId} not found locally", blobId[..8]);
            return Task.FromResult<Stream?>(null);
        }

        var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 81_920,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        });

        _logger.LogDebug("Opened blob {BlobId} for read ({Bytes} bytes)", blobId[..8], stream.Length);
        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAsync(string blobId, CancellationToken ct = default)
    {
        ValidateBlobId(blobId);
        _logger.LogDebug("Deleting blob {BlobId}", blobId[..8]);
        TryDeleteFile(BlobPath(blobId));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListBlobIdsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(_blobsRoot))
            yield break;

        foreach (var subDir in Directory.EnumerateDirectories(_blobsRoot))
        {
            foreach (var file in Directory.EnumerateFiles(subDir))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (BlobIdPattern().IsMatch(name))
                    yield return name;
            }

            // Yield between subdirectories to stay responsive
            await Task.Yield();
        }
    }

    private string BlobPath(string blobId) =>
        Path.Combine(_blobsRoot, blobId[..2], blobId);

    private string TempPath() =>
        Path.Combine(_blobsRoot, $".tmp-{Guid.NewGuid():N}");

    private static FileStream CreateWriteStream(string path, long estimatedSize)
    {
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 81_920,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            PreallocationSize = estimatedSize > 0 ? (long)(estimatedSize * 0.6) : 0,
        });
    }

    private static void ValidateBlobId(string blobId)
    {
        if (!BlobIdPattern().IsMatch(blobId))
            throw new ArgumentException($"Invalid blob ID: must be 64 hex characters, got '{blobId}'", nameof(blobId));
    }

    private void CleanupTempFiles()
    {
        if (!Directory.Exists(_blobsRoot))
            return;

        var cleaned = 0;
        foreach (var tmp in Directory.EnumerateFiles(_blobsRoot, ".tmp-*"))
        {
            TryDeleteFile(tmp);
            cleaned++;
        }

        if (cleaned > 0)
            _logger.LogInformation("Cleaned up {Count} orphan temp files on startup", cleaned);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}