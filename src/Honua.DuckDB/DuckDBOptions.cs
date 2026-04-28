// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

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

    /// <summary>
    /// Optional configuration for DuckDB's httpfs extension and S3-compatible object stores.
    /// </summary>
    public DuckDBHttpFsOptions HttpFs { get; set; } = new();

    /// <summary>
    /// Optional configuration for DuckDB's Azure extension.
    /// </summary>
    public DuckDBAzureOptions Azure { get; set; } = new();

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

    /// <summary>
    /// Optional external file source used to create a connection-scoped temporary view named by <see cref="Table"/>.
    /// </summary>
    public DuckDBExternalSourceOptions? ExternalSource { get; set; }

    /// <summary>Geometry column name in the table.</summary>
    public string GeometryColumn { get; set; } = "geom";

    /// <summary>Object ID / primary key column name.</summary>
    public string ObjectIdColumn { get; set; } = "id";

    /// <summary>SRID of the geometry data.</summary>
    public int Srid { get; set; } = SpatialConstants.DefaultSrid;

    /// <summary>Geometry type (Point, Polygon, etc.).</summary>
    public string GeometryType { get; set; } = "Point";

    /// <summary>
    /// Optional explicit attribute column names to expose.
    /// When null, columns are discovered from the DuckDB schema at startup.
    /// </summary>
    public string[]? Attributes { get; set; }
}

/// <summary>
/// External DuckDB source definition for a layer backed by Parquet or GeoParquet files.
/// </summary>
public sealed class DuckDBExternalSourceOptions
{
    /// <summary>
    /// Source format. Supported values are "Parquet" and "GeoParquet".
    /// </summary>
    public string Format { get; set; } = "GeoParquet";

    /// <summary>
    /// Single file, glob, or object-store URI to scan.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Multiple files, globs, or object-store URIs to scan as one source.
    /// </summary>
    public string[]? Paths { get; set; }

    /// <summary>
    /// Whether DuckDB should expose Hive partition columns while reading the source.
    /// </summary>
    public bool HivePartitioning { get; set; }

    /// <summary>
    /// Whether DuckDB should union columns by name across multiple Parquet files.
    /// </summary>
    public bool UnionByName { get; set; }
}

/// <summary>
/// Configuration for DuckDB httpfs and S3-compatible object-store access.
/// </summary>
public sealed class DuckDBHttpFsOptions
{
    /// <summary>
    /// S3-compatible credential and endpoint settings applied to each DuckDB connection.
    /// </summary>
    public DuckDBS3Options S3 { get; set; } = new();
}

/// <summary>
/// S3-compatible object-store settings for DuckDB httpfs.
/// </summary>
public sealed class DuckDBS3Options
{
    /// <summary>Bucket region, for example "us-east-1".</summary>
    public string? Region { get; set; }

    /// <summary>Custom endpoint for MinIO, R2, lakeFS, or other S3-compatible services.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Access key ID. Prefer environment or secret-backed configuration in production.</summary>
    public string? AccessKeyId { get; set; }

    /// <summary>Secret access key. Prefer environment or secret-backed configuration in production.</summary>
    public string? SecretAccessKey { get; set; }

    /// <summary>Optional temporary session token.</summary>
    public string? SessionToken { get; set; }

    /// <summary>S3 URL style. Supported values are "vhost" and "path".</summary>
    public string? UrlStyle { get; set; }

    /// <summary>Whether DuckDB should use SSL for S3-compatible requests.</summary>
    public bool? UseSsl { get; set; }

    /// <summary>Whether DuckDB should enable requester-pays requests.</summary>
    public bool? RequesterPays { get; set; }
}

/// <summary>
/// Azure Blob Storage and ADLS settings for DuckDB's Azure extension.
/// </summary>
public sealed class DuckDBAzureOptions
{
    /// <summary>Azure storage connection string.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Azure storage account name used with credential-chain authentication.</summary>
    public string? AccountName { get; set; }

    /// <summary>Optional Azure endpoint override.</summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Ordered Azure credential chain, for example "cli;managed_identity;env".
    /// </summary>
    public string? CredentialChain { get; set; }
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

    /// <summary>
    /// Capabilities. Only "Query" is supported by the DuckDB provider in V1; any
    /// "Create", "Update", "Delete", or "Extract" entries are stripped at startup
    /// because the provider is read-only and has no replica persistence path.
    /// </summary>
    public string[] Capabilities { get; set; } = ["Query"];

    /// <summary>Enabled protocols (e.g. "FeatureServer", "OgcFeatures", "Grpc").</summary>
    public string[]? EnabledProtocols { get; set; }
}
