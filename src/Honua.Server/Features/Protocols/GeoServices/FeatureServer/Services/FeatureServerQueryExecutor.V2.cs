// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Server.Features.Protocols.GeoServices.FeatureServer.Services;

/// <summary>
/// V2 overloads on <see cref="FeatureServerQueryExecutor"/> that route through
/// the Metadata v2 graph (service + resource + publication + storage layer id)
/// rather than the legacy catalog service/layer models. These forward through
/// the int-layer-id reader path so the existing <see cref="IFeatureReader"/>
/// implementations are reused without a legacy adapter shim.
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

    public async Task<(ReadOnlyMemory<byte> Payload, int Count)> QueryRawGeoServicesPointJsonWithValidationAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureQuery query,
        bool returnGeometry,
        int? outputSrid,
        CancellationToken cancellationToken)
    {
        var reader = await ResolveReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);
        return await QueryRawGeoServicesPointJsonWithValidationAsync(
            reader,
            storageLayerId,
            query,
            resource,
            returnGeometry,
            outputSrid,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StreamIdsAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureQuery query,
        string objectIdFieldName,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var streamingFeatureStore = await ResolveStreamingFeatureStoreV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);

        await StreamIdsAsync(
            streamingFeatureStore,
            storageLayerId,
            query,
            objectIdFieldName,
            context,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StreamQueryAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureQuery query,
        QueryParameters queryParams,
        int? outputSrid,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var streamingFeatureStore = await ResolveStreamingFeatureStoreV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);

        await StreamQueryAsync(
            streamingFeatureStore,
            storageLayerId,
            query,
            resource,
            queryParams,
            outputSrid,
            context,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task StreamQueryAsync(
        IStreamingFeatureStore streamingFeatureStore,
        int layerId,
        FeatureQuery query,
        MetadataV2Resource resource,
        QueryParameters queryParams,
        int? outputSrid,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var preparedStream = await PrepareFeatureStreamAsync(streamingFeatureStore, layerId, query, cancellationToken);

        string[]? outFields = string.IsNullOrEmpty(queryParams.OutFields)
            ? null
            : [.. queryParams.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim())];

        var format = queryParams.F ?? "json";
        var contentType = format.ToLowerInvariant() switch
        {
            "geojson" => "application/geo+json",
            _ => "application/json"
        };

        context.Response.ContentType = contentType;
        context.Response.StatusCode = StatusCodes.Status200OK;

        if (string.Equals(format, "geojson", StringComparison.OrdinalIgnoreCase))
        {
            await _streamingFormatter.StreamAsGeoJsonAsync(
                preparedStream.Features,
                resource,
                queryParams.ReturnGeometry,
                queryParams.ReturnZ,
                queryParams.ReturnM,
                queryParams.GeometryPrecision,
                queryParams.MaxAllowableOffset,
                outFields,
                preparedStream.HasMoreResults,
                context.Response.BodyWriter,
                cancellationToken);
        }
        else
        {
            await _streamingFormatter.StreamAsGeoServicesJsonAsync(
                preparedStream.Features,
                resource,
                queryParams.ReturnGeometry,
                outputSrid,
                queryParams.ReturnZ,
                queryParams.ReturnM,
                queryParams.GeometryPrecision,
                queryParams.MaxAllowableOffset,
                outFields,
                preparedStream.HasMoreResults,
                context.Response.BodyWriter,
                cancellationToken);
        }

        await context.Response.CompleteAsync();
    }

    private async Task<IStreamingFeatureStore> ResolveStreamingFeatureStoreV2Async(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureProviderReadOperation operation,
        CancellationToken cancellationToken)
    {
        if (_providerQueryRouter == null
            || _metadataGraphProvider == null
            || !ShouldRouteProviderReaderV2(publication))
        {
            return _streamingFeatureStore;
        }

        var reader = await ResolveReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            operation,
            cancellationToken).ConfigureAwait(false);

        return reader as IStreamingFeatureStore
            ?? throw new InvalidOperationException("Streaming feature output is not supported by the configured feature store.");
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
        // to the shared feature reader. This matches the legacy resolver gate.
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
        // Mirrors the legacy provider-reader gate: route through the
        // provider router only when the publication has a backing storage
        // binding (the V2 graph form of "source-backed"). The router itself
        // walks the snapshot to find the binding's connection.
        return !string.IsNullOrEmpty(publication.StorageBindingId);
    }
}
