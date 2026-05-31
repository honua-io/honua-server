// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.ArcGisRest.Features.FeatureStore.Models;
using Honua.ArcGisRest.Features.FeatureStore.Services;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;

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

    private readonly IArcGisRestFeatureClient _client;
    private readonly FeatureProviderBinding? _binding;

    public ArcGisRestFeatureStore(IArcGisRestFeatureClient client)
        : this(client, binding: null)
    {
    }

    private ArcGisRestFeatureStore(IArcGisRestFeatureClient client, FeatureProviderBinding? binding)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
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
        return new ArcGisRestFeatureStore(_client, binding);
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
        var url = ArcGisRestQueryParameters.BuildFeatureQueryUrl(
            context.ServiceLocation.ServiceUrl,
            context.ArcGisLayerId,
            query,
            context.ServiceLocation.Token);

        var response = await _client.QueryAsync(url, cancellationToken).ConfigureAwait(false);
        EnsureNoUpstreamError(response.Error);

        var geometryType = ResolveDeclaredGeometryType(context.Resource);
        var objectIdFieldName = ResolveObjectIdFieldName(response.ObjectIdFieldName, context.Resource);

        var items = ImmutableArray.CreateBuilder<Feature>();
        if (response.Features is { Length: > 0 })
        {
            foreach (var sourceFeature in response.Features)
            {
                items.Add(ProjectFeature(sourceFeature, objectIdFieldName, geometryType));
            }
        }

        var built = items.ToImmutable();
        return QueryResult<Feature>.Create(built.Length, built, response.ExceededTransferLimit);
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
        var url = ArcGisRestQueryParameters.BuildObjectIdsUrl(
            context.ServiceLocation.ServiceUrl,
            context.ArcGisLayerId,
            query,
            context.ServiceLocation.Token);

        var response = await _client.QueryObjectIdsAsync(url, cancellationToken).ConfigureAwait(false);
        EnsureNoUpstreamError(response.Error);

        return response.ObjectIds is { Length: > 0 } ids
            ? ImmutableArray.Create(ids)
            : ImmutableArray<long>.Empty;
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(int layerId, FeatureQuery query, CancellationToken cancellationToken = default)
    {
        var context = ResolveBinding(layerId);
        var url = ArcGisRestQueryParameters.BuildCountUrl(
            context.ServiceLocation.ServiceUrl,
            context.ArcGisLayerId,
            query,
            context.ServiceLocation.Token);

        var response = await _client.QueryCountAsync(url, cancellationToken).ConfigureAwait(false);
        EnsureNoUpstreamError(response.Error);
        return response.Count;
    }

    /// <inheritdoc />
    public async Task<FeatureExtent?> GetExtentAsync(int layerId, FeatureQuery? query = null, CancellationToken cancellationToken = default)
    {
        var context = ResolveBinding(layerId);
        var effectiveQuery = query ?? new FeatureQuery();
        var url = ArcGisRestQueryParameters.BuildExtentUrl(
            context.ServiceLocation.ServiceUrl,
            context.ArcGisLayerId,
            effectiveQuery,
            context.ServiceLocation.Token);

        var response = await _client.QueryExtentAsync(url, cancellationToken).ConfigureAwait(false);
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

        foreach (var kvp in attributes)
        {
            if (kvp.Key.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                return true;
            }
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

        throw new InvalidOperationException(
            $"ArcGIS REST service returned error {error.Code}: {error.Message ?? "(no message)"}");
    }

    private static NotSupportedException NotSupported(string operation, int layerId)
        => new($"ArcGIS REST provider does not support '{operation}' for layer {layerId} in this slice.");

    private readonly record struct ResolvedBinding(
        ArcGisRestServiceLocation ServiceLocation,
        int ArcGisLayerId,
        MetadataV2Resource Resource);
}
