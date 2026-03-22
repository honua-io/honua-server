// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// In-memory implementation of <see cref="IManifestVersionStore"/> for testing.
/// </summary>
internal sealed class InMemoryManifestVersionStore : IManifestVersionStore
{
    private readonly object _sync = new();
    private readonly List<ManifestVersionEntry> _versions = new();

    public Task StoreAsync(ManifestVersionEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _versions.Add(entry);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ManifestVersionSummary>> ListAsync(
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var results = _versions
                .OrderByDescending(v => v.AppliedAt)
                .Skip(offset)
                .Take(limit)
                .Select(v => new ManifestVersionSummary
                {
                    VersionId = v.VersionId,
                    ManifestHash = v.ManifestHash,
                    Summary = v.Summary,
                    Actor = v.Actor,
                    AppliedAt = v.AppliedAt,
                    ResourceCount = v.ResourceCount
                })
                .ToArray();

            return Task.FromResult<IReadOnlyList<ManifestVersionSummary>>(results);
        }
    }

    public Task<ManifestVersionEntry?> GetAsync(string versionId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var version = _versions.FirstOrDefault(v =>
                string.Equals(v.VersionId, versionId, StringComparison.Ordinal));
            return Task.FromResult(version);
        }
    }

    public Task<ManifestVersionEntry?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var version = _versions
                .OrderByDescending(v => v.AppliedAt)
                .FirstOrDefault();
            return Task.FromResult(version);
        }
    }
}
