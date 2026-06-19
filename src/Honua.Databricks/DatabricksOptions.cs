// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Databricks;

/// <summary>
/// Configuration options for the read-only Databricks feature provider, bound from
/// the <c>Databricks</c> configuration section.
/// </summary>
/// <remarks>
/// <para>The provider is an HTTP read-through adapter over the Databricks SQL
/// Statement Execution REST API. There is no first-class .NET ADO.NET driver for
/// Databricks, so the provider submits SQL statements over HTTPS against a SQL
/// Warehouse and parses the JSON result chunks. The provider is best-effort and
/// read-only.</para>
/// <para>Because Databricks exposes no Honua-aware service-metadata endpoint, layer
/// mappings are supplied through configuration (catalog/schema/table, geometry and
/// primary-key columns, and the attribute column list) much like the MySQL/MariaDB
/// provider. Schema introspection is intentionally not performed in this slice.</para>
/// </remarks>
public sealed class DatabricksOptions
{
    /// <summary>
    /// Whether the provider is registered. Defaults to <see langword="true"/>; set to
    /// <see langword="false"/> to compile the provider in but skip its DI registration.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Workspace host URL, for example <c>https://dbc-abc123.cloud.databricks.com</c>.
    /// Must be an absolute HTTPS URL. Prefer environment- or secret-backed configuration.
    /// </summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// SQL Warehouse identifier that statements execute against. Found in the
    /// Databricks workspace under SQL Warehouses (the trailing path segment of the
    /// warehouse's HTTP path, for example <c>1234567890abcdef</c>).
    /// </summary>
    public string WarehouseId { get; set; } = string.Empty;

    /// <summary>
    /// Personal access token (PAT) / OAuth bearer token used to authenticate to the
    /// REST API. Prefer a secret reference; the token is sent only as an
    /// <c>Authorization: Bearer …</c> request header and never placed in a URL.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Unity Catalog catalog name used to qualify table references when a layer does
    /// not specify its own. Optional.
    /// </summary>
    public string? Catalog { get; set; }

    /// <summary>
    /// Schema (database) name used to qualify table references when a layer does not
    /// specify its own. Optional.
    /// </summary>
    public string? Schema { get; set; }

    /// <summary>
    /// Overall statement wait/poll budget in seconds. The provider submits a
    /// statement asynchronously and polls for completion until this budget elapses.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Delay in milliseconds between successive status polls while a statement is
    /// <c>PENDING</c> or <c>RUNNING</c>.
    /// </summary>
    public int PollIntervalMilliseconds { get; set; } = 750;

    /// <summary>Configured layer definitions.</summary>
    public DatabricksLayerOptions[] Layers { get; set; } = [];
}

/// <summary>
/// Configuration for a single Databricks-backed layer.
/// </summary>
public sealed class DatabricksLayerOptions
{
    /// <summary>Honua layer ID.</summary>
    public int Id { get; set; }

    /// <summary>Display name for the layer.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Description of the layer.</summary>
    public string? Description { get; set; }

    /// <summary>Databricks table or view name containing the data.</summary>
    public string Table { get; set; } = string.Empty;

    /// <summary>
    /// Optional catalog qualifier overriding <see cref="DatabricksOptions.Catalog"/>
    /// for this layer.
    /// </summary>
    public string? Catalog { get; set; }

    /// <summary>
    /// Optional schema qualifier overriding <see cref="DatabricksOptions.Schema"/>
    /// for this layer.
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
    /// Explicit attribute column names to expose. Required — schema introspection is
    /// not performed in this slice.
    /// </summary>
    public string[] Attributes { get; set; } = [];
}
