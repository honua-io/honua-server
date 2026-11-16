// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Server.Features.Admin;

internal static partial class ServiceSettingsEndpoints
{
    // The Metadata v2 graph store uses optimistic concurrency: SaveAsync rejects a stale expected-etag with an
    // "etag mismatch" InvalidOperationException. Background writers (catalog cache, CRS warmup) can bump the
    // etag between our read and write, so the read-modify-write is retried a few times before surfacing.
    private const int MetadataMutationMaxAttempts = 5;

    private static bool IsEtagMismatch(Exception exception) =>
        exception is MetadataV2GraphConcurrencyException
        || exception is InvalidOperationException
        && exception.Message.Contains("etag mismatch", StringComparison.OrdinalIgnoreCase);

    private sealed record ServiceMutation(
        IMetadataV2GraphStore Store, string ServiceName, Func<MetadataV2Service, MetadataV2Service> Apply);

    // Update every protocol entry sharing a service name; unpinned writes retry concurrency conflicts.
    private static async Task MutateServicesByNameAsync(
        ServiceMutation mutation,
        CancellationToken cancellationToken,
        string? expectedMetadataEtag = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            var snapshot = await mutation.Store.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (expectedMetadataEtag is not null &&
                !string.Equals(expectedMetadataEtag, snapshot.Etag, StringComparison.Ordinal))
            {
                throw new MetadataV2GraphConcurrencyException("Access policy metadata changed.", expectedMetadataEtag, snapshot.Etag);
            }

            var updated = MutateServiceGraph(snapshot.Graph, mutation.ServiceName, mutation.Apply);
            if (updated is null)
            {
                return;
            }

            try
            {
                _ = await mutation.Store.SaveAsync(updated, snapshot.Etag, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (expectedMetadataEtag is not null && IsEtagMismatch(ex))
            {
                throw new MetadataV2GraphConcurrencyException("Access policy metadata changed during save.", expectedMetadataEtag, null);
            }
            catch (Exception ex) when (IsEtagMismatch(ex) && attempt < MetadataMutationMaxAttempts)
            {
                // Concurrent etag bump — re-read and re-apply the mutation against the fresh snapshot.
            }
        }
    }

    private static MetadataV2Graph? MutateServiceGraph(
        MetadataV2Graph graph,
        string serviceName,
        Func<MetadataV2Service, MetadataV2Service> mutate)
    {
        var services = graph.Services.ToArray();
        var mutatedAny = false;
        for (var i = 0; i < services.Length; i++)
        {
            if (string.Equals(services[i].Metadata.Name, serviceName, StringComparison.OrdinalIgnoreCase))
            {
                services[i] = mutate(services[i]);
                mutatedAny = true;
            }
        }

        return mutatedAny ? graph with { Services = services, Revision = graph.Revision + 1 } : null;
    }
}
