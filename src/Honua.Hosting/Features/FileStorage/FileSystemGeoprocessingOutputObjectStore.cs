// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

/// <summary>
/// Shared-filesystem implementation of <see cref="IGeoprocessingOutputObjectStore"/>
/// for local worker-pool placement (#3089). The worker and serving hosts mount the
/// same root directory; object keys map to contained relative paths. Writes are
/// staged to a temporary sibling and moved into place so a crashed write never
/// leaves a readable partial object at the final key. Read leases are sidecar
/// files holding a UTC expiry, honored by the orphan sweeper.
/// </summary>
internal sealed class FileSystemGeoprocessingOutputObjectStore : IGeoprocessingOutputObjectStore
{
    internal const string ReadLeaseSuffix = ".readlease";
    internal const string RetentionHoldSuffix = ".hold";
    internal const string PendingSuffix = ".pending";

    private readonly string _root;
    private readonly TimeSpan _pendingRetention;
    private readonly object _pendingGate = new();
    private readonly HashSet<string> _activePendingWrites = new(StringComparer.Ordinal);

    public FileSystemGeoprocessingOutputObjectStore(IOptions<GeoprocessingOutputStagingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        StoreReference = value.StoreReference;
        if (string.IsNullOrWhiteSpace(value.LocalRootPath))
        {
            throw new InvalidOperationException(
                "Geoprocessing:OutputStaging:LocalRootPath is required for the local staging provider.");
        }

        _root = Path.GetFullPath(value.LocalRootPath);
        _pendingRetention = value.SweepGrace;
        Directory.CreateDirectory(_root);
    }

    public CloudStorageProvider Provider => CloudStorageProvider.Local;

    public string StoreReference { get; }

    public async Task<RasterContentIdentity> WriteAsync(
        string objectKey,
        Stream content,
        string mediaType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);
        var path = ResolveContainedPath(objectKey);
        if (File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Staged output object '{objectKey}' already exists; attempt-scoped keys are written at most once.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pendingPath = path + PendingSuffix;

        lock (_pendingGate)
        {
            if (!_activePendingWrites.Add(pendingPath))
            {
                throw new InvalidOperationException(
                    $"Staged output object '{objectKey}' already has an active write.");
            }
        }

        long size;
        byte[] digest;
        try
        {
            // Share only delete/rename access and keep this handle alive through the
            // final move. A sweeper in another process probes the pending file and
            // acquires the same byte-range lock before reclaiming it, so this handle
            // is the durable cross-process proof that publication is still active.
            await using (var target = new FileStream(
                pendingPath, FileMode.Create, FileAccess.Write, FileShare.Delete, 81920, useAsync: true))
            using (var sha = SHA256.Create())
            {
                if (!OperatingSystem.IsMacOS())
                {
                    target.Lock(0, 1);
                }
                await using var hashing = new CryptoStream(target, sha, CryptoStreamMode.Write, leaveOpen: true);
                await content.CopyToAsync(hashing, cancellationToken).ConfigureAwait(false);
                await hashing.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
                size = target.Length;
                digest = sha.Hash!;

                // FileShare.Delete permits the atomic rename while retaining the
                // writer claim until the final object exists.
                File.Move(pendingPath, path, overwrite: false);
            }
        }
        catch
        {
            TryDeleteQuietly(pendingPath);
            throw;
        }
        finally
        {
            lock (_pendingGate)
            {
                _activePendingWrites.Remove(pendingPath);
            }
        }

        return new RasterContentIdentity
        {
            SizeBytes = size,
            MediaType = mediaType,
            Checksum = new RasterChecksum("sha256", Convert.ToHexString(digest).ToLowerInvariant()),
        };
    }

    public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var path = ResolveContainedPath(objectKey);
        if (!File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    public Task<GeoprocessingStagedObjectInfo?> GetInfoAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveContainedPath(objectKey);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return Task.FromResult<GeoprocessingStagedObjectInfo?>(null);
        }

