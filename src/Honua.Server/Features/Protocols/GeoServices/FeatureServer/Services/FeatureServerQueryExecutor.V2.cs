// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// V2 overloads on <see cref="FeatureServerQueryExecutor"/> that route through
/// the Metadata v2 graph (service + resource + publication + storage layer id)
/// rather than v1 <c>ServiceDefinition</c>/<c>LayerDefinition</c>. These mirror
/// the v1 entry points but forward through the int-layer-id reader path so the
/// existing <see cref="IFeatureReader"/> implementations are reused without a
/// v1 adapter shim. Streaming entry points (StreamQueryAsync/StreamIdsAsync)
/// still depend on v1-typed formatters and are not yet ported here.
/// </summary>
internal sealed partial class FeatureServerQueryExecutor
{
    public async Task<long> CountAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        var reader = await ResolveReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Count,
            cancellationToken).ConfigureAwait(false);
        return await reader.CountAsync(storageLayerId, query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FeatureExtent?> GetExtentAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        var reader = await ResolveReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Extent,
            cancellationToken).ConfigureAwait(false);
        return await reader.GetExtentAsync(storageLayerId, query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        var reader = await ResolveReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Statistics,
            cancellationToken).ConfigureAwait(false);
        return await reader.QueryStatisticsAsync(storageLayerId, query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QueryResult<Feature>> QueryWithValidationAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        var reader = await ResolveReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);
        return await QueryWithValidationAsync(reader, storageLayerId, query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> QueryFlatGeobufWithValidationAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        var reader = await ResolveReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);
        return await QueryFlatGeobufWithValidationAsync(reader, storageLayerId, query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> QueryGeobufWithValidationAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        var reader = await ResolveReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);
        return await QueryGeobufWithValidationAsync(reader, storageLayerId, query, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> SupportsGeobufOutputAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        CancellationToken cancellationToken)
    {
        var reader = await ResolveReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);
        return reader is IGeobufFeatureStore;
    }

    public async Task<bool> SupportsFlatGeobufOutputAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        CancellationToken cancellationToken)
    {
        var reader = await ResolveReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);
        return reader is IFlatGeobufFeatureStore;
    }

    public async Task<bool> SupportsRawGeoServicesPointOutputAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        CancellationToken cancellationToken)
    {
        var reader = await ResolveReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);
        return reader is IPagedRawGeoServicesFeatureStore;
    }

    private async Task<IFeatureReader> ResolveReaderV2Async(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureProviderReadOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(publication);

        // When no provider router is wired (test/in-process fixtures) or the
        // graph does not yet route through a per-tenant connection, fall back
        // to the shared feature reader. Matches the v1 ResolveReaderAsync gate.
        if (_providerQueryRouter == null
            || _metadataGraphProvider == null
            || !ShouldRouteProviderReaderV2(publication))
        {
            return _featureReader;
        }

        var snapshot = await _metadataGraphProvider
            .GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);

        return await _providerQueryRouter
            .ResolveReaderAsync(
                snapshot,
                service,
                resource,
                publication,
                storageLayerId,
                operation,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool ShouldRouteProviderReaderV2(MetadataV2Publication publication)
    {
        // Mirrors v1's ShouldRouteProviderReader semantics: route through the
        // provider router only when the publication has a backing storage
        // binding (the V2 graph form of "source-backed"). The router itself
        // walks the snapshot to find the binding's connection.
        return !string.IsNullOrEmpty(publication.StorageBindingId);
    }
}
