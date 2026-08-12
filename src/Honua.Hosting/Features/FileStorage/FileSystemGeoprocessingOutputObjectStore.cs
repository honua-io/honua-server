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
    private const string PendingSuffix = ".pending";

    private readonly string _root;

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

        long size;
        byte[] digest;
        try
        {
            await using (var target = new FileStream(
                pendingPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var sha = SHA256.Create())
            {
                await using var hashing = new CryptoStream(target, sha, CryptoStreamMode.Write, leaveOpen: true);
                await content.CopyToAsync(hashing, cancellationToken).ConfigureAwait(false);
                await hashing.FlushFinalBlockAsync(cancellationToken).ConfigureAwait(false);
                size = target.Length;
                digest = sha.Hash!;
            }

            File.Move(pendingPath, path, overwrite: false);
        }
        catch
        {
            TryDeleteQuietly(pendingPath);
            throw;
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
            if (file.EndsWith(ReadLeaseSuffix, StringComparison.Ordinal)
                || file.EndsWith(PendingSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var info = new FileInfo(file);
            var relative = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            yield return new GeoprocessingStagedObjectInfo(relative, info.Length, info.LastWriteTimeUtc);
        }

        await Task.CompletedTask.ConfigureAwait(false);
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

        var expiry = DateTimeOffset.UtcNow.Add(duration);
        await File.WriteAllTextAsync(
            path + ReadLeaseSuffix,
            expiry.UtcTicks.ToString(CultureInfo.InvariantCulture),
            cancellationToken).ConfigureAwait(false);
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
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                && new DateTimeOffset(ticks, TimeSpan.Zero) > DateTimeOffset.UtcNow;
        }
        catch (IOException)
        {
            // A concurrently rewritten lease is treated as active; the sweeper retries later.
            return true;
        }
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

        var combined = Path.GetFullPath(Path.Combine(_root, objectKey));
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
        }
    }
}
