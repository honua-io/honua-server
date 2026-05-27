// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Metadata definition for a GeoServices feature service
/// </summary>
/// <param name="Name">Service name (URL segment identifier)</param>
/// <param name="Description">Human-readable service description</param>
/// <param name="Layers">Layers available in this service</param>
/// <param name="SpatialReference">Default coordinate system for the service</param>
/// <param name="SupportedFormats">Query response formats supported by the service</param>
/// <param name="Capabilities">Operations supported by the service</param>
/// <param name="ServiceExtent">Overall spatial extent of all service data</param>
/// <param name="ConnectionId">Optional secure connection identifier for this service</param>
public record ServiceDefinition(
    string Name,
    string Description,
    LayerDefinition[] Layers,
    SpatialReference SpatialReference,
    string[] SupportedFormats = default!,
    string[] Capabilities = default!,
    FeatureExtent? ServiceExtent = null,
    Guid? ConnectionId = null)
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
    public FieldDefinition[] AllFields => _allFields ??= Layers
        .SelectMany(layer => layer.Fields)
        .DistinctBy(f => f.Name)
        .OrderBy(f => f.Name)
        .ToArray();

    /// <summary>
    /// All unique field definitions as ReadOnlySpan for efficient enumeration
    /// </summary>
    [JsonIgnore]
    public ReadOnlySpan<FieldDefinition> AllFieldsSpan => AllFields;

    /// <summary>
    /// All unique field definitions as Memory for efficient slicing and sharing
    /// </summary>
    public Memory<FieldDefinition> AllFieldsMemory => AllFields;

    private FieldDefinition[]? _allFields;

    /// <summary>
    /// All unique geometry types present in service layers
    /// </summary>
    public GeometryType[] GeometryTypes => _geometryTypes ??= Layers
        .Select(layer => layer.GeometryType)
        .Where(type => type != GeometryType.None)
        .Distinct()
        .OrderBy(type => type.ToString())
        .ToArray();

    /// <summary>
    /// All unique geometry types as ReadOnlySpan for efficient enumeration
    /// </summary>
    [JsonIgnore]
    public ReadOnlySpan<GeometryType> GeometryTypesSpan => GeometryTypes;

    /// <summary>
    /// All unique geometry types as Memory for efficient slicing and sharing
    /// </summary>
    public Memory<GeometryType> GeometryTypesMemory => GeometryTypes;

    private GeometryType[]? _geometryTypes;

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
            if (_computedExtentCached)
                return _computedExtent;

            var layerExtents = Layers
                .Select(layer => layer.Extent)
                .Where(extent => extent != null)
                .ToArray();

            if (layerExtents.Length == 0)
            {
                _computedExtent = null;
            }
            else
            {
                var allExtents = layerExtents.Cast<FeatureExtent>().ToArray();
                _computedExtent = allExtents.Length == 1
                    ? allExtents[0]
                    : FeatureExtent.Create(
                        allExtents.Min(e => e.MinX),
                        allExtents.Min(e => e.MinY),
                        allExtents.Max(e => e.MaxX),
                        allExtents.Max(e => e.MaxY),
                        allExtents[0].SpatialReference);
            }

            _computedExtentCached = true;
            return _computedExtent;
        }
    }

    private FeatureExtent? _computedExtent;
    private bool _computedExtentCached;

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
