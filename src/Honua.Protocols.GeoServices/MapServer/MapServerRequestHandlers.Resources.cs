// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.GeoServices.MapServer;

internal static partial class MapServerEndpoints
{
    /// <summary>
    /// Handle the MapServer allLayersAndTables resource: returns the full layer
    /// and table metadata for every accessible layer/table in a single document.
    /// </summary>
    private static async Task<IResult> HandleAllLayersAndTables(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        if (!TryValidateMetadataFormat(context.Request.Query, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, formatError ?? "Output format is not supported.");
        }

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "metadata",
            HonuaTelemetry.Protocols.MapServer,
            "*");
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId)
            .WithTag(HonuaTelemetry.Tags.Operation, "all-layers-and-tables");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
                resourceValidator,
                serviceId,
                ServiceProtocols.MapServer,
                context,
                cancellationToken: cancellationToken);
            if (!serviceResult.IsValid)
            {
                return serviceResult.ErrorResult!;
            }

            var service = serviceResult.Service!;
            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var publishedLayers = ResolveMapServerMetadataLayers(snapshot, service);

            var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
                context,
                publishedLayers.Select(static layer => layer.Resource),
                service);
            if (accessError != null)
            {
                return accessError;
            }

            var visibleLayers = publishedLayers
                .Where(layer => AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service))
                .ToArray();

            var limitsOptions = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value;
            var maxRecordCount = limitsOptions.Query.MaxRecordCount;

            var layerResponses = new List<MapServerLayerResponse>();
            var tableResponses = new List<MapServerLayerResponse>();
            foreach (var layer in visibleLayers)
            {
                var drawingInfo = ResolveMapServerDrawingInfo(layer.Resource, snapshot);
                var response = MapLayerToMapServerLayerResponse(
                    service,
                    layer.Publication,
                    layer.Resource,
                    snapshot,
                    maxRecordCount,
                    drawingInfo);

                if (HasMapServerGeometry(layer.Resource))
                {
                    layerResponses.Add(response);
                }
                else
                {
                    tableResponses.Add(response);
                }
            }

            var result = new AllLayersAndTablesResponse
            {
                Layers = [.. layerResponses],
                Tables = [.. tableResponses]
            };

            stopwatch.Stop();
            scope.SetSuccess(layerResponses.Count + tableResponses.Count);
            scope.CategorizeLatency(stopwatch.Elapsed.TotalMilliseconds);

            return Results.Json(result, MapServerJsonContext.Default.AllLayersAndTablesResponse, contentType: "application/json");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            scope.RecordException(ex);
            throw;
        }
    }

    /// <summary>
    /// Handle the MapServer queryDomains resource: returns the coded-value and
    /// range domains referenced by the requested layers.
    /// </summary>
    private static async Task<IResult> HandleQueryDomains(HttpContext context)
    {
        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        if (!TryValidateMetadataFormat(context.Request.Query, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, formatError ?? "Output format is not supported.");
        }

        var serviceError = RouteValidationHelpers.ValidateServiceId(context, out var serviceId);
        if (serviceError is not null)
        {
            return serviceError;
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "queryDomains",
            HonuaTelemetry.Protocols.MapServer,
            "*");
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId)
            .WithTag(HonuaTelemetry.Tags.Operation, "query-domains");

        try
        {
            var resourceValidator = context.RequestServices.GetRequiredService<IResourceValidator>();
            var serviceResult = await ServiceResourceValidationHelpers.ValidateServiceV2Async(
                resourceValidator,
                serviceId,
                ServiceProtocols.MapServer,
                context,
                cancellationToken: cancellationToken);
            if (!serviceResult.IsValid)
            {
                return serviceResult.ErrorResult!;
            }

            var service = serviceResult.Service!;
            var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
            var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var publishedLayers = ResolveMapServerMetadataLayers(snapshot, service);

            if (!TryResolveQueryDomainsLayers(context.Request.Query, publishedLayers, out var selectedLayers, out var selectionError))
            {
                return StandardErrorHelpers.CreateBadRequest(context, selectionError ?? "Invalid layers parameter.");
            }

            var accessError = AccessPolicyHelpers.RequireAnyResourceAccess(
                context,
                selectedLayers.Select(static layer => layer.Resource),
                service);
            if (accessError != null)
            {
                return accessError;
            }

            var accessibleLayers = selectedLayers
                .Where(layer => AccessPolicyHelpers.IsResourceAccessible(context, layer.Resource, service))
                .ToArray();

            // Esri MapServer queryDomains returns each distinct domain once across
            // the requested layers; de-duplicate by (domain name, field name).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var domains = new List<MapServerDomainInfo>();
            foreach (var layer in accessibleLayers)
            {
                foreach (var field in layer.Resource.SchemaFields)
                {
                    if (field.Hidden || field.Domain is null)
                    {
                        continue;
                    }

                    var key = $"{field.Domain.Name}|{field.Name}";
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    domains.Add(MapMapServerDomainInfo(field));
                }
            }

            var response = new MapServerQueryDomainsResponse { Domains = [.. domains] };
            scope.SetSuccess(domains.Count);
            return Results.Json(response, MapServerJsonContext.Default.MapServerQueryDomainsResponse, contentType: "application/json");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            scope.RecordException(ex);
            throw;
        }
    }

    /// <summary>
    /// Handle the MapServer Feature child resource
    /// (<c>.../MapServer/{layerId}/{featureId}</c>): returns the single feature
    /// identified by its object id by delegating to the shared FeatureServer
    /// query pipeline and reshaping the result into <c>{feature:{...}}</c>.
    /// </summary>
    private static async Task<IResult> HandleFeatureResource(string serviceId, int layerId, long featureId, HttpContext context)
    {
        if (!TryValidateMetadataFormat(context.Request.Query, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, formatError ?? "Output format is not supported.");
        }

        var serviceError = await TryValidateMapServerServiceAsync(serviceId, context);
        if (serviceError is not null)
        {
            return serviceError;
        }

        using var scope = HonuaTelemetryScope.StartFeature(
            "feature",
            HonuaTelemetry.Protocols.MapServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.ServiceId, serviceId)
            .WithTag(HonuaTelemetry.Tags.Operation, "feature-resource");

        var cancellationToken = GeoServicesRequestValueHelpers.GetTimeoutAwareCancellationToken(context);
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();

        // Translate the Feature child resource into an object-id query against the
        // shared FeatureServer query pipeline so CRS, geometry, and field shaping
        // stay consistent with the layer query endpoint.
        var queryValues = new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectIds"] = featureId.ToString(CultureInfo.InvariantCulture),
            ["where"] = "1=1",
            ["outFields"] = "*",
            ["returnGeometry"] = "true",
            ["f"] = "json"
        };

        if (!FeatureServerEndpoints.TryParseQueryParameters(queryValues, out var queryParams, out var parseError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [parseError ?? "Invalid feature request."]);
        }

        var queryHandler = context.RequestServices.GetRequiredService<FeatureServerQueryHandler>();
        var (queryResponse, errorResult) = await queryHandler.HandleServiceQueryLayerAsync(
            serviceId,
            layerId,
            queryParams,
            context,
            queryValidator,
            ServiceProtocols.MapServer,
            cancellationToken);
        if (errorResult is not null)
        {
            return errorResult;
        }

        var feature = queryResponse?.Features is { Length: > 0 } features ? features[0] : null;
        if (feature is null)
        {
            return StandardErrorHelpers.CreateNotFound(context,
                $"Feature '{featureId}' was not found in layer {layerId}.");
        }

        // Serialize the shared GeoServices feature ({attributes, geometry}) and
        // wrap it in the Esri Feature-resource envelope.
        var featureElement = JsonSerializer.SerializeToElement(feature, FeatureServerJsonContext.Default.GeoServicesFeature);
        var response = new FeatureResourceResponse { Feature = featureElement };

        scope.SetSuccess(1);
        return Results.Json(response, MapServerJsonContext.Default.FeatureResourceResponse, contentType: "application/json");
    }

    private static bool TryResolveQueryDomainsLayers(
        IQueryCollection query,
        IReadOnlyList<MapServerMetadataLayerDescriptor> allLayers,
        out MapServerMetadataLayerDescriptor[] selected,
        out string? error)
    {
        error = null;
        selected = [.. allLayers];

        if (!query.TryGetValue("layers", out var layersRaw) || StringValues.IsNullOrEmpty(layersRaw))
        {
            return true;
        }

        var raw = layersRaw.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var requestedIds = new HashSet<int>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(token.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                error = "layers must contain integer layer identifiers.";
                return false;
            }

            requestedIds.Add(id);
        }

        var matched = allLayers.Where(layer => requestedIds.Contains(layer.PublicLayerId)).ToArray();
        if (matched.Length != requestedIds.Count)
        {
            error = "layers must reference valid layer identifiers in the service.";
            return false;
        }

        selected = matched;
        return true;
    }

    private static MapServerDomainInfo MapMapServerDomainInfo(MetadataV2Field field)
    {
        var domain = field.Domain!;
        return new MapServerDomainInfo
        {
            Type = domain.Type,
            Name = domain.Name,
            FieldName = field.Name,
            FieldType = MapFieldTypeToGeoServicesV2(field.Type),
            CodedValues = domain.CodedValues.Count == 0
                ? null
                : [.. domain.CodedValues.Select(static codedValue => new MapServerDomainCodedValue
                {
                    Name = codedValue.Name,
                    Code = codedValue.Code.Clone()
                })],
            Range = domain.Range is null
                ? null
                : [.. domain.Range.Select(static value => (object)value.Clone())],
            MergePolicy = domain.MergePolicy,
            SplitPolicy = domain.SplitPolicy
        };
    }
}
