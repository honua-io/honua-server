// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.FeatureServer.Services;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Replica data scoping and bounded change delivery shared by createReplica, extractChanges and
/// synchronizeReplica (#4017, #4018, #4019).
/// </summary>
internal static partial class FeatureServerEndpoints
{
    private const string ReplicaEmbeddedTransportType = "esriTransportTypeEmbedded";
    private const string ReplicaUrlTransportType = "esriTransportTypeUrl";
    private const string ReplicaQueryOptionAll = "all";
    private const string ReplicaQueryOptionNone = "none";
    private const string ReplicaQueryOptionUseFilter = "useFilter";

    /// <summary>
    /// Change categories an extract returns (Esri <c>returnInserts</c>/<c>returnUpdates</c>/<c>returnDeletes</c>).
    /// </summary>
    private readonly record struct ReplicaChangeSelection(bool Inserts, bool Updates, bool Deletes)
    {
        public static ReplicaChangeSelection All => new(true, true, true);
    }

    /// <summary>
    /// A layer's resolved replica scope: whether it carries data at all, the translated where clause,
    /// the intersects filter, and the spatial reference geometries are delivered in.
    /// </summary>
    private sealed record ReplicaLayerScope(bool IncludeData, SqlFragment? SqlFilter, SpatialFilter? SpatialFilter, int? OutputSrid)
    {
        public static ReplicaLayerScope Whole { get; } = new(true, null, null, null);
    }

    /// <summary>One layer's delivered changes plus the insert/update ids behind them.</summary>
    private sealed record ReplicaLayerDelivery(LayerChanges Changes, long[] InsertIds, long[] UpdateIds);

    /// <summary>
    /// Changes delivered for a generation window. <see cref="ThroughGeneration"/> is the generation the
    /// delivery actually reached; <see cref="ExceededTransferLimit"/> is true when it stopped short of
    /// the requested bound because a layer exceeded <c>Limits:Replica:MaxChangesPerLayer</c>.
    /// </summary>
    private sealed record ReplicaDelivery(ReplicaLayerDelivery[] Layers, long ThroughGeneration, bool ExceededTransferLimit)
    {
        public LayerChanges[] LegacyLayerChanges => [.. Layers.Select(static layer => layer.Changes)];
    }

