// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Service responsible for WFS 2.0 query operations.
/// Follows Single Responsibility Principle by handling only query-related operations.
/// </summary>
internal sealed class Wfs20QueryService : IWfs20QueryService
{
    private readonly ILogger<Wfs20QueryService> _logger;
    private readonly ILayerCatalog _layerCatalog;
    private readonly IFeatureReader _featureReader;
    private readonly IWfs20FeatureFormatConverter _formatConverter;

    public Wfs20QueryService(
        ILogger<Wfs20QueryService> logger,
        ILayerCatalog layerCatalog,
        IFeatureReader featureReader,
        IWfs20FeatureFormatConverter formatConverter)
    {
        _logger = logger;
        _layerCatalog = layerCatalog;
        _featureReader = featureReader;
        _formatConverter = formatConverter;
    }

    public async Task<IResult> HandleGetFeatureAsync(
        HttpContext context,
        Dictionary<string, object> queryParameters,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.get_feature", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "GetFeature");

        // Parse query parameters first to get the type names and format
        var typeNames = queryParameters.GetValueOrDefault("typenames")?.ToString() ?? "unknown";
        var outputFormat = queryParameters.GetValueOrDefault("outputFormat")?.ToString() ?? "application/gml+xml; version=3.2";

        Wfs20Log.GetFeatureRequested(_logger, typeNames, outputFormat);

        try
        {
            // Parse remaining query parameters
            var count = ParseCount(queryParameters.GetValueOrDefault("count")?.ToString());

            // Get authorized layers
            var authorizedLayers = await GetAuthorizedLayersAsync(context, typeNames, cancellationToken);

            if (authorizedLayers.Length == 0)
            {
                return Results.Ok(GenerateEmptyFeatureCollection(outputFormat));
            }

            // Execute query for each layer
            var allFeatures = new List<object>();
            foreach (var layer in authorizedLayers)
            {
                var features = await QueryLayerFeaturesAsync(layer, queryParameters, count, cancellationToken);
                allFeatures.AddRange(features);
            }

            // Convert to requested output format
            // TODO: Implement proper format conversion using the format converter
            var result = "Feature collection output would be here";

            Wfs20Log.GetFeatureReturned(_logger, allFeatures.Count, allFeatures.Count.ToString());
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            activity?.SetTag(HonuaTelemetry.Tags.Error, "true");
            activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, ex.Message);
            throw;
        }
    }

    public async Task<IResult> HandleStoredQueryGetFeatureAsync(
        HttpContext context,
        string storedQueryId,
        string? featureId,
        string? outputFormat,
        string? count,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.stored_query_get_feature", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "StoredQueryGetFeature");

        if (storedQueryId == "urn:ogc:def:query:OGC-WFS::GetFeatureById" && !string.IsNullOrEmpty(featureId))
        {
            return await HandleGetFeatureByIdAsync(context, featureId, outputFormat, cancellationToken);
        }

        return Results.BadRequest($"Unsupported stored query: {storedQueryId}");
    }

    public async Task<IResult> ListStoredQueriesAsync(
        HttpContext context,
        CancellationToken cancellationToken = default)
    {
        var xml = """
<?xml version="1.0" encoding="UTF-8"?>
<wfs:ListStoredQueriesResponse
    xmlns:wfs="http://www.opengis.net/wfs/2.0"
    xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
    xsi:schemaLocation="http://www.opengis.net/wfs/2.0 http://schemas.opengis.net/wfs/2.0/wfs.xsd">
    <wfs:StoredQuery id="urn:ogc:def:query:OGC-WFS::GetFeatureById">
        <wfs:Title>Get feature by identifier</wfs:Title>
        <wfs:Abstract>Returns the feature whose identifier matches the specified value.</wfs:Abstract>
    </wfs:StoredQuery>
</wfs:ListStoredQueriesResponse>
""";

        return Results.Text(xml, "application/xml");
    }

    public async Task<IResult> DescribeStoredQueriesAsync(
        HttpContext context,
        string? storedQueryIds,
        CancellationToken cancellationToken = default)
    {
        var xml = """
<?xml version="1.0" encoding="UTF-8"?>
<wfs:DescribeStoredQueriesResponse
    xmlns:wfs="http://www.opengis.net/wfs/2.0"
    xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
    xsi:schemaLocation="http://www.opengis.net/wfs/2.0 http://schemas.opengis.net/wfs/2.0/wfs.xsd">
    <wfs:StoredQueryDescription id="urn:ogc:def:query:OGC-WFS::GetFeatureById">
        <wfs:Title>Get feature by identifier</wfs:Title>
        <wfs:Abstract>Returns the feature whose identifier matches the specified value.</wfs:Abstract>
        <wfs:Parameter name="id" type="xsd:string">
            <wfs:Title>Identifier</wfs:Title>
            <wfs:Abstract>Feature identifier</wfs:Abstract>
        </wfs:Parameter>
        <wfs:QueryExpressionText
            returnFeatureTypes="*"
            language="urn:ogc:def:queryLanguage:OGC-WFS::WFS_QueryExpression"
            isPrivate="false">
            <wfs:Query typeNames="*">
                <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
                    <fes:PropertyIsEqualTo>
                        <fes:PropertyName>@gml:id</fes:PropertyName>
                        <fes:Literal>${id}</fes:Literal>
                    </fes:PropertyIsEqualTo>
                </fes:Filter>
            </wfs:Query>
        </wfs:QueryExpressionText>
    </wfs:StoredQueryDescription>
</wfs:DescribeStoredQueriesResponse>
""";

        return Results.Text(xml, "application/xml");
    }

    public async Task<IResult> HandleGetPropertyValueAsync(
        HttpContext context,
        Dictionary<string, object> queryParameters,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.get_property_value", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "GetPropertyValue");

        // TODO: Implement GetPropertyValue operation
        // This is a placeholder implementation
        var xml = """
<?xml version="1.0" encoding="UTF-8"?>
<wfs:ValueCollection
    xmlns:wfs="http://www.opengis.net/wfs/2.0"
    xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
    xsi:schemaLocation="http://www.opengis.net/wfs/2.0 http://schemas.opengis.net/wfs/2.0/wfs.xsd">
    <wfs:member>
        <wfs:Value>PropertyValue</wfs:Value>
    </wfs:member>
</wfs:ValueCollection>
""";

        return Results.Text(xml, "application/xml");
    }

    private async Task<IResult> HandleGetFeatureByIdAsync(
        HttpContext context,
        string featureId,
        string? outputFormat,
        CancellationToken cancellationToken)
    {
        // TODO: Implement feature-by-ID lookup
        var format = outputFormat ?? "application/gml+xml; version=3.2";
        var emptyCollection = GenerateEmptyFeatureCollection(format);
        return Results.Ok(emptyCollection);
    }

    private async Task<LayerDefinition[]> GetAuthorizedLayersAsync(
        HttpContext context,
        string? typeNames,
        CancellationToken cancellationToken)
    {
        var allLayers = await _layerCatalog.ListLayersAsync(cancellationToken);

        if (!string.IsNullOrEmpty(typeNames))
        {
            var requestedTypeNames = typeNames.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            allLayers = allLayers.Where(layer => requestedTypeNames.Contains(layer.Name)).ToArray();
        }

        return allLayers
            .Where(IsVectorLayer)
            .ToArray();
    }

    private static bool IsVectorLayer(LayerDefinition layer)
    {
        return layer.GeometryType != GeometryType.None;
    }

    private async Task<IEnumerable<object>> QueryLayerFeaturesAsync(
        LayerDefinition layer,
        Dictionary<string, object> queryParameters,
        int? maxCount,
        CancellationToken cancellationToken)
    {
        // TODO: Implement actual feature querying using IFeatureReader
        // This is a placeholder that returns empty results
        return Array.Empty<object>();
    }

    private static int? ParseCount(string? countStr)
    {
        if (string.IsNullOrEmpty(countStr))
            return null;

        return int.TryParse(countStr, out var count) ? count : null;
    }

    private static string GenerateEmptyFeatureCollection(string outputFormat)
    {
        if (outputFormat.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return """
{
  "type": "FeatureCollection",
  "numberMatched": 0,
  "numberReturned": 0,
  "features": []
}
""";
        }

        return """
<?xml version="1.0" encoding="UTF-8"?>
<wfs:FeatureCollection
    xmlns:wfs="http://www.opengis.net/wfs/2.0"
    xmlns:gml="http://www.opengis.net/gml/3.2"
    xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
    xsi:schemaLocation="http://www.opengis.net/wfs/2.0 http://schemas.opengis.net/wfs/2.0/wfs.xsd"
    numberMatched="0"
    numberReturned="0"/>
""";
    }
}