// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Plugins.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer.Models;

namespace Honua.Protocols.GeoServices.FeatureServer.Services;

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
        => await QueryWithValidationAsync(
            service,
            resource,
            publication,
            storageLayerId,
            query,
            actor: null,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Materializes a FeatureServer query and applies plugin computed-field projection
    /// (<see cref="IComputedFieldProvider"/>, issue #1562) to each returned feature before it is
    /// formatted. Projection is a no-op unless the Enterprise plugin SDK is licensed, enabled, and
    /// at least one computed-field provider is registered, so the common path adds no overhead.
    /// The raw GeoServices/Geobuf/FlatGeobuf fast paths bypass the canonical <see cref="Feature"/>
    /// representation and therefore do not (yet) carry computed fields; those formats are tracked
    /// separately in the phase-2 follow-ups for #1562.
    /// </summary>
    /// <param name="service">The owning V2 service.</param>
    /// <param name="resource">The queried V2 resource (carries layer identity for the compute context).</param>
    /// <param name="publication">The publication binding the resource to the service.</param>
    /// <param name="storageLayerId">The resolved storage layer id.</param>
    /// <param name="query">The canonical feature query.</param>
    /// <param name="actor">The authenticated caller, when known; flows into the compute context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query result whose features include any projected computed fields.</returns>
    public async Task<QueryResult<Feature>> QueryWithValidationAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureQuery query,
        string? actor,
        CancellationToken cancellationToken)
    {
        var reader = await ResolveReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);
        var result = await QueryWithValidationAsync(reader, storageLayerId, query, cancellationToken).ConfigureAwait(false);
        return await ProjectComputedFieldsAsync(result, resource, storageLayerId, actor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Id-only V2 read. Resolves the same reader as a feature query but calls
    /// <see cref="IFeatureReader.QueryObjectIdsAsync"/> and does not project computed fields.
    /// </summary>
    public async Task<ImmutableArray<long>> QueryObjectIdsWithValidationAsync(
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
        return await QueryObjectIdsWithValidationAsync(reader, storageLayerId, query, cancellationToken).ConfigureAwait(false);
    }

    private async Task<QueryResult<Feature>> ProjectComputedFieldsAsync(
        QueryResult<Feature> result,
        MetadataV2Resource resource,
        int storageLayerId,
        string? actor,
        CancellationToken cancellationToken)
    {
        if (!_computedFieldPipeline.HasComputedFields || result.Items.IsDefaultOrEmpty)
        {
            return result;
        }

        var context = new ComputedFieldContext(
            ServiceId: resource.Metadata.Id,
            LayerId: storageLayerId,
            ResourceName: resource.Metadata.Name,
            Actor: actor);

        var builder = ImmutableArray.CreateBuilder<Feature>(result.Items.Length);
        foreach (var feature in result.Items)
        {
            builder.Add(await _computedFieldPipeline
                .ProjectAsync(feature, context, cancellationToken)
                .ConfigureAwait(false));
        }

        return QueryResult<Feature>.Create(
            totalCount: result.TotalCount,
            items: builder.MoveToImmutable(),
            hasMoreResults: result.HasMoreResults);
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

    public async Task<bool> SupportsDistinctValuesAsync(
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
        return reader is IDistinctFeatureReader;
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
        var routedReader = await ResolveRoutedReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);

        if (routedReader is not null and not IStreamingFeatureStore)
        {
            // A reader that cannot stream (federated ArcGIS REST, warehouse providers) must
            // not be wrapped in MaterializingFeatureStream here: with no limit that pulls
            // every full feature, geometry included, into memory before the first id is
            // written (#6113). Ask the reader for object ids only, under a hard ceiling.
            var bounded = await QueryBoundedObjectIdsAsync(
                routedReader,
                storageLayerId,
                query,
                cancellationToken).ConfigureAwait(false);
            await WriteObjectIdsAsync(bounded, objectIdFieldName, context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var objectIds = await QueryObjectIdsWithValidationAsync(
            routedReader ?? _featureReader,
            storageLayerId,
            query,
            cancellationToken).ConfigureAwait(false);
        await WriteObjectIdsAsync(objectIds, objectIdFieldName, context, cancellationToken).ConfigureAwait(false);
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
        string? negotiatedFormat = null,
        CancellationToken cancellationToken = default)
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
            negotiatedFormat,
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
        string? negotiatedFormat,
        CancellationToken cancellationToken)
    {
        var preparedStream = PrepareFeatureStream(streamingFeatureStore, layerId, query, cancellationToken);

        string[]? outFields = string.IsNullOrEmpty(queryParams.OutFields)
            ? null
            : [.. queryParams.OutFields.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim())];

        var format = negotiatedFormat ?? queryParams.F ?? "json";
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
                returnCentroid: queryParams.ReturnCentroid,
                cancellationToken: cancellationToken);
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
        var reader = await ResolveRoutedReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            operation,
            cancellationToken).ConfigureAwait(false);
        if (reader is null)
        {
            return _streamingFeatureStore;
        }

        // Federated readers such as ArcGIS REST implement IFeatureReader and page
        // through QueryAsync / QueryObjectIdsAsync. They are not IStreamingFeatureStore.
        // Materialize that page instead of failing the request.
        return reader as IStreamingFeatureStore ?? new MaterializingFeatureStream(reader);
    }

    /// <summary>
    /// Resolves the provider reader for a publication routed through the provider router, or
    /// <see langword="null"/> when the publication uses the shared feature reader and streaming store.
    /// </summary>
    private async Task<IFeatureReader?> ResolveRoutedReaderV2Async(
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
            return null;
        }

        return await ResolveReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            operation,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a <c>returnIdsOnly</c> query from <see cref="IFeatureReader.QueryObjectIdsAsync"/> when the
    /// publication's provider reader cannot stream. Returns <see langword="null"/> when the reader streams
    /// natively (or the shared reader serves the publication), so the caller keeps its existing path.
    /// </summary>
    public async Task<BoundedObjectIds?> TryQueryNonStreamingObjectIdsAsync(
        MetadataV2Service service,
        MetadataV2Resource resource,
        MetadataV2Publication publication,
        int storageLayerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        var reader = await ResolveRoutedReaderV2Async(
            service,
            resource,
            publication,
            storageLayerId,
            FeatureProviderReadOperation.Query,
            cancellationToken).ConfigureAwait(false);
        if (reader is null or IStreamingFeatureStore)
        {
            return null;
        }

        return await QueryBoundedObjectIdsAsync(reader, storageLayerId, query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads at most <see cref="MaxNonStreamingObjectIds"/> (or the caller's smaller limit) object ids
    /// from a non-streaming reader. One extra id is probed so a truncated result reports
    /// <c>exceededTransferLimit</c> instead of silently looking complete.
    /// </summary>
    private static async Task<BoundedObjectIds> QueryBoundedObjectIdsAsync(
        IFeatureReader reader,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        var ceiling = query.Limit is int limit && limit > 0 && limit < MaxNonStreamingObjectIds
            ? limit
            : MaxNonStreamingObjectIds;
        var ids = await reader
            .QueryObjectIdsAsync(layerId, query with { Limit = ceiling + 1 }, cancellationToken)
            .ConfigureAwait(false);
        if (ids.IsDefault)
        {
            ids = ImmutableArray<long>.Empty;
        }

        var exceeded = ids.Length > ceiling;
        return new BoundedObjectIds(exceeded ? ids[..ceiling] : ids, exceeded);
    }

    /// <summary>
    /// Adapts a non-streaming <see cref="IFeatureReader"/> to <see cref="IStreamingFeatureStore"/>
    /// by reading one materialized page. Postgres readers that already stream are not wrapped.
    /// </summary>
    private sealed class MaterializingFeatureStream(IFeatureReader reader) : IStreamingFeatureStore
    {
        public async IAsyncEnumerable<Feature> StreamFeaturesAsync(
            int layerId,
            FeatureQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var result = await reader.QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            foreach (var feature in result.Items)
            {
                yield return feature;
            }
        }

        public async IAsyncEnumerable<IReadOnlyList<Feature>> StreamFeatureBatchesAsync(
            int layerId,
            FeatureQuery query,
            int batchSize = 1000,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var page = new List<Feature>(batchSize);
            await foreach (var feature in StreamFeaturesAsync(layerId, query, cancellationToken).ConfigureAwait(false))
            {
                page.Add(feature);
                if (page.Count >= batchSize)
                {
                    yield return page;
                    page = new List<Feature>(batchSize);
                }
            }

            if (page.Count > 0)
            {
                yield return page;
            }
        }

        public IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(
            int layerId,
            FeatureQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "GML streaming is not supported by feature stores that do not implement IStreamingFeatureStore.");
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
