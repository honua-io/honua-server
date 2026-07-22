// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Server.Features.Orchestration;

/// <summary>
/// Poll-based baseline <see cref="IObjectStoreTriggerProbe"/> backed by local/mounted file
/// system roots. Each configured <see cref="ObjectStoreTriggerConfig.StoreId"/> maps to a root
/// directory; the probe lists files under the watched prefix and reports the newest one (by the
/// monotonic last-modified + key marker). An object-store/S3 push implementation can later return
/// the same <see cref="ObjectStoreProbeResult"/> without changing the scheduler dispatch.
/// </summary>
/// <remarks>
/// The file-system root is the natural baseline for "drop a file → pipeline runs" on a mounted
/// volume (NFS/EFS/local), and keeps the baseline dependency-free and AOT-safe.
/// </remarks>
internal sealed class FileSystemObjectStoreTriggerProbe : IObjectStoreTriggerProbe
{
    private readonly Dictionary<string, string> _roots;

    public FileSystemObjectStoreTriggerProbe(IReadOnlyDictionary<string, string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots = new Dictionary<string, string>(roots, StringComparer.Ordinal);
    }

    public Task<ObjectStoreProbeResult?> ProbeNewestAsync(
        ObjectStoreTriggerConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_roots.TryGetValue(config.StoreId, out var root) || string.IsNullOrWhiteSpace(root))
        {
            return Task.FromResult<ObjectStoreProbeResult?>(null);
        }

        var searchRoot = root;
        if (!string.IsNullOrEmpty(config.Prefix))
        {
            // Prefix is interpreted as a relative sub-path under the store root. Path.Combine
            // silently discards `root` when Prefix is rooted (e.g. an absolute path), so reject
            // rooted prefixes outright before combining rather than relying solely on the
            // containment check below.
            if (Path.IsPathRooted(config.Prefix))
            {
                return Task.FromResult<ObjectStoreProbeResult?>(null);
            }

            // Reject any attempt to escape the root so a malformed prefix cannot read outside
            // the store. Compare against the root plus a trailing separator (not a bare prefix)
            // so a sibling directory that merely shares the root's string prefix (e.g. "/data/store1"
            // vs "/data/store12") is not mistaken for containment.
            var combined = Path.GetFullPath(Path.Join(root, config.Prefix));
            var normalizedRoot = Path.GetFullPath(root);
            var rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;
            if (!string.Equals(combined, normalizedRoot, StringComparison.Ordinal) &&
                !combined.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            {
                return Task.FromResult<ObjectStoreProbeResult?>(null);
            }

            searchRoot = combined;
        }

        if (!Directory.Exists(searchRoot))
        {
            return Task.FromResult<ObjectStoreProbeResult?>(null);
        }

        ObjectStoreProbeResult? newest = null;
        foreach (var path in Directory.EnumerateFiles(searchRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            DateTimeOffset lastModified;
            try
            {
                lastModified = File.GetLastWriteTimeUtc(path);
            }
            catch (IOException)
            {
                // File vanished between enumeration and stat; skip it.
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            var key = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            var candidate = new ObjectStoreProbeResult
            {
                Key = key,
                LastModified = lastModified
            };

            if (newest is null || string.CompareOrdinal(candidate.Marker, newest.Value.Marker) > 0)
            {
                newest = candidate;
            }
        }

        return Task.FromResult(newest);
    }
}
