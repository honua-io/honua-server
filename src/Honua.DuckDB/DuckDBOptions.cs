// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.DuckDB;

/// <summary>
/// Configuration options for the DuckDB provider, bound from the "DuckDB" section.
/// </summary>
public sealed class DuckDBOptions
{
    /// <summary>Path to the DuckDB database file, or ":memory:" for in-memory mode.</summary>
    public string DatabasePath { get; set; } = ":memory:";

    /// <summary>Optional offline path for the spatial extension files.</summary>
    public string? SpatialExtensionPath { get; set; }

    /// <summary>Whether to open the database in read-only mode (recommended for production).</summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>Configured layer definitions.</summary>
    public DuckDBLayerOptions[] Layers { get; set; } = [];

    /// <summary>Configured service definitions.</summary>
    public DuckDBServiceOptions[] Services { get; set; } = [];
}

/// <summary>
/// Configuration for a single DuckDB-backed layer.
/// </summary>
public sealed class DuckDBLayerOptions
{
    /// <summary>Honua layer ID.</summary>
    public int Id { get; set; }

    /// <summary>Display name for the layer.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Description of the layer.</summary>
    public string? Description { get; set; }

    /// <summary>DuckDB table name containing the data.</summary>
    public string Table { get; set; } = string.Empty;

    /// <summary>Geometry column name in the table.</summary>
    public string GeometryColumn { get; set; } = "geom";

    /// <summary>Object ID / primary key column name.</summary>
    public string ObjectIdColumn { get; set; } = "id";

    /// <summary>SRID of the geometry data.</summary>
    public int Srid { get; set; } = 4326;

    /// <summary>Geometry type (Point, Polygon, etc.).</summary>
    public string GeometryType { get; set; } = "Point";
}

/// <summary>
/// Configuration for a DuckDB-backed service (a group of layers).
/// </summary>
public sealed class DuckDBServiceOptions
{
    /// <summary>Service name (used in URL paths).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Service description.</summary>
    public string? Description { get; set; }

    /// <summary>Layer IDs included in this service.</summary>
    public int[] LayerIds { get; set; } = [];

    /// <summary>Capabilities. Only "Query" and "Extract" are supported for DuckDB.</summary>
    public string[] Capabilities { get; set; } = ["Query", "Extract"];

    /// <summary>Enabled protocols (e.g. "FeatureServer", "OgcFeatures", "Grpc").</summary>
    public string[]? EnabledProtocols { get; set; }
}
