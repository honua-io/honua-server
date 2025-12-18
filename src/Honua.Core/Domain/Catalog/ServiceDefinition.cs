// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Domain.Features;

namespace Honua.Core.Domain.Catalog;

/// <summary>
/// Metadata definition for a GeoServices feature service
/// </summary>
/// <param name="Name">Service name (URL segment identifier)</param>
/// <param name="Description">Human-readable service description</param>
/// <param name="Layers">Layers available in this service</param>
/// <param name="SpatialReference">Default coordinate system for the service</param>
/// <param name="MaxRecordCount">Maximum number of features returned in a single query</param>
/// <param name="SupportedFormats">Query response formats supported by the service</param>
/// <param name="Capabilities">Operations supported by the service</param>
/// <param name="ServiceExtent">Overall spatial extent of all service data</param>
public record ServiceDefinition(
    string Name,
    string Description,
    LayerDefinition[] Layers,
    SpatialReference SpatialReference,
    int MaxRecordCount = 1000,
    string[] SupportedFormats = default!,
    string[] Capabilities = default!,
    FeatureExtent? ServiceExtent = null)
{
    /// <summary>
    /// Default supported formats for GeoServices REST API
    /// </summary>
    public string[] SupportedFormats { get; init; } = SupportedFormats ?? ["JSON", "GeoJSON"];

    /// <summary>
    /// Default capabilities for GeoServices feature service
    /// </summary>
    public string[] Capabilities { get; init; } = Capabilities ?? ["Query", "Extract"];

    /// <summary>
    /// All unique field definitions across all layers in the service
    /// </summary>
    public FieldDefinition[] AllFields => Layers
        .SelectMany(layer => layer.Fields)
        .DistinctBy(f => f.Name)
        .OrderBy(f => f.Name)
        .ToArray();

    /// <summary>
    /// All unique geometry types present in service layers
    /// </summary>
    public GeometryType[] GeometryTypes => Layers
        .Select(layer => layer.GeometryType)
        .Where(type => type != GeometryType.None)
        .Distinct()
        .OrderBy(type => type.ToString())
        .ToArray();

    /// <summary>
    /// Whether the service supports editing operations
    /// </summary>
    public bool SupportsEditing => Capabilities.Contains("Create") ||
                                   Capabilities.Contains("Update") ||
                                   Capabilities.Contains("Delete");

    /// <summary>
    /// Whether the service supports advanced querying
    /// </summary>
    public bool SupportsAdvancedQueries => Capabilities.Contains("Query");

    /// <summary>
    /// Combined extent of all layers (computed from layer extents)
    /// </summary>
    public FeatureExtent? ComputedExtent
    {
        get
        {
            var layerExtents = Layers
                .Select(layer => layer.Extent)
                .Where(extent => extent != null)
                .ToArray();

            if (layerExtents.Length == 0)
                return null;

            // Compute union of all layer extents manually
            var allExtents = layerExtents.Cast<FeatureExtent>().ToArray();
            if (allExtents.Length == 1)
                return allExtents[0];

            var minX = allExtents.Min(e => e.MinX);
            var minY = allExtents.Min(e => e.MinY);
            var maxX = allExtents.Max(e => e.MaxX);
            var maxY = allExtents.Max(e => e.MaxY);
            var srid = allExtents[0].SpatialReference; // Use first extent's SRID

            return FeatureExtent.Create(minX, minY, maxX, maxY, srid);
        }
    }

    /// <summary>
    /// Gets the overall extent, preferring explicitly set ServiceExtent over computed
    /// </summary>
    public FeatureExtent? EffectiveExtent => ServiceExtent ?? ComputedExtent;

    /// <summary>
    /// Finds a layer by its ID
    /// </summary>
    /// <param name="layerId">Layer identifier</param>
    /// <returns>Layer definition if found, null otherwise</returns>
    public LayerDefinition? GetLayer(int layerId) =>
        Layers.FirstOrDefault(layer => layer.Id == layerId);

    /// <summary>
    /// Finds a layer by its name (case-insensitive)
    /// </summary>
    /// <param name="layerName">Layer name</param>
    /// <returns>Layer definition if found, null otherwise</returns>
    public LayerDefinition? GetLayerByName(string layerName) =>
        Layers.FirstOrDefault(layer =>
            layer.Name.Equals(layerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Validates the service definition for common issues
    /// </summary>
    /// <returns>Validation error messages, empty if valid</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("Service name cannot be empty");

        if (Name?.Length > 64)
            errors.Add("Service name cannot exceed 64 characters");

        if (MaxRecordCount <= 0)
            errors.Add("MaxRecordCount must be positive");

        if (MaxRecordCount > 10000)
            errors.Add("MaxRecordCount should not exceed 10000 for performance");

        if (Layers.Length == 0)
            errors.Add("Service must have at least one layer");

        // Check for duplicate layer IDs
        var duplicateIds = Layers
            .GroupBy(layer => layer.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var duplicateId in duplicateIds)
            errors.Add($"Duplicate layer ID: {duplicateId}");

        // Check for duplicate layer names (case-insensitive)
        var duplicateNames = Layers
            .GroupBy(layer => layer.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var duplicateName in duplicateNames)
            errors.Add($"Duplicate layer name: {duplicateName}");

        // Validate each layer
        foreach (var layer in Layers)
        {
            var layerErrors = layer.Validate();
            foreach (var error in layerErrors)
                errors.Add($"Layer {layer.Id} ({layer.Name}): {error}");
        }

        return errors;
    }

    /// <summary>
    /// Creates a basic service definition with a single layer
    /// </summary>
    /// <param name="serviceName">Service name</param>
    /// <param name="layer">Initial layer</param>
    /// <param name="spatialReference">Default coordinate system</param>
    /// <returns>Service definition</returns>
    public static ServiceDefinition CreateSingle(string serviceName, LayerDefinition layer, SpatialReference? spatialReference = null)
    {
        var srs = spatialReference ?? layer.SpatialReference;
        return new ServiceDefinition(
            serviceName,
            $"Feature service for {layer.Name}",
            [layer],
            srs);
    }
}
