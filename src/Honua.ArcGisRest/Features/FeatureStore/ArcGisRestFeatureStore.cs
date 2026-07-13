// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Honua.ArcGisRest.Features.FeatureStore.Models;
using Honua.ArcGisRest.Features.FeatureStore.Services;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.ArcGisRest.Features.FeatureStore;

/// <summary>
/// Read-through ArcGIS REST FeatureServer/MapServer feature provider. Each query
/// proxies through to the source service so customers can register a live
/// ArcGIS service as a federated layer without copying any data on day one.
/// </summary>
/// <remarks>
/// <para>Implements the canonical <see cref="IFeatureDataProvider"/> +
/// <see cref="IFeatureReader"/> surface so the federated layer participates in
/// the Honua catalog identically to locally-hosted layers: REST,
/// OGC Features, WFS, OData, GeoServices, and gRPC adapters all reach this
/// provider through the shared <see cref="FeatureProviderQueryRouter"/>.</para>
/// <para>Writes, statistics, top-features, bins, H3 aggregation, and native
/// MVT/FlatGeobuf/Geobuf/GML output are intentionally disabled — the provider
/// is read-only, and shared formatters handle every output format above the
/// canonical <see cref="Feature"/> stream.</para>
/// </remarks>
internal sealed class ArcGisRestFeatureStore : IFeatureDataProvider, IFeatureReader, IBindableFeatureDataProvider
{
    /// <summary>
    /// Dedicated ActivitySource for the federated ArcGIS REST provider (PA-108). One span
    /// covers the entire paging loop in <see cref="QueryAsync"/> rather than per-page
    /// spans, since a single query can issue up to <see cref="MaxPageIterations"/> outbound
    /// HTTP calls and per-page spans would themselves become a cardinality problem. Must be
    /// registered as <c>"Honua.ArcGisRest"</c> in <c>Honua.ServiceDefaults.Extensions._activitySourceNames</c>
    /// or the spans are silently dropped by the tracer provider.
    /// </summary>
    private static readonly ActivitySource _activitySource = new("Honua.ArcGisRest");

    private static readonly FeatureProviderCapabilities _capabilities = new()
    {
        SupportsQuery = true,
        SupportsCount = true,
        SupportsExtent = true,
        SupportsStatistics = false,
        Edits = FeatureProviderEditCapabilities.ReadOnly,
        Outputs = new FeatureProviderOutputCapabilities
        {
            SupportsStreamingGeoJson = false,
            SupportsNativeMvt = false,
            SupportsNativeFlatGeobuf = false,
            SupportsNativeGeobuf = false,
            SupportsNativeGml = false
        }
    };

    /// <summary>
    /// Per-page <c>resultRecordCount</c> requested when the caller did not cap the
    /// query with an explicit <see cref="FeatureQuery.Limit"/>. The upstream service
    /// still clamps each page to its own <c>maxRecordCount</c> (commonly 1000–2000)
    /// and flags <c>exceededTransferLimit</c>; the provider advances by the actual
    /// page length, so this value only bounds the request size, not correctness.
    /// </summary>
    internal const int DefaultPageSize = 2000;

    /// <summary>
    /// Hard cap on the number of <c>/query</c> round-trips a single
    /// <see cref="QueryAsync(int, FeatureQuery, CancellationToken)"/> /
    /// <see cref="QueryObjectIdsAsync(int, FeatureQuery, CancellationToken)"/> call
    /// will issue, guarding against an unbounded fetch when a misbehaving upstream
    /// keeps flagging <c>exceededTransferLimit</c> without advancing the offset. At
    /// the default page size this still admits millions of records.
    /// </summary>
    private const int MaxPageIterations = 10_000;

    private readonly IArcGisRestFeatureClient _client;
    private readonly ILogger<ArcGisRestFeatureStore> _logger;
    private readonly FeatureProviderBinding? _binding;

    public ArcGisRestFeatureStore(IArcGisRestFeatureClient client, ILogger<ArcGisRestFeatureStore> logger)
        : this(client, logger, binding: null)
    {
    }

