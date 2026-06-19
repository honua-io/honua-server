// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Databricks.Features.Infrastructure;

/// <summary>
/// Resolved physical mapping for a single Databricks-backed layer, built from
/// configuration at startup. Carries the fully qualified table reference and the
/// column names used to emit SQL against the SQL Warehouse.
/// </summary>
internal sealed class DatabricksLayerMapping
{
    /// <summary>Honua layer ID.</summary>
    public required int LayerId { get; init; }

    /// <summary>Table or view name.</summary>
    public required string Table { get; init; }

    /// <summary>Optional catalog qualifier (resolved layer-over-global).</summary>
    public string? Catalog { get; init; }

    /// <summary>Optional schema qualifier (resolved layer-over-global).</summary>
    public string? Schema { get; init; }

    /// <summary>Geometry column name.</summary>
    public required string GeometryColumn { get; init; }

    /// <summary>Primary-key column name.</summary>
    public required string PrimaryKeyColumn { get; init; }

    /// <summary>Stored geometry SRID.</summary>
    public required int Srid { get; init; }

    /// <summary>Declared geometry type.</summary>
    public required GeometryType GeometryType { get; init; }

    /// <summary>Attribute column names exposed by the layer.</summary>
    public required IReadOnlyList<string> AttributeColumns { get; init; }

    /// <summary>
    /// Builds the backtick-quoted, fully qualified table reference
    /// (<c>`catalog`.`schema`.`table`</c>) honoring any configured catalog/schema.
    /// </summary>
    public string QualifiedTable()
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(Catalog))
        {
            parts.Add(DatabricksIdentifier.Quote(Catalog));
        }

        if (!string.IsNullOrWhiteSpace(Schema))
        {
            parts.Add(DatabricksIdentifier.Quote(Schema));
        }

        parts.Add(DatabricksIdentifier.Quote(Table));
        return string.Join('.', parts);
    }
}

/// <summary>
/// Registry of configured Databricks layer mappings keyed by Honua layer id.
/// </summary>
internal sealed class DatabricksLayerMappingRegistry
{
    private readonly Dictionary<int, DatabricksLayerMapping> _mappings;

    /// <summary>
    /// Initializes the registry from the resolved layer mappings.
    /// </summary>
    /// <param name="mappings">Resolved layer mappings.</param>
    public DatabricksLayerMappingRegistry(IEnumerable<DatabricksLayerMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        _mappings = mappings.ToDictionary(static m => m.LayerId);
    }

    /// <summary>
    /// Resolves the mapping for <paramref name="layerId"/> or throws when no layer is configured.
    /// </summary>
    /// <param name="layerId">Honua layer id.</param>
    /// <returns>Resolved layer mapping.</returns>
    public DatabricksLayerMapping Resolve(int layerId)
        => _mappings.TryGetValue(layerId, out var mapping)
            ? mapping
            : throw new InvalidOperationException($"No Databricks layer mapping configured for layer {layerId}.");
}
