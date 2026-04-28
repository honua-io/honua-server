// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.MySql;

/// <summary>
/// Configuration options for the MySQL/MariaDB read-only provider, bound from the "MySql" section.
/// </summary>
public sealed class MySqlOptions
{
    /// <summary>MySQL/MariaDB connection string. Prefer environment- or secret-backed configuration.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>Configured layer definitions.</summary>
    public MySqlLayerOptions[] Layers { get; set; } = [];

    /// <summary>Configured service definitions.</summary>
    public MySqlServiceOptions[] Services { get; set; } = [];
}

/// <summary>
/// Configuration for a single MySQL/MariaDB-backed layer.
/// </summary>
public sealed class MySqlLayerOptions
{
    /// <summary>Honua layer ID.</summary>
    public int Id { get; set; }

    /// <summary>Display name for the layer.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Description of the layer.</summary>
    public string? Description { get; set; }

    /// <summary>MySQL/MariaDB table or view name containing the data.</summary>
    public string Table { get; set; } = string.Empty;

    /// <summary>
    /// Optional schema/database qualifier. MySQL treats schemas as databases; this is the
    /// qualifier emitted in <c>`db`.`table`</c> references.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>Geometry column name in the table.</summary>
    public string GeometryColumn { get; set; } = "geom";

    /// <summary>Primary key column name.</summary>
    public string PrimaryKeyColumn { get; set; } = "id";

    /// <summary>SRID of the stored geometry data.</summary>
    public int Srid { get; set; } = SpatialConstants.DefaultSrid;

    /// <summary>Geometry type (Point, Polygon, etc.).</summary>
    public string GeometryType { get; set; } = "Point";

    /// <summary>
    /// Optional explicit attribute column names to expose. Required for MySQL/MariaDB —
    /// schema introspection is not performed in this slice.
    /// </summary>
    public string[] Attributes { get; set; } = [];

    /// <summary>Optional attribute column type metadata, mapping column name to MySQL type.</summary>
    public Dictionary<string, string> AttributeTypes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Configuration for a MySQL/MariaDB-backed service (a group of layers).
/// </summary>
public sealed class MySqlServiceOptions
{
    /// <summary>Service name (used in URL paths).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Service description.</summary>
    public string? Description { get; set; }

    /// <summary>Layer IDs included in this service.</summary>
    public int[] LayerIds { get; set; } = [];

    /// <summary>
    /// Capabilities. Only "Query" is supported by the MySQL/MariaDB provider in this slice; any
    /// "Create", "Update", "Delete", or "Extract" entries are stripped at startup.
    /// </summary>
    public string[] Capabilities { get; set; } = ["Query"];

    /// <summary>Enabled protocols (e.g. "FeatureServer", "OgcFeatures", "Grpc").</summary>
    public string[]? EnabledProtocols { get; set; }
}
