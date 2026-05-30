// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Protocols.Ogc.Common;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Validation;
using Honua.Protocols.Ogc.Api.Features.Models;
using Honua.ServiceDefaults;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.Ogc.Api.Features;

/// <summary>
/// OGC API Features extension for H3 hexagonal grid aggregation
/// </summary>
internal static class H3Endpoints
{
    private const string OgcFeaturesProtocolName = "OgcFeatures";

    /// <summary>
    /// Maps H3 aggregation endpoint for OGC API Features collections
    /// </summary>
    public static IEndpointRouteBuilder MapH3Endpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ogc/features/collections/{collectionId}/h3", HandleGetH3)
            .WithDisplayName("OGC API Features H3 Aggregation")
            .WithName("OgcFeaturesH3")
            .WithSummary("H3 hexagonal grid aggregation for a collection")
            .WithDescription("Groups collection features into H3 cells at a configurable resolution and returns GeoJSON features with aggregate statistics per cell")
            .WithTags("OGC API Features");

        return endpoints;
    }

    private static async Task<IResult> HandleGetH3(
        string collectionId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        // Use timeout-aware token (consistent with other OGC endpoints)
        cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);

        // Validate query parameters before any work
        var validationError = OgcCommonUtilities.ValidateQueryParameters(
            context.Request, OgcFeaturesUtilities.AllowedQueryParameters.H3);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        using var activity = HonuaTelemetry.ActivitySource.StartActivity("ogc.features.h3");
        activity?.SetTag("ogc.collectionId", collectionId);

        // Parse resolution (required) — validate input before checking server capabilities
        var resolutionStr = context.Request.Query["resolution"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(resolutionStr) ||
            !int.TryParse(resolutionStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resolution))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "resolution parameter is required",
                [$"resolution must be an integer between {H3AggregationQuery.MinResolution} and {H3AggregationQuery.MaxResolution}."]);
        }

        if (resolution < H3AggregationQuery.MinResolution || resolution > H3AggregationQuery.MaxResolution)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid resolution",
                [$"resolution must be between {H3AggregationQuery.MinResolution} and {H3AggregationQuery.MaxResolution}."]);
        }

        activity?.SetTag("h3.resolution", resolution);

        // Validate collection access with OGC protocol check
        var layerValidation = await LayerValidationHelpers.ValidateCollectionWithAccessV2Async(
            context,
            collectionId,
            cancellationToken: cancellationToken,
            requiredProtocol: OgcFeaturesProtocolName);
        if (!layerValidation.IsValid)
        {
            return layerValidation.ErrorResult!;
        }
        var resource = layerValidation.Resource!;
        var publication = layerValidation.Publication!;

        var graphProvider = context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>();
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var storageLayerId = publication.LayerIndex
            ?? snapshot.ResolveStorageLayerId(publication)
            ?? snapshot.ResolveStorageLayerId(resource);
        if (storageLayerId is not { } layerId)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Collection '{collectionId}' has no storage binding.");
        }

        // Check h3-pg extension availability
        var h3Error = await H3CapabilityHelpers.ValidateH3AvailabilityAsync(context, cancellationToken);
        if (h3Error is not null)
        {
            return h3Error;
        }

        var queryLimits = context.RequestServices.GetRequiredService<IOptions<LimitsOptions>>().Value.Query;
        var h3Query = new H3AggregationQuery
        {
            Resolution = resolution,
            MaxCells = queryLimits.MaxH3CellsPerQuery
        };

        var featureQuery = new FeatureQuery
        {
            SpatialReferenceSrid = resource.ReadSrid() ?? SpatialReference.WGS84.Srid
        };

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var rows = await featureReader.QueryH3Async(layerId, featureQuery, h3Query, cancellationToken);

        // Return as GeoJSON FeatureCollection using typed models for AOT safety
        var features = rows.Select(row =>
        {
            var properties = new Dictionary<string, object?>(row);

            // Extract cellGeometry and convert to typed geometry
            SimpleGeoJsonGeometry? geometry = null;
            if (properties.Remove("cellGeometry", out var cellGeoJson) && cellGeoJson is string geoJsonStr)
            {
                try
                {
                    using var doc = JsonDocument.Parse(geoJsonStr);
                    var root = doc.RootElement;
                    var geoType = root.GetProperty("type").GetString() ?? "Polygon";
                    string? coordsJson = root.TryGetProperty("coordinates", out var coords)
                        ? coords.GetRawText()
                        : null;

                    geometry = new SimpleGeoJsonGeometry
                    {
                        Type = geoType,
                        CoordinatesJson = coordsJson
                    };
                }
                catch (JsonException)
                {
                    // If parsing fails, include as string property
                    properties["cellGeometry"] = cellGeoJson;
                }
            }

            return new GeoJsonFeature
            {
                Geometry = geometry,
                Properties = properties
            };
        }).ToArray();

        var featureCollection = new FeatureCollection
        {
            Features = features,
            NumberReturned = features.Length
        };

        return Results.Json(featureCollection, OgcJsonContext.Default.FeatureCollection, contentType: "application/geo+json");
    }
}