    private ArcGisRestFeatureStore(IArcGisRestFeatureClient client, ILogger<ArcGisRestFeatureStore> logger, FeatureProviderBinding? binding)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _binding = binding;
    }

    /// <inheritdoc />
    public string ProviderName => DataProviderNames.ArcGisRest;

    /// <inheritdoc />
    public FeatureProviderCapabilities Capabilities => _capabilities;

    /// <inheritdoc />
    public IFeatureReader Reader => this;

    /// <inheritdoc />
    public IFeatureWriter? Writer => null;

    /// <inheritdoc />
    public IFeatureReader CreateReaderForBinding(FeatureProviderBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new ArcGisRestFeatureStore(_client, _logger, binding);
    }

    /// <inheritdoc />
    public async Task<Feature?> GetAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
    {
        var query = new FeatureQuery
        {
            ObjectIds = [featureId],
            Limit = 1
        };
        var result = await QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        return result.Items.Length == 0 ? null : result.Items[0];
    }

    /// <inheritdoc />
    public async Task<QueryResult<Feature>> QueryAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var context = ResolveBinding(layerId);
        var authHeader = ArcGisRestQueryParameters.BuildAuthorizationHeader(context.ServiceLocation.Token);

        // One span for the whole federated query, not one per page: the loop below can
        // issue up to MaxPageIterations outbound HTTP calls, and a child span per page
        // would itself become a cardinality/performance problem (PA-108).
        using var activity = _activitySource.StartActivity("honua.arcgisrest.query", ActivityKind.Client);
        activity?.SetTag("honua.arcgisrest.service_url", context.ServiceLocation.ServiceUrl);
        activity?.SetTag("honua.arcgisrest.layer_id", context.ArcGisLayerId);

        var geometryType = ResolveDeclaredGeometryType(context.Resource);
        var items = ImmutableArray.CreateBuilder<Feature>();
        var pageCount = 0;

        // The caller-supplied paging window. The upstream service truncates each
        // /query response at its own maxRecordCount (commonly 1000–2000) and flags
        // exceededTransferLimit; a single request therefore surfaces only the first
        // page. Loop on resultOffset until the upstream stops flagging more results
        // (or the caller's explicit limit is satisfied) so SupportsQuery=true does
        // not silently truncate large result sets.
        var baseOffset = query.Offset is int o && o > 0 ? o : 0;
        var requestedLimit = query.Limit is int l && l > 0 ? l : (int?)null;

        string? objectIdFieldName = null;
        var exceededTransferLimit = false;
        var pageOffset = baseOffset;

        for (var iteration = 0; iteration < MaxPageIterations; iteration++)
        {
            // Cap the page to the caller's remaining budget when a limit was supplied,
            // so an explicit limit is honored exactly and the loop never over-fetches.
            int pageSize;
            if (requestedLimit is int limit)
            {
                var remaining = limit - items.Count;
                if (remaining <= 0)
                {
                    break;
                }

                pageSize = Math.Min(remaining, DefaultPageSize);
            }
            else
            {
                pageSize = DefaultPageSize;
            }

            var pageQuery = query with { Offset = pageOffset, Limit = pageSize };
            var url = ArcGisRestQueryParameters.BuildFeatureQueryUrl(
                context.ServiceLocation.ServiceUrl,
                context.ArcGisLayerId,
                pageQuery);

            var response = await _client.QueryAsync(url, authHeader, cancellationToken).ConfigureAwait(false);
            pageCount++;
            EnsureNoUpstreamError(response.Error);

            // The object-id field name only needs resolving once; later pages echo
            // the same wire field (or none at all on a returnGeometry-only page).
            objectIdFieldName ??= ResolveObjectIdFieldName(response.ObjectIdFieldName, context.Resource);

            var pageLength = response.Features is { Length: > 0 } ? response.Features.Length : 0;
            if (pageLength > 0)
            {
                foreach (var sourceFeature in response.Features!)
                {
                    items.Add(ProjectFeature(sourceFeature, objectIdFieldName, geometryType));
                }
            }

            exceededTransferLimit = response.ExceededTransferLimit;

            // Stop when the upstream reports no further results, or returned an empty
            // page (guards against an infinite loop if a hostile/buggy upstream keeps
            // flagging exceededTransferLimit without advancing). Advance by the actual
            // page length so an upstream maxRecordCount smaller than the requested
            // pageSize still pages correctly.
            if (!exceededTransferLimit || pageLength == 0)
            {
                break;
            }

            pageOffset += pageLength;
        }

        var built = items.ToImmutable();

        // When the loop drained every matching record (the final page did not flag
        // exceededTransferLimit) the materialized count is the true total. When a
        // caller-supplied limit stopped the loop early while more records remain,
        // issue a returnCountOnly request so callers
        // (SearchEndpoints.ExecuteSearchAcrossPublicationsAsync, OGC Features
        // next-link computation) can report an accurate numberMatched.
        long totalCount;
        if (!exceededTransferLimit)
        {
            totalCount = baseOffset + built.Length;
        }
        else
        {
            var countUrl = ArcGisRestQueryParameters.BuildCountUrl(
                context.ServiceLocation.ServiceUrl,
                context.ArcGisLayerId,
                query);
            var countResponse = await _client.QueryCountAsync(countUrl, authHeader, cancellationToken).ConfigureAwait(false);
            EnsureNoUpstreamError(countResponse.Error);
            totalCount = countResponse.Count;
        }

        activity?.SetTag("honua.arcgisrest.page_count", pageCount);
        activity?.SetTag("honua.arcgisrest.feature_count", built.Length);
        ArcGisRestFeatureStoreLog.QueryPagesFetched(_logger, context.ArcGisLayerId, pageCount, built.Length);

        return QueryResult<Feature>.Create(totalCount, built, exceededTransferLimit);
    }

    /// <inheritdoc />
    public Task<byte[]?> QueryFlatGeobufAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        // The provider does not advertise native FlatGeobuf; return null so the
        // calling adapter falls back to the shared in-process formatter.
        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<long>> QueryObjectIdsAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var context = ResolveBinding(layerId);
        var authHeader = ArcGisRestQueryParameters.BuildAuthorizationHeader(context.ServiceLocation.Token);

        // Determine the total matching count first so paging does not depend on comparing
        // each page against the hardcoded DefaultPageSize sentinel — which breaks when the
        // upstream's maxRecordCount < DefaultPageSize and every page looks "short" even
        // though more records remain (BH4-001).
        var totalCount = await CountAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        if (totalCount <= 0)
        {
            return ImmutableArray<long>.Empty;
        }

        var baseOffset = query.Offset is int o && o > 0 ? o : 0;
        var requestedLimit = query.Limit is int l && l > 0 ? l : (int?)null;

        // Cap accumulation at the lesser of total known count and the caller's limit.
        var targetCount = requestedLimit.HasValue
            ? (int)Math.Min(requestedLimit.Value, totalCount - baseOffset)
            : (int)Math.Min(totalCount - baseOffset, int.MaxValue);

        if (targetCount <= 0)
        {
            return ImmutableArray<long>.Empty;
        }

        var ids = ImmutableArray.CreateBuilder<long>(targetCount);
        var pageOffset = baseOffset;

        for (var iteration = 0; iteration < MaxPageIterations && ids.Count < targetCount; iteration++)
        {
            var pageQuery = query with { Offset = pageOffset, Limit = DefaultPageSize };
            var url = ArcGisRestQueryParameters.BuildObjectIdsUrl(
                context.ServiceLocation.ServiceUrl,
                context.ArcGisLayerId,
                pageQuery);

            var response = await _client.QueryObjectIdsAsync(url, authHeader, cancellationToken).ConfigureAwait(false);
            EnsureNoUpstreamError(response.Error);

            var pageLength = response.ObjectIds is { Length: > 0 } ? response.ObjectIds.Length : 0;
            if (pageLength == 0)
            {
                // Upstream returned nothing — stop to avoid infinite loop.
                break;
            }

            ids.AddRange(response.ObjectIds!);
            pageOffset += pageLength;
        }

        if (requestedLimit is int cap && ids.Count > cap)
        {
            // Trim any overrun from the final full page down to the caller's limit.
            return ids.ToImmutable()[..cap];
        }

        return ids.Count == 0 ? ImmutableArray<long>.Empty : ids.ToImmutable();
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var context = ResolveBinding(layerId);
        var authHeader = ArcGisRestQueryParameters.BuildAuthorizationHeader(context.ServiceLocation.Token);
        var url = ArcGisRestQueryParameters.BuildCountUrl(
            context.ServiceLocation.ServiceUrl,
            context.ArcGisLayerId,
            query);

        var response = await _client.QueryCountAsync(url, authHeader, cancellationToken).ConfigureAwait(false);
        EnsureNoUpstreamError(response.Error);
        return response.Count;
    }

    /// <inheritdoc />
    public async Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        var context = ResolveBinding(layerId);
        var effectiveQuery = query ?? new FeatureQuery();
        var authHeader = ArcGisRestQueryParameters.BuildAuthorizationHeader(context.ServiceLocation.Token);
        var url = ArcGisRestQueryParameters.BuildExtentUrl(
            context.ServiceLocation.ServiceUrl,
            context.ArcGisLayerId,
            effectiveQuery);

        var response = await _client.QueryExtentAsync(url, authHeader, cancellationToken).ConfigureAwait(false);
        EnsureNoUpstreamError(response.Error);

        if (response.Extent is not { } extent)
        {
            return null;
        }

        var srid = ResolveSrid(extent.SpatialReference, context.Resource);
        return FeatureExtent.Create(extent.Xmin, extent.Ymin, extent.Xmax, extent.Ymax, srid);
    }

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryStatisticsAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(QueryStatisticsAsync), layerId);

    /// <inheritdoc />
    public Task<TemporalExtentResult?> GetTemporalExtentAsync(
        int layerId, string fieldName, TemporalPropertyType propertyType, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(GetTemporalExtentAsync), layerId);

    /// <inheritdoc />
    public async Task<EstimateResult> GetEstimatesAsync(int layerId, CancellationToken cancellationToken = default)
    {
        var countTask = CountAsync(layerId, new FeatureQuery(), cancellationToken);
        var extentTask = GetExtentAsync(layerId, null, cancellationToken);
        await Task.WhenAll(countTask, extentTask).ConfigureAwait(false);

        return new EstimateResult
        {
            EstimatedCount = await countTask.ConfigureAwait(false),
            Extent = await extentTask.ConfigureAwait(false)
        };
    }

    /// <inheritdoc />
    public Task<QueryResult<Feature>> QueryTopFeaturesAsync(
        int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(QueryTopFeaturesAsync), layerId);

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryDateBinsAsync(
        int layerId, FeatureQuery query, DateBinDefinition dateBin, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(QueryDateBinsAsync), layerId);

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryBinsAsync(
        int layerId, FeatureQuery query, BinDefinition binDefinition, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(QueryBinsAsync), layerId);

    /// <inheritdoc />
    public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> QueryH3Async(
        int layerId, FeatureQuery query, H3AggregationQuery h3Query, CancellationToken cancellationToken = default)
        => throw NotSupported(nameof(QueryH3Async), layerId);

    private ResolvedBinding ResolveBinding(int layerId)
    {
        var binding = _binding
            ?? throw new InvalidOperationException(
                "ArcGIS REST provider reads require a Metadata v2 provider binding; route requests through FeatureProviderQueryRouter.");

        if (binding.StorageLayerId != layerId)
        {
            throw new InvalidOperationException(
                $"ArcGIS REST provider binding targets storage layer {binding.StorageLayerId}, not requested layer {layerId}.");
        }

        var location = ArcGisRestServiceLocator.Resolve(binding.Connection);
        var arcGisLayerId = ResolveArcGisLayerId(binding, layerId);
        return new ResolvedBinding(location, arcGisLayerId, binding.Resource);
    }

    private static int ResolveArcGisLayerId(FeatureProviderBinding binding, int defaultLayerId)
    {
        // Storage binding options may carry an explicit `arcgisLayerId` when the
        // Honua catalog re-numbers layers. When absent, the canonical
        // StorageLayerId already lines up with the ArcGIS layer index — both
        // are zero-based integers on the same axis.
        if (binding.StorageBinding.Options.TryGetValue("arcgisLayerId", out var raw))
        {
            if (raw.ValueKind == JsonValueKind.Number && raw.TryGetInt32(out var fromNumber))
            {
                return fromNumber;
            }

            if (raw.ValueKind == JsonValueKind.String
                && int.TryParse(raw.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var fromString))
            {
                return fromString;
            }
        }

        return defaultLayerId;
    }

    private static MetadataV2GeometryType ResolveDeclaredGeometryType(MetadataV2Resource resource)
        => resource.Spatial?.GeometryType ?? MetadataV2GeometryType.None;

    private static string? ResolveObjectIdFieldName(string? wireField, MetadataV2Resource resource)
    {
        if (!string.IsNullOrWhiteSpace(wireField))
        {
            return wireField;
        }

        var primary = resource.SchemaFields.FirstOrDefault(field =>
            field.SemanticRoles.Any(role => role.Equals("id.primary", StringComparison.OrdinalIgnoreCase)));
        return primary?.Name;
    }

    private static int ResolveSrid(ArcGisRestSpatialReference? sr, MetadataV2Resource resource)
    {
        if (sr is { Wkid: int wkid } && wkid > 0)
        {
            return sr.LatestWkid is int latest && latest > 0 ? latest : wkid;
        }

        return resource.Spatial?.SpatialReference?.ResolveSrid() ?? 4326;
    }

    private static Feature ProjectFeature(
        ArcGisRestFeature source,
        string? objectIdFieldName,
        MetadataV2GeometryType geometryType)
    {
        var attributes = source.Attributes is { Count: > 0 }
            ? ProjectAttributes(source.Attributes)
            : ImmutableDictionary<string, object?>.Empty;

        var id = ResolveObjectId(source.Attributes, objectIdFieldName);
        var wkb = EsriJsonWkbWriter.Write(source.Geometry, geometryType);

        return Feature.Create(id, wkb, attributes);
    }

    /// <summary>
    /// Resolves the canonical <see cref="Feature"/> id from the upstream object-id
    /// attribute.
    /// </summary>
    /// <remarks>
    /// Returns <c>0</c> when the object-id field is unknown, absent, or carries a
    /// value that is not an integer (or integer-formatted string). The canonical
    /// <see cref="Feature"/> id is a <see cref="long"/>, so a non-integer upstream id
    /// (e.g. a GUID GlobalId) cannot be preserved verbatim and collapses to the
    /// <c>0</c> fallback. Multiple unresolvable features in a page therefore share id
    /// <c>0</c>, which can confuse id-based consumers and <see cref="GetAsync"/>-by-id
    /// semantics. This is acceptable for a read-through provider whose canonical
    /// access path is the WHERE/paging query rather than id lookup, but the
    /// collision is intentional and silent — publications relying on stable feature
    /// ids must expose an integer object-id field.
    /// </remarks>
    private static long ResolveObjectId(IReadOnlyDictionary<string, JsonElement>? attributes, string? objectIdFieldName)
    {
        if (attributes is null || string.IsNullOrWhiteSpace(objectIdFieldName))
        {
            return 0;
        }

        if (!TryLookupCaseInsensitive(attributes, objectIdFieldName, out var element))
        {
            return 0;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var asLong) => asLong,
            JsonValueKind.String when long.TryParse(element.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }

    private static bool TryLookupCaseInsensitive(IReadOnlyDictionary<string, JsonElement> attributes, string fieldName, out JsonElement value)
    {
        if (attributes.TryGetValue(fieldName, out value))
        {
            return true;
        }

        foreach (var kvp in attributes.Where(kvp => kvp.Key.Equals(fieldName, StringComparison.OrdinalIgnoreCase)))
        {
            value = kvp.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static ImmutableDictionary<string, object?> ProjectAttributes(IReadOnlyDictionary<string, JsonElement> attributes)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var kvp in attributes)
        {
            builder[kvp.Key] = ProjectAttributeValue(kvp.Value);
        }

        return builder.ToImmutable();
    }

    private static object? ProjectAttributeValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var asLong) => asLong,
            JsonValueKind.Number when value.TryGetDouble(out var asDouble) => asDouble,
            JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
            _ => value.GetRawText()
        };

    private static void EnsureNoUpstreamError(ArcGisRestError? error)
    {
        if (error is null)
        {
            return;
        }

        // Include the upstream message so diagnostics do not require reproducing the request.
        // The message is internal-facing; shared error mapping prevents it from being leaked
        // to HTTP clients.
        var detail = string.IsNullOrWhiteSpace(error.Message)
            ? string.Empty
            : $": {error.Message}";

        throw new InvalidOperationException(
            $"ArcGIS REST service returned error {error.Code}{detail}");
    }

    private static NotSupportedException NotSupported(string operation, int layerId)
        => new($"ArcGIS REST provider does not support '{operation}' for layer {layerId} in this slice.");

    private readonly record struct ResolvedBinding(
        ArcGisRestServiceLocation ServiceLocation,
        int ArcGisLayerId,
        MetadataV2Resource Resource);
}
