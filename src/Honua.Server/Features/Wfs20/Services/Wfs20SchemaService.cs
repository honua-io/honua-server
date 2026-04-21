// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Service responsible for WFS 2.0 schema operations.
/// Follows Single Responsibility Principle by handling only schema-related operations.
/// </summary>
internal sealed class Wfs20SchemaService : IWfs20SchemaService
{
    private readonly ILogger<Wfs20SchemaService> _logger;
    private readonly ILayerCatalog _layerCatalog;
    private readonly IWfs20FeatureTypeSchemaGenerator _schemaGenerator;

    public Wfs20SchemaService(
        ILogger<Wfs20SchemaService> logger,
        ILayerCatalog layerCatalog,
        IWfs20FeatureTypeSchemaGenerator schemaGenerator)
    {
        _logger = logger;
        _layerCatalog = layerCatalog;
        _schemaGenerator = schemaGenerator;
    }

    public async Task<string> DescribeFeatureTypeAsync(
        HttpContext context,
        string? typeNames,
        string? outputFormat,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.describe_feature_type", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "DescribeFeatureType");

        Wfs20Log.DescribeFeatureTypeRequested(_logger, typeNames ?? "all");

        try
        {
            // Get authorized layers that match the requested type names
            var authorizedLayers = await GetAuthorizedLayersAsync(context, typeNames, cancellationToken);

            if (authorizedLayers.Length == 0)
            {
                _logger.LogWarning("No authorized feature types available for schema generation");
                return GenerateEmptySchema();
            }

            // Generate schema for the authorized feature types
            // TODO: Implement proper schema generation for multiple layers
            var schemaDocument = _schemaGenerator.GenerateFeatureTypeSchema(
                authorizedLayers[0],
                "http://honua.io/wfs",
                authorizedLayers[0].Name);
            var schema = schemaDocument.ToString();

            Wfs20Log.DescribeFeatureTypeReturned(_logger, authorizedLayers.Length);
            return schema;
        }
        catch (Exception ex)
        {
            activity?.SetTag(HonuaTelemetry.Tags.Error, "true");
            activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, ex.Message);
            throw;
        }
    }

    private async Task<LayerDefinition[]> GetAuthorizedLayersAsync(
        HttpContext context,
        string? typeNames,
        CancellationToken cancellationToken)
    {
        var allLayers = await _layerCatalog.ListLayersAsync(cancellationToken);

        // Filter by type names if specified
        if (!string.IsNullOrEmpty(typeNames))
        {
            var requestedTypeNames = typeNames.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            allLayers = allLayers.Where(layer => requestedTypeNames.Contains(layer.Name)).ToArray();
        }

        // Filter to vector layers only
        return allLayers
            .Where(IsVectorLayer)
            .ToArray();
    }

    private static bool IsVectorLayer(LayerDefinition layer)
    {
        // TODO: Implement proper vector layer detection based on geometry type
        return layer.GeometryType != GeometryType.None;
    }

    private static string GenerateEmptySchema()
    {
        return """
<?xml version="1.0" encoding="UTF-8"?>
<xsd:schema xmlns:xsd="http://www.w3.org/2001/XMLSchema"
            xmlns:gml="http://www.opengis.net/gml/3.2"
            targetNamespace="http://honua.io/wfs"
            xmlns:honua="http://honua.io/wfs"
            elementFormDefault="qualified">
    <xsd:import namespace="http://www.opengis.net/gml/3.2"
                schemaLocation="http://schemas.opengis.net/gml/3.2.1/gml.xsd"/>
</xsd:schema>
""";
    }
}