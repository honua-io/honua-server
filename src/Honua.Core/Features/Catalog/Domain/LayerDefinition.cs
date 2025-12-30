// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Catalog.Domain;

/// <summary>
/// Metadata definition for a feature layer
/// </summary>
/// <param name="Id">Unique layer identifier</param>
/// <param name="Name">Display name for the layer</param>
/// <param name="Description">Optional description of the layer content</param>
/// <param name="GeometryType">Primary geometry type stored in this layer</param>
/// <param name="SpatialReference">Coordinate system for geometry data</param>
/// <param name="Fields">Field definitions for the layer's attribute schema</param>
/// <param name="Extent">Spatial bounds of the layer data (optional)</param>
/// <param name="MinScale">Minimum scale for layer visibility (optional)</param>
/// <param name="MaxScale">Maximum scale for layer visibility (optional)</param>
/// <param name="DefaultVisibility">Whether layer is visible by default</param>
/// <param name="Relationships">Relationships defined for this layer (optional)</param>
/// <param name="SupportsAttachments">Whether this layer supports file attachments (default true)</param>
public record LayerDefinition(
    int Id,
    string Name,
    string? Description,
    GeometryType GeometryType,
    SpatialReference SpatialReference,
    FieldDefinition[] Fields,
    FeatureExtent? Extent = null,
    double? MinScale = null,
    double? MaxScale = null,
    bool DefaultVisibility = true,
    Relationship[]? Relationships = null,
    bool SupportsAttachments = true)
{
    /// <summary>
    /// The geometry field definition (if layer has geometry)
    /// </summary>
    public FieldDefinition? GeometryField => Fields.FirstOrDefault(f => f.IsGeometry);

    /// <summary>
    /// Non-geometry attribute fields
    /// </summary>
    public FieldDefinition[] AttributeFields => Fields.Where(f => !f.IsGeometry).ToArray();

    /// <summary>
    /// Non-geometry attribute fields as ReadOnlySpan for efficient enumeration
    /// </summary>
    [JsonIgnore]
    public ReadOnlySpan<FieldDefinition> AttributeFieldsSpan =>
        AttributeFieldsMemory.Span;

    /// <summary>
    /// Non-geometry attribute fields as Memory for efficient slicing and sharing
    /// </summary>
    public Memory<FieldDefinition> AttributeFieldsMemory
    {
        get
        {
            // Cache the result to avoid repeated allocations during multiple accesses
            if (_attributeFieldsCache == null)
            {
                var attributeFields = Fields.Where(f => !f.IsGeometry).ToArray();
                _attributeFieldsCache = new Memory<FieldDefinition>(attributeFields);
            }
            return _attributeFieldsCache.Value;
        }
    }

    private Memory<FieldDefinition>? _attributeFieldsCache;

    /// <summary>
    /// Whether this layer contains geometry data
    /// </summary>
    public bool HasGeometry => GeometryType != GeometryType.None;

    /// <summary>
    /// Primary key field (typically the first integer field named 'id' or 'objectid')
    /// </summary>
    public FieldDefinition? PrimaryKeyField => Fields.FirstOrDefault(f =>
        f.Name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
        f.Name.Equals("objectid", StringComparison.OrdinalIgnoreCase) ||
        f.Name.Equals("fid", StringComparison.OrdinalIgnoreCase)) ??
        Fields.FirstOrDefault(f => f.Type is FieldType.Integer or FieldType.BigInteger);

    /// <summary>
    /// Relationships defined for this layer (empty array if none)
    /// </summary>
    public Relationship[] LayerRelationships => Relationships ?? Array.Empty<Relationship>();

    /// <summary>
    /// Whether this layer has any relationships defined
    /// </summary>
    public bool HasRelationships => Relationships?.Length > 0;

    /// <summary>
    /// Validates the layer definition for common issues
    /// </summary>
    /// <returns>Validation error messages, empty if valid</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Id <= 0)
            errors.Add("Layer ID must be positive");

        if (string.IsNullOrWhiteSpace(Name))
            errors.Add("Layer name cannot be empty");

        if (Name?.Length > 128)
            errors.Add("Layer name cannot exceed 128 characters");

        if (Fields.Length == 0)
            errors.Add("Layer must have at least one field");

        if (HasGeometry && GeometryField == null)
            errors.Add($"Layer has geometry type {GeometryType} but no geometry field defined");

        if (!HasGeometry && GeometryField != null)
            errors.Add("Layer has geometry field but geometry type is None");

        if (PrimaryKeyField == null)
            errors.Add("Layer must have a primary key field (integer field named 'id', 'objectid', or 'fid')");

        // Validate each field
        foreach (var field in Fields)
        {
            var fieldError = field.Validate();
            if (fieldError != null)
                errors.Add($"Field '{field.Name}': {fieldError}");
        }

        // Check for duplicate field names (case-insensitive)
        var duplicateFields = Fields
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);

        foreach (var duplicateName in duplicateFields)
            errors.Add($"Duplicate field name: {duplicateName}");

        return errors;
    }

    /// <summary>
    /// Creates a basic layer definition with minimal required fields
    /// </summary>
    /// <param name="id">Layer identifier</param>
    /// <param name="name">Layer name</param>
    /// <param name="geometryType">Geometry type</param>
    /// <param name="spatialReference">Coordinate system</param>
    /// <param name="supportsAttachments">Whether this layer supports file attachments</param>
    /// <returns>Layer definition with standard ID and geometry fields</returns>
    public static LayerDefinition CreateBasic(int id, string name, GeometryType geometryType, SpatialReference? spatialReference = null, bool supportsAttachments = true)
    {
        var srs = spatialReference ?? SpatialReference.WGS84;
        var fields = new List<FieldDefinition>
        {
            new("objectid", FieldType.Integer, Nullable: false, Description: "Unique object identifier")
        };

        if (geometryType != GeometryType.None)
        {
            fields.Add(new("shape", FieldType.Geometry, Nullable: false, Description: "Geometry field"));
        }

        return new LayerDefinition(
            id,
            name,
            Description: null,
            geometryType,
            srs,
            fields.ToArray(),
            SupportsAttachments: supportsAttachments);
    }
}