        return Task.FromResult<GeoprocessingStagedObjectInfo?>(
            new GeoprocessingStagedObjectInfo(objectKey, info.Length, info.LastWriteTimeUtc));
    }

    public async IAsyncEnumerable<GeoprocessingStagedObjectInfo> ListAsync(
        string keyPrefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prefixPath = ResolveContainedPath(keyPrefix.TrimEnd('/'));
        if (!Directory.Exists(prefixPath))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(prefixPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.EndsWith(PendingSuffix, StringComparison.Ordinal))
            {
                TryReclaimAbandonedPending(file);
                continue;
            }

            if (file.EndsWith(ReadLeaseSuffix, StringComparison.Ordinal)
                || file.EndsWith(RetentionHoldSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var info = new FileInfo(file);
            var relative = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            yield return new GeoprocessingStagedObjectInfo(relative, info.Length, info.LastWriteTimeUtc);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void TryReclaimAbandonedPending(string pendingPath)
    {
        lock (_pendingGate)
        {
            if (_activePendingWrites.Contains(pendingPath))
            {
                return;
            }

            var info = new FileInfo(pendingPath);
            if (!info.Exists || DateTimeOffset.UtcNow - info.LastWriteTimeUtc < _pendingRetention)
            {
                return;
            }

            try
            {
                // FileStream byte-range locking is unavailable on macOS. Do not
                // reclaim there when this process cannot prove the writer is gone.
                if (OperatingSystem.IsMacOS())
                {
                    return;
                }

                // _activePendingWrites is process-local. The exclusive access probe
                // and advisory byte-range lock also coordinate with writers in other
                // server/worker processes on Windows and Linux.
                // FileShare.Delete lets this process unlink the abandoned file while
                // retaining the claim, closing the probe-to-delete race.
                using var claim = new FileStream(
                    pendingPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.Delete);
                claim.Lock(0, 1);
                info.Refresh();
                if (!info.Exists || DateTimeOffset.UtcNow - info.LastWriteTimeUtc < _pendingRetention)
                {
                    return;
                }

                File.Delete(pendingPath);
                PruneEmptyDirectories(info.DirectoryName);
            }
            catch (IOException)
            {
                // A writer still owns the pending object, or it completed between
                // enumeration and the claim. Either state is not reclaimable.
            }
            catch (UnauthorizedAccessException)
            {
                // Treat an object that cannot be claimed as active/fail closed.
            }
        }
    }

    public Task<bool> DeleteAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var path = ResolveContainedPath(objectKey);
        var existed = File.Exists(path);
        if (existed)
        {
            File.Delete(path);
        }

        TryDeleteQuietly(path + ReadLeaseSuffix);
        TryDeleteQuietly(path + RetentionHoldSuffix);
        PruneEmptyDirectories(Path.GetDirectoryName(path));
        return Task.FromResult(existed);
    }

    public async Task<bool> TryAcquireReadLeaseAsync(
        string objectKey,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var path = ResolveContainedPath(objectKey);
        if (!File.Exists(path))
        {
            return false;
        }

        // Refresh atomically: a truncate-then-write (FileMode.Create) leaves a window
        // where a concurrent sweeper reads an empty/partial sidecar. Writing a sibling
        // temp file and moving it into place means the sidecar is always either the
        // old complete lease or the new complete lease.
        var expiry = DateTimeOffset.UtcNow.Add(duration);
        var leasePath = path + ReadLeaseSuffix;
        var tempPath = leasePath + "." + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(
            tempPath,
            expiry.UtcTicks.ToString(CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, leasePath, overwrite: true);
        return true;
    }

    public async Task<bool> HasActiveReadLeaseAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        var leasePath = ResolveContainedPath(objectKey) + ReadLeaseSuffix;
        if (!File.Exists(leasePath))
        {
            return false;
        }

        try
        {
            var text = await File.ReadAllTextAsync(leasePath, cancellationToken).ConfigureAwait(false);
            if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
            {
                // An existing sidecar we cannot parse (torn concurrent refresh) must
                // count as ACTIVE — failing open here would let the sweeper delete an
                // object that is being read right now. The next successful refresh or
                // lease expiry resolves the ambiguity.
                return true;
            }

            return new DateTimeOffset(ticks, TimeSpan.Zero) > DateTimeOffset.UtcNow;
        }
        catch (IOException)
        {
            // A concurrently rewritten lease is treated as active; the sweeper retries later.
            return true;
        }
    }

    public Task<GeoprocessingRetentionHoldResult> SetRetentionHoldAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolveContainedPath(objectKey);
        if (!File.Exists(path))
        {
            return Task.FromResult(GeoprocessingRetentionHoldResult.ObjectMissing);
        }

        var holdPath = path + RetentionHoldSuffix;
        try
        {
            using var hold = new FileStream(holdPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(hold);
            writer.Write("held");
            return Task.FromResult(GeoprocessingRetentionHoldResult.Added);
        }
        catch (IOException) when (File.Exists(holdPath))
        {
            return Task.FromResult(GeoprocessingRetentionHoldResult.AlreadyHeld);
        }
    }

    public Task<bool> HasRetentionHoldAsync(string objectKey, CancellationToken cancellationToken = default)
        => Task.FromResult(File.Exists(ResolveContainedPath(objectKey) + RetentionHoldSuffix));

    public Task ReleaseRetentionHoldAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(ResolveContainedPath(objectKey) + RetentionHoldSuffix);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps an object key to a contained absolute path, rejecting rooted keys, drive
    /// or scheme syntax, and traversal escapes (mirrors the worker's VSI containment).
    /// </summary>
    private string ResolveContainedPath(string objectKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        if (objectKey[0] is '/' or '\\'
            || objectKey.Contains('\\')
            || objectKey.Contains("..", StringComparison.Ordinal)
            || objectKey.Contains("://", StringComparison.Ordinal)
            || Path.IsPathRooted(objectKey))
        {
            throw new ArgumentException($"Object key '{objectKey}' is not a contained relative key.", nameof(objectKey));
        }

        var combined = Path.GetFullPath(Path.Join(_root, objectKey));
        if (!combined.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(combined, _root, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Object key '{objectKey}' escapes the staging root.", nameof(objectKey));
        }

        return combined;
    }

    private void PruneEmptyDirectories(string? directory)
    {
        while (!string.IsNullOrEmpty(directory)
               && directory.StartsWith(_root, StringComparison.Ordinal)
               && !string.Equals(directory, _root, StringComparison.Ordinal))
        {
            try
            {
                if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    return;
                }

                Directory.Delete(directory);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    private static void TryDeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temporary sidecar.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temporary sidecar.
        }
    }
}