    /// <summary>
    /// Rejects the Esri replica transport parameters this server cannot honor instead of silently
    /// ignoring them (#4017, #4018): <c>async=true</c> (replicas are produced synchronously and
    /// <c>syncCapabilities.supportsAsync</c> is false), any <c>dataFormat</c> other than JSON, an
    /// unknown <c>transportType</c>, and <c>returnAttachments=true</c>. <c>esriTransportTypeUrl</c> is
    /// accepted and answered with the embedded transport, which the response declares.
    /// </summary>
    private static IResult? ValidateReplicaTransportParameters(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values)
    {
        if (!TryParseBoolValue(values, "async", false, out var isAsync, out var asyncError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid async parameter",
                [asyncError ?? "async must be a boolean value."]);
        }

        if (isAsync)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "async is not supported",
                ["This server produces replica data synchronously (syncCapabilities.supportsAsync is false). Omit async or pass async=false."]);
        }

        var dataFormat = GetValueString(values, "dataFormat");
        if (!string.IsNullOrWhiteSpace(dataFormat) &&
            !dataFormat.Trim().Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context, $"dataFormat '{dataFormat.Trim()}' is not supported",
                ["Replica data is delivered as Esri JSON only. Pass dataFormat=json; runtime geodatabase (sqlite), file geodatabase, shapefile and other file formats are not produced."]);
        }

        var transportType = GetValueString(values, "transportType");
        if (!string.IsNullOrWhiteSpace(transportType) &&
            !transportType.Trim().Equals(ReplicaEmbeddedTransportType, StringComparison.OrdinalIgnoreCase) &&
            !transportType.Trim().Equals(ReplicaUrlTransportType, StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid transportType parameter",
                [$"transportType must be {ReplicaEmbeddedTransportType} or {ReplicaUrlTransportType}."]);
        }

        // honua-server#4405: `returnAttachments` was accepted and silently ignored — a client asking for
        // an attachment-carrying replica got one without attachments and no indication of it.
        // Attachment replication is not implemented for 2026.1, so the request is rejected rather than
        // quietly downgraded.
        if (!TryParseBoolValue(values, "returnAttachments", false, out var returnAttachments, out var returnAttachmentsError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid returnAttachments parameter",
                [returnAttachmentsError ?? "returnAttachments must be a boolean value."]);
        }

        if (returnAttachments)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "returnAttachments is not supported",
                ["This server does not replicate attachments. Omit returnAttachments or pass returnAttachments=false, and synchronize attachments through the FeatureServer attachment endpoints."]);
        }

        return null;
    }

    /// <summary>
    /// Parses the Esri replica data-scope parameters shared by createReplica and extractChanges:
    /// <c>geometry</c>/<c>geometryType</c>/<c>inSR</c> (an intersects filter), <c>layerQueries</c>
    /// (per-layer <c>queryOption</c>, <c>where</c>, <c>useGeometry</c>, <c>includeRelated</c>) and, for
    /// createReplica, <c>replicaSR</c>. Every value is resolved against each target layer here, so a
    /// scope that cannot be applied is rejected instead of silently widening the replica to whole layers
    /// (#4018). Returns a null scope when no scoping parameter is present.
    /// </summary>
    private static async Task<(ReplicaScopeDefinition? Scope, IResult? Error)> TryParseReplicaScopeAsync(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        ReplicaLayerV2[] layers,
        bool acceptReplicaSpatialReference,
        CancellationToken cancellationToken)
    {
        var geometryText = GetValueString(values, "geometry");
        var geometryType = GetValueString(values, "geometryType");
        var inSr = GetValueString(values, "inSR");
        var layerQueriesText = GetValueString(values, "layerQueries");
        var replicaSrText = acceptReplicaSpatialReference ? GetValueString(values, "replicaSR") : null;

        if (string.IsNullOrWhiteSpace(geometryText) &&
            string.IsNullOrWhiteSpace(layerQueriesText) &&
            string.IsNullOrWhiteSpace(replicaSrText))
        {
            return (null, null);
        }

        var queryServices = context.RequestServices.GetRequiredService<IFeatureServerQueryServices>();
        var scope = new ReplicaScopeDefinition();

        if (!string.IsNullOrWhiteSpace(geometryText))
        {
            if (!GeoServicesGeometryParser.TryParseGeoServicesGeometry(geometryText, geometryType, out var parsedGeometry, out var geometryError) ||
                parsedGeometry is null)
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context, "Invalid geometry parameter",
                    [geometryError ?? "geometry must be an Esri JSON geometry."]));
            }

            var inputSrid = await queryServices.ResolveSridAsync(inSr, parsedGeometry.SpatialReference, cancellationToken).ConfigureAwait(false);
            if (inputSrid is null && !string.IsNullOrWhiteSpace(inSr))
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context, "Invalid inSR parameter",
                    [$"inSR '{inSr}' is not a supported spatial reference."]));
            }

            scope.Geometry = geometryText;
            scope.GeometryType = geometryType;
            scope.InputSrid = inputSrid;
        }

        if (!string.IsNullOrWhiteSpace(layerQueriesText))
        {
            if (!TryParseReplicaLayerQueries(layerQueriesText, layers, out var layerQueries, out var layerQueriesError))
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context, "Invalid layerQueries parameter", [layerQueriesError!]));
            }

            scope.LayerQueries = layerQueries;
        }

        if (!string.IsNullOrWhiteSpace(replicaSrText))
        {
            var outputSrid = await queryServices.ResolveSridAsync(replicaSrText, null, cancellationToken).ConfigureAwait(false);
            if (outputSrid is null)
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context, "Invalid replicaSR parameter",
                    [$"replicaSR '{replicaSrText}' is not a supported spatial reference."]));
            }

            scope.OutputSrid = outputSrid;
        }

        foreach (var layer in layers)
        {
            if (!TryResolveReplicaLayerScope(context, scope, layer, out _, out var scopeError))
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context, "Invalid replica scope", [scopeError!]));
            }
        }

        return (scope, null);
    }

    private static bool TryParseReplicaLayerQueries(
        string layerQueriesText,
        ReplicaLayerV2[] layers,
        out Dictionary<string, ReplicaLayerQueryDefinition> layerQueries,
        out string? error)
    {
        layerQueries = new Dictionary<string, ReplicaLayerQueryDefinition>(StringComparer.Ordinal);
        error = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(layerQueriesText);
        }
        catch (JsonException)
        {
            error = "layerQueries must be a JSON object keyed by layer id.";
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "layerQueries must be a JSON object keyed by layer id.";
                return false;
            }

            var layerIds = layers.Select(static layer => layer.PublicLayerId).ToHashSet();
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var layerId) ||
                    !layerIds.Contains(layerId))
                {
                    error = $"layerQueries key '{entry.Name}' is not one of the requested layers.";
                    return false;
                }

                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    error = $"layerQueries entry for layer {layerId} must be a JSON object.";
                    return false;
                }

                var definition = new ReplicaLayerQueryDefinition();
                foreach (var option in entry.Value.EnumerateObject())
                {
                    switch (option.Name.ToUpperInvariant())
                    {
                        case "QUERYOPTION":
                            if (option.Value.ValueKind != JsonValueKind.String ||
                                !TryNormalizeReplicaQueryOption(option.Value.GetString(), out var queryOption))
                            {
                                error = $"layerQueries entry for layer {layerId}: queryOption must be none, all or useFilter.";
                                return false;
                            }

                            definition.QueryOption = queryOption;
                            break;
                        case "WHERE":
                            if (option.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                            {
                                error = $"layerQueries entry for layer {layerId}: where must be a string.";
                                return false;
                            }

                            definition.Where = option.Value.GetString();
                            break;
                        case "USEGEOMETRY":
                            if (option.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            {
                                error = $"layerQueries entry for layer {layerId}: useGeometry must be a boolean.";
                                return false;
                            }

                            definition.UseGeometry = option.Value.GetBoolean();
                            break;
                        case "INCLUDERELATED":
                            if (option.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            {
                                error = $"layerQueries entry for layer {layerId}: includeRelated must be a boolean.";
                                return false;
                            }

                            if (option.Value.GetBoolean())
                            {
                                error = $"layerQueries entry for layer {layerId}: includeRelated=true is not supported because related records are not replicated. Omit it or pass false.";
                                return false;
                            }

                            break;
                        default:
                            error = $"layerQueries entry for layer {layerId} has unsupported option '{option.Name}'. Supported options: queryOption, where, useGeometry, includeRelated.";
                            return false;
                    }
                }

                layerQueries[layerId.ToString(CultureInfo.InvariantCulture)] = definition;
            }
        }

        return true;
    }

    private static bool TryNormalizeReplicaQueryOption(string? raw, out string queryOption)
    {
        queryOption = string.Empty;
        foreach (var candidate in _replicaQueryOptions)
        {
            if (string.Equals(raw?.Trim(), candidate, StringComparison.OrdinalIgnoreCase))
            {
                queryOption = candidate;
                return true;
            }
        }

        return false;
    }

    private static ReplicaScopeDefinition? DeserializeReplicaScope(string? scopeDefinition)
        => string.IsNullOrWhiteSpace(scopeDefinition)
            ? null
            : JsonSerializer.Deserialize(scopeDefinition, FeatureServerJsonContext.Default.ReplicaScopeDefinition);

    private static string? SerializeReplicaScope(ReplicaScopeDefinition? scope)
        => scope is null ? null : JsonSerializer.Serialize(scope, FeatureServerJsonContext.Default.ReplicaScopeDefinition);

    /// <summary>
    /// Resolves one layer's scope using Esri <c>layerQueries</c> semantics: a layer without an entry is
    /// filtered by the replica geometry; <c>none</c> carries no data; <c>all</c> carries every row;
    /// <c>useFilter</c> applies the layer's where clause and, unless <c>useGeometry</c> is false, the
    /// replica geometry.
    /// </summary>
    private static bool TryResolveReplicaLayerScope(
        HttpContext context,
        ReplicaScopeDefinition? scope,
        ReplicaLayerV2 layer,
        out ReplicaLayerScope layerScope,
        out string? error)
    {
        layerScope = ReplicaLayerScope.Whole;
        error = null;
        if (scope is null)
        {
            return true;
        }

        ReplicaLayerQueryDefinition? layerQuery = null;
        scope.LayerQueries?.TryGetValue(layer.PublicLayerId.ToString(CultureInfo.InvariantCulture), out layerQuery);
        var queryOption = layerQuery?.QueryOption ?? ReplicaQueryOptionUseFilter;
        if (queryOption == ReplicaQueryOptionNone)
        {
            layerScope = new ReplicaLayerScope(false, null, null, scope.OutputSrid);
            return true;
        }

        SqlFragment? sqlFilter = null;
        SpatialFilter? spatialFilter = null;
        if (queryOption == ReplicaQueryOptionUseFilter)
        {
            if (!string.IsNullOrWhiteSpace(layerQuery?.Where))
            {
                var filterService = context.RequestServices.GetRequiredService<IFilterExpressionService>();
                var parseResult = filterService.Parse(FilterLanguage.ArcGisSql, layerQuery.Where);
                if (!parseResult.IsSuccess)
                {
                    error = $"Layer {layer.PublicLayerId} where clause is invalid: {parseResult.ErrorMessage ?? "invalid filter syntax."}";
                    return false;
                }

                if (parseResult.Expression is not null)
                {
                    var translationResult = filterService.Translate(parseResult.Expression, layer.Resource);
                    if (!translationResult.IsSuccess)
                    {
                        error = $"Layer {layer.PublicLayerId} where clause is invalid: {translationResult.ErrorMessage ?? "invalid filter syntax."}";
                        return false;
                    }

                    sqlFilter = translationResult.SqlFilter;
                }
            }

            if (scope.Geometry is not null && (layerQuery?.UseGeometry ?? true))
            {
                if (!GeoServicesGeometryParser.TryParseGeoServicesGeometry(scope.Geometry, scope.GeometryType, out var parsedGeometry, out _) ||
                    parsedGeometry is null)
                {
                    error = "The replica geometry is not a valid Esri JSON geometry.";
                    return false;
                }

                try
                {
                    spatialFilter = GeoServicesSpatialFilterBuilder.BuildSpatialFilter(
                        new QueryParameters { Geometry = scope.Geometry, GeometryType = scope.GeometryType },
                        parsedGeometry,
                        scope.InputSrid ?? layer.Resource.ReadSrid());
                }
                catch (ArgumentException)
                {
                    error = $"The replica geometry cannot be applied to layer {layer.PublicLayerId}.";
                    return false;
                }
            }
        }

        layerScope = new ReplicaLayerScope(true, sqlFilter, spatialFilter, scope.OutputSrid);
        return true;
    }

    /// <summary>
    /// Assembles the changes committed in (<paramref name="sinceGeneration"/>,
    /// <paramref name="throughGeneration"/>] for the replica layers, restricted to the replica scope
    /// and to what the caller may read. When a layer holds more than
    /// <c>Limits:Replica:MaxChangesPerLayer</c> changes, the window is narrowed to an earlier generation
    /// so the response stays bounded and the caller continues from the generation reached — a backlog
    /// is never a permanent 400 (#4019). Feature payloads are read in pages of
    /// <c>Limits:Query:MaxRecordCount</c>. A <paramref name="sinceGeneration"/> of 0 on a layer the
    /// change log does not cover falls back to a scoped snapshot of the current rows.
    /// </summary>
    private static async Task<(ReplicaDelivery? Delivery, IResult? Error)> AssembleReplicaDeliveryAsync(
        HttpContext context,
        string? recipientReplicaId,
        long sinceGeneration,
        long throughGeneration,
        ReplicaLayerV2[] layers,
        ReplicaScopeDefinition? scope,
        ReplicaChangeSelection selection,
        bool returnIdsOnly,
        CancellationToken cancellationToken)
    {
        var layerScopes = new Dictionary<int, ReplicaLayerScope>(layers.Length);
        foreach (var layer in layers)
        {
            if (!TryResolveReplicaLayerScope(context, scope, layer, out var layerScope, out var scopeError))
            {
                return (null, StandardErrorHelpers.CreateBadRequest(context, "Invalid replica scope", [scopeError!]));
            }

            layerScopes[layer.PublicLayerId] = layerScope;
        }

        var changeTracker = context.RequestServices.GetRequiredService<IChangeTracker>();
        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var limits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
        var maxChangesPerLayer = Math.Max(1, limits.Replica.MaxChangesPerLayer);
        var pageSize = Math.Max(1, limits.Query.MaxRecordCount);

        var requestedThrough = Math.Max(sinceGeneration, throughGeneration);
        var through = requestedThrough;
        var storageLayerIds = layers.Select(static layer => layer.StorageLayerId).Distinct().ToArray();
        var changes = await changeTracker.GetChangesInWindowAsync(
            sinceGeneration, through, storageLayerIds, recipientReplicaId, cancellationToken).ConfigureAwait(false);

        // Narrow the window until every layer fits. The collapsed feed is ordered by each object's last
        // generation, so the generation of a layer's MaxChangesPerLayer-th change bounds a window that
        // usually fits; the window is re-read (collapse over the narrower window can differ) until it
        // does. Generations are allocated per row, so this converges; a single generation holding more
        // changes (a pre-change-tracking baseline) cannot be split and is delivered whole.
        while (TryFindNarrowerReplicaWindow(changes, maxChangesPerLayer, through, out var narrowed))
        {
            through = narrowed;
            changes = await changeTracker.GetChangesInWindowAsync(
                sinceGeneration, through, storageLayerIds, recipientReplicaId, cancellationToken).ConfigureAwait(false);
        }

        var changesByLayer = changes
            .GroupBy(static change => change.LayerId)
            .ToDictionary(static group => group.Key, static group => group.ToList());

        var deliveries = new List<ReplicaLayerDelivery>(layers.Length);
        foreach (var layer in layers)
        {
            var layerScope = layerScopes[layer.PublicLayerId];
            if (!layerScope.IncludeData)
            {
                deliveries.Add(EmptyReplicaLayerDelivery(layer, layerScope));
                continue;
            }

            long[] insertIds;
            long[] updateIds;
            long[] deleteIds;
            GeoServicesFeature[]? snapshotFeatures = null;
            if (changesByLayer.TryGetValue(layer.StorageLayerId, out var layerChangeList))
            {
                (insertIds, updateIds, deleteIds) = await FilterChangesForReadAsync(
                    context, layer, layerChangeList, featureReader, layerScope, pageSize, cancellationToken).ConfigureAwait(false);
            }
            else if (sinceGeneration == 0 &&
                     (await changeTracker.GetChangesSinceAsync(0, [layer.StorageLayerId], cancellationToken).ConfigureAwait(false)).Count == 0)
            {
                // First sync of a layer the change log does not cover (no-op change trackers, or a log
                // that never observed the layer): deliver the scoped current rows as adds. Without
                // generations there is nothing to window on, so an oversized snapshot stays a 400 that
                // names the recovery (narrow the scope or raise the limit).
                var snapshot = await featureReader.QueryAsync(
                    layer.StorageLayerId,
                    CreateReplicaFeatureQuery(layer, layerScope) with { Limit = maxChangesPerLayer },
                    cancellationToken).ConfigureAwait(false);
                if (snapshot.HasMoreResults || snapshot.Items.Length > maxChangesPerLayer)
                {
                    return (null, StandardErrorHelpers.CreateBadRequest(
                        context,
                        $"Layer {layer.PublicLayerId} has no change-tracking history and more than {maxChangesPerLayer} features in the replica scope.",
                        ["Narrow the replica with geometry or layerQueries, or raise Limits:Replica:MaxChangesPerLayer."]));
                }

                insertIds = [.. snapshot.Items.Select(static feature => feature.Id)];
                updateIds = [];
                deleteIds = [];
                var snapshotSrid = ResolveReplicaDeliverySrid(layer, layerScope);
                snapshotFeatures = [.. snapshot.Items.Select(feature => ConvertFeatureToGeoServices(feature, layer.Resource, snapshotSrid))];
            }
            else
            {
                deliveries.Add(EmptyReplicaLayerDelivery(layer, layerScope));
                continue;
            }

            insertIds = selection.Inserts ? insertIds : [];
            updateIds = selection.Updates ? updateIds : [];
            deleteIds = selection.Deletes ? deleteIds : [];

            GeoServicesFeature[]? addFeatures = null;
            GeoServicesFeature[]? updateFeatures = null;
            if (!returnIdsOnly)
            {
                if (insertIds.Length > 0)
                {
                    addFeatures = snapshotFeatures ?? await ReadReplicaFeaturesAsync(
                        featureReader, layer, layerScope, insertIds, pageSize, cancellationToken).ConfigureAwait(false);
                }

                if (updateIds.Length > 0)
                {
                    updateFeatures = await ReadReplicaFeaturesAsync(
                        featureReader, layer, layerScope, updateIds, pageSize, cancellationToken).ConfigureAwait(false);
                }
            }

            deliveries.Add(new ReplicaLayerDelivery(
                new LayerChanges
                {
                    Id = layer.PublicLayerId,
                    SpatialReference = GeoServicesGeometryConverter.CreateSpatialReference(ResolveReplicaDeliverySrid(layer, layerScope)),
                    Adds = insertIds.Length,
                    Updates = updateIds.Length,
                    Deletes = deleteIds.Length,
                    AddFeatures = addFeatures,
                    UpdateFeatures = updateFeatures,
                    DeleteIds = deleteIds.Length > 0 ? deleteIds : null
                },
                insertIds,
                updateIds));
        }

        return (new ReplicaDelivery([.. deliveries], through, through < requestedThrough), null);
    }

    /// <summary>
    /// Returns a narrower upper bound when a layer in <paramref name="changes"/> exceeds
    /// <paramref name="maxChangesPerLayer"/> and a narrower bound exists.
    /// </summary>
    private static bool TryFindNarrowerReplicaWindow(
        IReadOnlyList<FeatureChange> changes,
        int maxChangesPerLayer,
        long currentThrough,
        out long narrowedThrough)
    {
        narrowedThrough = currentThrough;
        foreach (var layerChanges in changes.GroupBy(static change => change.LayerId))
        {
            var ordered = layerChanges.Select(static change => change.Generation).Order().ToArray();
            if (ordered.Length > maxChangesPerLayer)
            {
                narrowedThrough = Math.Min(narrowedThrough, ordered[maxChangesPerLayer - 1]);
            }
        }

        // No layer over the limit, or the limit falls inside the window's first generation, which
        // cannot be split.
        return narrowedThrough < currentThrough;
    }

    private static ReplicaLayerDelivery EmptyReplicaLayerDelivery(ReplicaLayerV2 layer, ReplicaLayerScope layerScope)
        => new(new LayerChanges
        {
            Id = layer.PublicLayerId,
            SpatialReference = GeoServicesGeometryConverter.CreateSpatialReference(ResolveReplicaDeliverySrid(layer, layerScope)),
            Adds = 0,
            Updates = 0,
            Deletes = 0
        }, [], []);

    /// <summary>
    /// The SRID replica features are delivered and labelled in: the replica's <c>replicaSR</c> when one
    /// was requested (#4018), otherwise the layer's advertised SRID (#4027). An attribute-only table has none.
    /// </summary>
    private static int? ResolveReplicaDeliverySrid(ReplicaLayerV2 layer, ReplicaLayerScope layerScope)
        => layer.Resource.HasGeometry() ? layerScope.OutputSrid ?? ResolveReplicaLayerSrid(layer.Resource) : null;

    /// <summary>
    /// Scoped read query for replica payloads: Z and M kept and coordinates in the delivery SRID (#4027),
    /// with the replica's where clause and geometry applied (#4018).
    /// </summary>
    private static FeatureQuery CreateReplicaFeatureQuery(ReplicaLayerV2 layer, ReplicaLayerScope layerScope)
    {
        var query = ReplicaGeometryQuery(
            new FeatureQuery
            {
                SqlFilter = layerScope.SqlFilter,
                SpatialFilter = layerScope.SpatialFilter,
                SpatialReferenceSrid = layerScope.OutputSrid is null && layerScope.SpatialFilter is null
                    ? null
                    : layer.Resource.ReadSrid()
            },
            layer.Resource);
        return layerScope.OutputSrid is { } replicaSrid && layer.Resource.HasGeometry()
            ? query with { OutputSrid = replicaSrid }
            : query;
    }

    private static async Task<GeoServicesFeature[]> ReadReplicaFeaturesAsync(
        IFeatureReader featureReader,
        ReplicaLayerV2 layer,
        ReplicaLayerScope layerScope,
        long[] objectIds,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var features = new List<GeoServicesFeature>(objectIds.Length);
        foreach (var page in objectIds.Chunk(pageSize))
        {
            // Ids come from the already-scoped change filter, so only the geometry shape and CRS apply here.
            var result = await featureReader.QueryAsync(
                layer.StorageLayerId,
                CreateReplicaFeatureQuery(layer, layerScope with { SqlFilter = null, SpatialFilter = null }) with
                {
                    ObjectIds = ImmutableArray.Create(page),
                    Limit = page.Length
                },
                cancellationToken).ConfigureAwait(false);
            var deliverySrid = ResolveReplicaDeliverySrid(layer, layerScope);
            features.AddRange(result.Items.Select(feature => ConvertFeatureToGeoServices(feature, layer.Resource, deliverySrid)));
        }

        return [.. features];
    }

    /// <summary>
    /// Projects a delivery into the Esri extractChanges <c>edits</c> envelope (#4017).
    /// </summary>
    private static ExtractChangesLayerEdits[] BuildExtractChangesEdits(ReplicaLayerDelivery[] layers, bool returnIdsOnly)
        => [.. layers.Select(layer => returnIdsOnly
            ? new ExtractChangesLayerEdits
            {
                Id = layer.Changes.Id,
                ObjectIds = new ExtractChangesObjectIdEdits
                {
                    Adds = layer.InsertIds,
                    Updates = layer.UpdateIds,
                    Deletes = layer.Changes.DeleteIds ?? []
                }
            }
            : new ExtractChangesLayerEdits
            {
                Id = layer.Changes.Id,
                Features = new ExtractChangesFeatureEdits
                {
                    Adds = layer.Changes.AddFeatures ?? [],
                    Updates = layer.Changes.UpdateFeatures ?? [],
                    DeleteIds = layer.Changes.DeleteIds ?? []
                }
            })];

    private static ReplicaInfoLayerServerGeneration[] BuildLayerServerGens(IEnumerable<int> layerIds, long generation)
        => [.. layerIds.Distinct().Select(id => new ReplicaInfoLayerServerGeneration
        {
            Id = id,
            ServerGen = generation,
            ServerSibGen = generation
        })];

    /// <summary>
    /// Parses the Esri <c>returnInserts</c>/<c>returnUpdates</c>/<c>returnDeletes</c> selection. When none
    /// of the three is sent, every category is returned (the historical behavior Honua SDK clients rely
    /// on); once any is sent, an omitted category defaults to false as in the Esri contract (#4017).
    /// </summary>
    private static bool TryParseReplicaChangeSelection(
        IReadOnlyDictionary<string, StringValues> values,
        out ReplicaChangeSelection selection,
        out string? error)
    {
        selection = ReplicaChangeSelection.All;
        error = null;
        if (GetValueString(values, "returnInserts") is null &&
            GetValueString(values, "returnUpdates") is null &&
            GetValueString(values, "returnDeletes") is null)
        {
            return true;
        }

        if (!TryParseBoolValue(values, "returnInserts", false, out var inserts, out error) ||
            !TryParseBoolValue(values, "returnUpdates", false, out var updates, out error) ||
            !TryParseBoolValue(values, "returnDeletes", false, out var deletes, out error))
        {
            error ??= "returnInserts, returnUpdates and returnDeletes must be boolean values.";
            return false;
        }

        selection = new ReplicaChangeSelection(inserts, updates, deletes);
        return true;
    }

    /// <summary>
    /// Parses Esri <c>layerServerGens</c> (<c>[{"id":0,"serverGen":123}, ...]</c>) into a per-layer
    /// "extract since" generation. Returns a null map when the parameter is absent.
    /// </summary>
    private static bool TryParseLayerServerGens(
        IReadOnlyDictionary<string, StringValues> values,
        out Dictionary<int, long>? layerServerGens,
        out string? error)
    {
        layerServerGens = null;
        error = null;
        var raw = GetValueString(values, "layerServerGens");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        const string shapeError = "layerServerGens must be a JSON array of {\"id\": <layer id>, \"serverGen\": <generation>} objects.";
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            error = shapeError;
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                error = shapeError;
                return false;
            }

            var parsed = new Dictionary<int, long>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !TryGetPropertyIgnoreCase(entry, "id", out var idElement) ||
                    !idElement.TryGetInt32(out var layerId) ||
                    !TryGetPropertyIgnoreCase(entry, "serverGen", out var generationElement) ||
                    !generationElement.TryGetInt64(out var generation))
                {
                    error = shapeError;
                    return false;
                }

                if (generation < 0)
                {
                    error = "layerServerGens serverGen values must be non-negative integers.";
                    return false;
                }

                if (!parsed.TryAdd(layerId, generation))
                {
                    error = $"layerServerGens lists layer {layerId} more than once.";
                    return false;
                }
            }

            layerServerGens = parsed;
            return true;
        }
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Resolves the per-layer extract window: <c>layerServerGens</c> sets a layer's lower bound;
    /// <c>serverGen</c> or the first value of <c>serverGens</c> sets it for the other layers (falling back
    /// to <paramref name="defaultSinceGeneration"/>); the second value of <c>serverGens</c> caps the
    /// upper bound, which never exceeds <paramref name="currentGeneration"/> (#4017).
    /// </summary>
    private static bool TryResolveExtractWindow(
        IReadOnlyDictionary<string, StringValues> values,
        IReadOnlyDictionary<int, long>? layerServerGens,
        IReadOnlyCollection<int> layerIds,
        long defaultSinceGeneration,
        long currentGeneration,
        out Dictionary<int, long> sinceByLayer,
        out long throughGeneration,
        out string? error)
    {
        sinceByLayer = [];
        throughGeneration = currentGeneration;
        if (!TryParseServerGenRange(values, out var minGeneration, out var maxGeneration, out error))
        {
            return false;
        }

        if (maxGeneration is { } upper)
        {
            if (minGeneration > upper)
            {
                error = "serverGens must be [minServerGen, maxServerGen] with minServerGen <= maxServerGen.";
                return false;
            }

            throughGeneration = Math.Min(upper, currentGeneration);
        }

        if (layerServerGens is not null)
        {
            var unknown = layerServerGens.Keys.Where(id => !layerIds.Contains(id)).ToArray();
            if (unknown.Length > 0)
            {
                error = $"layerServerGens references layer {unknown[0]}, which is not part of this request.";
                return false;
            }
        }

        foreach (var layerId in layerIds)
        {
            var since = layerServerGens is not null && layerServerGens.TryGetValue(layerId, out var layerGeneration)
                ? layerGeneration
                : minGeneration ?? defaultSinceGeneration;
            sinceByLayer[layerId] = Math.Min(since, throughGeneration);
        }

        return true;
    }

    /// <summary>
    /// Parses <c>serverGen</c> / <c>serverGens</c>: a single integer, or a JSON array whose first value is
    /// the exclusive lower bound and whose optional second value is the inclusive upper bound.
    /// </summary>
    private static bool TryParseServerGenRange(
        IReadOnlyDictionary<string, StringValues> values,
        out long? minGeneration,
        out long? maxGeneration,
        out string? error)
    {
        minGeneration = null;
        maxGeneration = null;
        error = null;

        var raw = GetValueString(values, "serverGen") ?? GetValueString(values, "serverGens");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single))
        {
            if (single < 0)
            {
                error = "serverGen must be a non-negative integer.";
                return false;
            }

            minGeneration = single;
            return true;
        }

        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
        {
            error = "serverGen must be an integer or a JSON array of integers.";
            return false;
        }

        long[]? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(trimmed, FeatureServerJsonContext.Default.Int64Array);
        }
        catch (JsonException)
        {
            error = "serverGens must be an integer or a JSON array of integers.";
            return false;
        }

        if (parsed is not { Length: > 0 })
        {
            return true;
        }

        if (parsed.Length > 2)
        {
            error = "serverGens must be [minServerGen] or [minServerGen, maxServerGen].";
            return false;
        }

        if (parsed.Any(static generation => generation < 0))
        {
            error = "serverGens values must be non-negative integers.";
            return false;
        }

        minGeneration = parsed[0];
        maxGeneration = parsed.Length == 2 ? parsed[1] : null;
        return true;
    }

    private const string ReplicaSyncModelNone = "none";

    private static readonly string[] _replicaQueryOptions = [ReplicaQueryOptionAll, ReplicaQueryOptionNone, ReplicaQueryOptionUseFilter];

    private static readonly string[] _replicaSyncModels = ["perReplica", "perLayer", ReplicaSyncModelNone];

    /// <summary>
    /// Normalizes the Esri <c>syncModel</c>; an absent value keeps the historical <c>perReplica</c>
    /// default, and anything other than perReplica, perLayer or none is rejected (#4018).
    /// </summary>
    private static bool TryNormalizeReplicaSyncModel(string? raw, out string syncModel)
    {
        syncModel = _replicaSyncModels[0];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        foreach (var candidate in _replicaSyncModels)
        {
            if (string.Equals(raw.Trim(), candidate, StringComparison.OrdinalIgnoreCase))
            {
                syncModel = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Esri <c>replicaOptions</c> carries server-side replica behaviors this server does not implement.
    /// An empty object is accepted; any option is rejected by name instead of being ignored (#4018).
    /// </summary>
    private static IResult? ValidateReplicaOptions(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values)
    {
        var raw = GetValueString(values, "replicaOptions");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string[] optionNames;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Invalid replicaOptions parameter",
                    ["replicaOptions must be a JSON object."]);
            }

            optionNames = [.. document.RootElement.EnumerateObject().Select(static option => option.Name)];
        }
        catch (JsonException)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Invalid replicaOptions parameter",
                ["replicaOptions must be a JSON object."]);
        }

        return optionNames.Length == 0
            ? null
            : StandardErrorHelpers.CreateBadRequest(context, "replicaOptions are not supported",
                [$"This server does not implement replicaOptions ({string.Join(", ", optionNames)}). Omit replicaOptions or pass an empty object."]);
    }

    /// <summary>
    /// Parses the extractChanges payload options: <c>returnIdsOnly</c>, the insert/update/delete
    /// selection, and <c>returnExtentOnly</c>, which is rejected because no change extent is computed.
    /// </summary>
    private static bool TryParseExtractChangeOptions(
        HttpContext context,
        IReadOnlyDictionary<string, StringValues> values,
        out ReplicaChangeSelection selection,
        out bool returnIdsOnly,
        out IResult? error)
    {
        selection = ReplicaChangeSelection.All;
        error = null;
        if (!TryParseBoolValue(values, "returnIdsOnly", false, out returnIdsOnly, out var returnIdsOnlyError))
        {
            error = StandardErrorHelpers.CreateBadRequest(context, "Invalid returnIdsOnly parameter",
                [returnIdsOnlyError ?? "returnIdsOnly must be a boolean value."]);
            return false;
        }

        if (!TryParseReplicaChangeSelection(values, out selection, out var selectionError))
        {
            error = StandardErrorHelpers.CreateBadRequest(context, "Invalid change selection", [selectionError!]);
            return false;
        }

        if (!TryParseBoolValue(values, "returnExtentOnly", false, out var returnExtentOnly, out var returnExtentOnlyError))
        {
            error = StandardErrorHelpers.CreateBadRequest(context, "Invalid returnExtentOnly parameter",
                [returnExtentOnlyError ?? "returnExtentOnly must be a boolean value."]);
            return false;
        }

        if (returnExtentOnly)
        {
            error = StandardErrorHelpers.CreateBadRequest(context, "returnExtentOnly is not supported",
                ["This server does not compute change extents. Omit returnExtentOnly or pass returnExtentOnly=false."]);
            return false;
        }

        return true;
    }

    /// <summary>Changes extracted for layers that may start from different generations.</summary>
    private sealed record ReplicaExtractResult(
        ReplicaLayerDelivery[] Layers,
        IReadOnlyDictionary<int, long> ThroughByLayer,
        long MinSinceGeneration,
        bool ExceededTransferLimit);

    /// <summary>
    /// Delivers an extract whose layers may start from different generations (<c>layerServerGens</c>):
    /// layers sharing a lower bound are assembled together, each group windowed independently.
    /// </summary>
    private static async Task<(ReplicaExtractResult? Result, IResult? Error)> DeliverExtractWindowAsync(
        HttpContext context,
        string? replicaId,
        ReplicaLayerV2[] layers,
        IReadOnlyDictionary<int, long> sinceByLayer,
        long throughGeneration,
        ReplicaScopeDefinition? scope,
        ReplicaChangeSelection selection,
        bool returnIdsOnly,
        CancellationToken cancellationToken)
    {
        var deliveries = new List<ReplicaLayerDelivery>(layers.Length);
        var throughByLayer = new Dictionary<int, long>(layers.Length);
        var exceeded = false;
        foreach (var group in layers.GroupBy(layer => sinceByLayer[layer.PublicLayerId]).OrderBy(static group => group.Key))
        {
            var (delivery, error) = await AssembleReplicaDeliveryAsync(
                context, replicaId, group.Key, throughGeneration, [.. group], scope, selection, returnIdsOnly, cancellationToken).ConfigureAwait(false);
            if (error is not null)
            {
                return (null, error);
            }

            deliveries.AddRange(delivery!.Layers);
            foreach (var layer in group)
            {
                throughByLayer[layer.PublicLayerId] = delivery.ThroughGeneration;
            }

            exceeded |= delivery.ExceededTransferLimit;
        }

        return (new ReplicaExtractResult(
            [.. deliveries.OrderBy(static delivery => delivery.Changes.Id)],
            throughByLayer,
            sinceByLayer.Values.DefaultIfEmpty(throughGeneration).Min(),
            exceeded), null);
    }

    private static ExtractChangesResponse CreateExtractChangesResponse(
        string? replicaId,
        ReplicaExtractResult result,
        bool returnIdsOnly)
    {
        var reached = result.ThroughByLayer.Values.DefaultIfEmpty(0).Min();
        return new ExtractChangesResponse
        {
            Success = true,
            ReplicaId = replicaId,
            LayerChanges = [.. result.Layers.Select(static layer => layer.Changes)],
            ServerGen = reached,
            MinServerGen = result.MinSinceGeneration,
            MaxServerGen = reached,
            TransportType = ReplicaEmbeddedTransportType,
            ResponseType = "esriReplicaResponseTypeEdits",
            LayerServerGens = [.. result.ThroughByLayer
                .OrderBy(static entry => entry.Key)
                .Select(static entry => new ReplicaInfoLayerServerGeneration
                {
                    Id = entry.Key,
                    ServerGen = entry.Value,
                    ServerSibGen = entry.Value
                })],
            Edits = BuildExtractChangesEdits(result.Layers, returnIdsOnly),
            ExceededTransferLimit = result.ExceededTransferLimit ? true : null
        };
    }

    /// <summary>
    /// Describes each replica layer's stored scope in the replica info resource instead of the
    /// hard-coded <c>queryOption=all</c>, <c>useGeometry=true</c>, <c>where=""</c> it used to report (#4018).
    /// </summary>
    private static ReplicaInfoLayer[] BuildReplicaInfoLayers(ReplicaLayerV2[] layers, ReplicaScopeDefinition? scope)
        => [.. layers.Select(layer =>
        {
            ReplicaLayerQueryDefinition? layerQuery = null;
            scope?.LayerQueries?.TryGetValue(layer.PublicLayerId.ToString(CultureInfo.InvariantCulture), out layerQuery);
            var queryOption = layerQuery?.QueryOption
                ?? (layerQuery is not null || scope?.Geometry is not null ? ReplicaQueryOptionUseFilter : ReplicaQueryOptionAll);
            var usesFilter = queryOption == ReplicaQueryOptionUseFilter;
            return new ReplicaInfoLayer
            {
                Id = layer.PublicLayerId,
                QueryOption = queryOption,
                UseGeometry = usesFilter && scope?.Geometry is not null && (layerQuery?.UseGeometry ?? true),
                IncludeRelated = false,
                Where = usesFilter ? layerQuery?.Where ?? string.Empty : string.Empty
            };
        })];
}
