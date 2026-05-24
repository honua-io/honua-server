// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Services;

internal sealed class MetadataV2GraphProviderEnvironmentSnapshotReader(
    IMetadataV2GraphProvider graphProvider) : IMetadataV2EnvironmentSnapshotReader
{
    public async ValueTask<MetadataV2GraphSnapshot?> GetCurrentAsync(
        string environment,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            return null;
        }

        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return MatchesEnvironment(snapshot, environment) ? snapshot : null;
    }

    public async ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(
        string environment,
        long revision,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            return null;
        }

        var snapshot = await graphProvider.GetByRevisionAsync(revision, cancellationToken).ConfigureAwait(false);
        return snapshot is not null && MatchesEnvironment(snapshot, environment) ? snapshot : null;
    }

    public async IAsyncEnumerable<MetadataV2EnvironmentRevision> ListCurrentRevisionsAsync(
        IReadOnlyList<string> environments,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (environments.Count == 0)
        {
            yield break;
        }

        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        foreach (var environment in environments)
        {
            if (MatchesEnvironment(snapshot, environment))
            {
                yield return new MetadataV2EnvironmentRevision
                {
                    Environment = snapshot.Graph.Environment,
                    Revision = snapshot.Revision,
                    ETag = snapshot.Etag,
                    ActivatedAt = snapshot.LoadedAt,
                };
            }
        }
    }

    private static bool MatchesEnvironment(MetadataV2GraphSnapshot snapshot, string environment)
        => string.Equals(snapshot.Graph.Environment, environment.Trim(), StringComparison.OrdinalIgnoreCase);
}
