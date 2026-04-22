// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Global server capabilities shared across all services and protocols.
/// Contains server-wide configuration, supported operations, and system limitations.
/// </summary>
public sealed record GlobalCapabilities
{
    /// <summary>
    /// Server identity and version information.
    /// </summary>
    public ServerIdentity Server { get; init; } = new();

    /// <summary>
    /// Supported protocols and their capabilities.
    /// </summary>
    public ProtocolCapabilities Protocols { get; init; } = new();

    /// <summary>
    /// Global spatial capabilities.
    /// </summary>
    public GlobalSpatialCapabilities Spatial { get; init; } = new();

    /// <summary>
    /// Global format support.
    /// </summary>
    public GlobalFormatCapabilities Formats { get; init; } = new();

    /// <summary>
    /// Global query capabilities.
    /// </summary>
    public GlobalQueryCapabilities Query { get; init; } = new();

    /// <summary>
    /// Global limits and constraints.
    /// </summary>
    public GlobalLimits Limits { get; init; } = new();

    /// <summary>
    /// Security and authentication capabilities.
    /// </summary>
    public SecurityCapabilities Security { get; init; } = new();

    /// <summary>
    /// Caching and performance capabilities.
    /// </summary>
    public PerformanceCapabilities Performance { get; init; } = new();

    /// <summary>
    /// Extension capabilities.
    /// </summary>
    public ExtensionCapabilities Extensions { get; init; } = new();

    /// <summary>
    /// When capabilities were last computed.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Server configuration version.
    /// </summary>
    public string Version { get; init; } = "1.0.0";
}

/// <summary>
/// Server identity and version information.
/// </summary>
public sealed record ServerIdentity
{
    /// <summary>
    /// Server name.
    /// </summary>
    public string Name { get; init; } = "Honua Server";

    /// <summary>
    /// Server title.
    /// </summary>
    public string Title { get; init; } = "Honua Geospatial Server";

    /// <summary>
    /// Server description.
    /// </summary>
    public string Description { get; init; } = "Open geospatial data server";

    /// <summary>
    /// Server version.
    /// </summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// Contact information for the server administrator.
    /// </summary>
    public ContactInfo? Contact { get; init; }

    /// <summary>
    /// Server provider information.
    /// </summary>
    public ProviderInfo? Provider { get; init; }

    /// <summary>
    /// Server URL for external references.
    /// </summary>
    public string? ServerUrl { get; init; }

    /// <summary>
    /// License information for the server.
    /// </summary>
    public string? License { get; init; }

    /// <summary>
    /// Server keywords for discovery.
    /// </summary>
    public string[] Keywords { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Supported protocols and their capabilities.
/// </summary>
public sealed record ProtocolCapabilities
{
    /// <summary>
    /// OGC API Features capabilities.
    /// </summary>
    public OgcApiFeaturesCapabilities? OgcApiFeatures { get; init; }

    /// <summary>
    /// WFS 2.0 capabilities.
    /// </summary>
    public Wfs20Capabilities? Wfs20 { get; init; }

    /// <summary>
    /// GeoServices REST API capabilities.
    /// </summary>
    public GeoServicesCapabilities? GeoServices { get; init; }

    /// <summary>
    /// OData capabilities.
    /// </summary>
    public ODataCapabilities? OData { get; init; }

    /// <summary>
    /// gRPC capabilities.
    /// </summary>
    public GrpcCapabilities? Grpc { get; init; }

    /// <summary>
    /// STAC API capabilities.
    /// </summary>
    public StacCapabilities? Stac { get; init; }

    /// <summary>
    /// Additional protocol capabilities.
    /// </summary>
    public Dictionary<string, object> Additional { get; init; } = new();
}

/// <summary>
/// OGC API Features protocol capabilities.
/// </summary>
public sealed record OgcApiFeaturesCapabilities
{
    /// <summary>
    /// Supported OGC API Features conformance classes.
    /// </summary>
    public string[] ConformanceClasses { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported CQL2 capabilities.
    /// </summary>
    public Cql2Capabilities Cql2 { get; init; } = new();

    /// <summary>
    /// Supported coordinate reference systems.
    /// </summary>
    public string[] SupportedCrs { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether HTML output is supported.
    /// </summary>
    public bool SupportsHtml { get; init; } = true;
}

/// <summary>
/// CQL2 filter capabilities.
/// </summary>
public sealed record Cql2Capabilities
{
    /// <summary>
    /// Whether CQL2 text format is supported.
    /// </summary>
    public bool SupportsText { get; init; } = true;

    /// <summary>
    /// Whether CQL2 JSON format is supported.
    /// </summary>
    public bool SupportsJson { get; init; } = true;

    /// <summary>
    /// Supported spatial operators.
    /// </summary>
    public string[] SpatialOperators { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported temporal operators.
    /// </summary>
    public string[] TemporalOperators { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported functions.
    /// </summary>
    public string[] Functions { get; init; } = Array.Empty<string>();
}

/// <summary>
/// WFS 2.0 protocol capabilities.
/// </summary>
public sealed record Wfs20Capabilities
{
    /// <summary>
    /// Supported WFS operations.
    /// </summary>
    public string[] Operations { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported output formats.
    /// </summary>
    public string[] OutputFormats { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Filter capabilities.
    /// </summary>
    public WfsFilterCapabilities Filter { get; init; } = new();

    /// <summary>
    /// Whether transactions are supported.
    /// </summary>
    public bool SupportsTransactions { get; init; }

    /// <summary>
    /// Supported coordinate reference systems.
    /// </summary>
    public string[] SupportedCrs { get; init; } = Array.Empty<string>();
}

/// <summary>
/// WFS filter capabilities.
/// </summary>
public sealed record WfsFilterCapabilities
{
    /// <summary>
    /// Supported spatial operators.
    /// </summary>
    public string[] SpatialOperators { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported comparison operators.
    /// </summary>
    public string[] ComparisonOperators { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported logical operators.
    /// </summary>
    public string[] LogicalOperators { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported functions.
    /// </summary>
    public string[] Functions { get; init; } = Array.Empty<string>();
}

/// <summary>
/// GeoServices REST API capabilities.
/// </summary>
public sealed record GeoServicesCapabilities
{
    /// <summary>
    /// Supported operations.
    /// </summary>
    public string[] Operations { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported output formats.
    /// </summary>
    public string[] OutputFormats { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether editing is supported.
    /// </summary>
    public bool SupportsEditing { get; init; }

    /// <summary>
    /// Whether attachments are supported.
    /// </summary>
    public bool SupportsAttachments { get; init; }

    /// <summary>
    /// Whether sync is supported.
    /// </summary>
    public bool SupportsSync { get; init; }
}

/// <summary>
/// OData protocol capabilities.
/// </summary>
public sealed record ODataCapabilities
{
    /// <summary>
    /// Supported OData version.
    /// </summary>
    public string Version { get; init; } = "4.0";

    /// <summary>
    /// Whether batch operations are supported.
    /// </summary>
    public bool SupportsBatch { get; init; } = true;

    /// <summary>
    /// Whether change tracking is supported.
    /// </summary>
    public bool SupportsChangeTracking { get; init; }

    /// <summary>
    /// Supported query options.
    /// </summary>
    public string[] QueryOptions { get; init; } = Array.Empty<string>();
}

/// <summary>
/// gRPC protocol capabilities.
/// </summary>
public sealed record GrpcCapabilities
{
    /// <summary>
    /// Supported gRPC services.
    /// </summary>
    public string[] Services { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether streaming is supported.
    /// </summary>
    public bool SupportsStreaming { get; init; } = true;

    /// <summary>
    /// Whether compression is supported.
    /// </summary>
    public bool SupportsCompression { get; init; } = true;
}

/// <summary>
/// STAC API capabilities.
/// </summary>
public sealed record StacCapabilities
{
    /// <summary>
    /// Supported STAC API conformance classes.
    /// </summary>
    public string[] ConformanceClasses { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported STAC extensions.
    /// </summary>
    public string[] Extensions { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Global spatial capabilities across all protocols.
/// </summary>
public sealed record GlobalSpatialCapabilities
{
    /// <summary>
    /// Default coordinate reference system for the server.
    /// </summary>
    public SpatialReference DefaultCrs { get; init; } = SpatialReference.WGS84;

    /// <summary>
    /// All supported coordinate reference systems.
    /// </summary>
    public SpatialReference[] SupportedCrs { get; init; } = Array.Empty<SpatialReference>();

    /// <summary>
    /// Supported geometry types.
    /// </summary>
    public GeometryType[] SupportedGeometryTypes { get; init; } = Array.Empty<GeometryType>();

    /// <summary>
    /// Whether coordinate transformation is supported.
    /// </summary>
    public bool SupportsTransformation { get; init; } = true;

    /// <summary>
    /// Whether 3D geometries are supported.
    /// </summary>
    public bool Supports3D { get; init; }

    /// <summary>
    /// Whether measured geometries are supported.
    /// </summary>
    public bool SupportsM { get; init; }

    /// <summary>
    /// Supported spatial operations.
    /// </summary>
    public string[] SpatialOperations { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Default spatial reference authority.
    /// </summary>
    public string DefaultCrsAuthority { get; init; } = "EPSG";

    /// <summary>
    /// Maximum geometry complexity allowed.
    /// </summary>
    public int MaxGeometryComplexity { get; init; } = 10_000;
}

/// <summary>
/// Global format capabilities.
/// </summary>
public sealed record GlobalFormatCapabilities
{
    /// <summary>
    /// Supported vector output formats.
    /// </summary>
    public string[] VectorFormats { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported raster output formats.
    /// </summary>
    public string[] RasterFormats { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported metadata formats.
    /// </summary>
    public string[] MetadataFormats { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether custom formats can be added.
    /// </summary>
    public bool SupportsCustomFormats { get; init; }
}

/// <summary>
/// Global query capabilities.
/// </summary>
public sealed record GlobalQueryCapabilities
{
    /// <summary>
    /// Whether SQL queries are supported globally.
    /// </summary>
    public bool SupportsSql { get; init; } = true;

    /// <summary>
    /// Whether spatial queries are supported globally.
    /// </summary>
    public bool SupportsSpatial { get; init; } = true;

    /// <summary>
    /// Whether temporal queries are supported globally.
    /// </summary>
    public bool SupportsTemporal { get; init; }

    /// <summary>
    /// Whether full-text search is supported globally.
    /// </summary>
    public bool SupportsFullTextSearch { get; init; }

    /// <summary>
    /// Whether statistical queries are supported globally.
    /// </summary>
    public bool SupportsStatistics { get; init; } = true;

    /// <summary>
    /// Whether sorting is supported globally.
    /// </summary>
    public bool SupportsSorting { get; init; } = true;

    /// <summary>
    /// Whether pagination is supported globally.
    /// </summary>
    public bool SupportsPagination { get; init; } = true;
}

/// <summary>
/// Global server limits and constraints.
/// </summary>
public sealed record GlobalLimits
{
    /// <summary>
    /// Maximum number of features that can be returned in a single request.
    /// </summary>
    public int MaxFeatureCount { get; init; } = 10_000;

    /// <summary>
    /// Default number of features returned when not specified.
    /// </summary>
    public int DefaultFeatureCount { get; init; } = 1_000;

    /// <summary>
    /// Maximum request timeout.
    /// </summary>
    public TimeSpan MaxTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum upload size for data.
    /// </summary>
    public long MaxUploadSize { get; init; } = 100 * 1024 * 1024; // 100 MB

    /// <summary>
    /// Maximum concurrent requests per client.
    /// </summary>
    public int MaxConcurrentRequests { get; init; } = 10;

    /// <summary>
    /// Rate limiting configuration.
    /// </summary>
    public RateLimits RateLimit { get; init; } = new();
}

/// <summary>
/// Rate limiting configuration.
/// </summary>
public sealed record RateLimits
{
    /// <summary>
    /// Maximum requests per minute.
    /// </summary>
    public int RequestsPerMinute { get; init; } = 1000;

    /// <summary>
    /// Maximum requests per hour.
    /// </summary>
    public int RequestsPerHour { get; init; } = 10_000;

    /// <summary>
    /// Whether rate limiting is enabled.
    /// </summary>
    public bool Enabled { get; init; }
}

/// <summary>
/// Security and authentication capabilities.
/// </summary>
public sealed record SecurityCapabilities
{
    /// <summary>
    /// Supported authentication methods.
    /// </summary>
    public string[] AuthenticationMethods { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether anonymous access is allowed by default.
    /// </summary>
    public bool AllowsAnonymousAccess { get; init; } = true;

    /// <summary>
    /// Whether role-based access control is supported.
    /// </summary>
    public bool SupportsRbac { get; init; } = true;

    /// <summary>
    /// Whether API key authentication is supported.
    /// </summary>
    public bool SupportsApiKeys { get; init; }

    /// <summary>
    /// Whether OAuth is supported.
    /// </summary>
    public bool SupportsOAuth { get; init; }

    /// <summary>
    /// Whether HTTPS is enforced.
    /// </summary>
    public bool RequiresHttps { get; init; }
}

/// <summary>
/// Performance and caching capabilities.
/// </summary>
public sealed record PerformanceCapabilities
{
    /// <summary>
    /// Whether response caching is enabled.
    /// </summary>
    public bool SupportsCaching { get; init; } = true;

    /// <summary>
    /// Whether ETags are supported.
    /// </summary>
    public bool SupportsETags { get; init; } = true;

    /// <summary>
    /// Whether compression is supported.
    /// </summary>
    public bool SupportsCompression { get; init; } = true;

    /// <summary>
    /// Default cache duration.
    /// </summary>
    public TimeSpan DefaultCacheDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether background processing is supported.
    /// </summary>
    public bool SupportsBackgroundProcessing { get; init; }
}

/// <summary>
/// Extension capabilities.
/// </summary>
public sealed record ExtensionCapabilities
{
    /// <summary>
    /// Loaded server extensions.
    /// </summary>
    public ServerExtension[] LoadedExtensions { get; init; } = Array.Empty<ServerExtension>();

    /// <summary>
    /// Whether custom extensions can be loaded.
    /// </summary>
    public bool SupportsCustomExtensions { get; init; }

    /// <summary>
    /// Extension API version.
    /// </summary>
    public string ExtensionApiVersion { get; init; } = "1.0.0";
}

/// <summary>
/// Server extension information.
/// </summary>
public sealed record ServerExtension
{
    /// <summary>
    /// Extension name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Extension version.
    /// </summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>
    /// Extension description.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Extension capabilities.
    /// </summary>
    public Dictionary<string, object> Capabilities { get; init; } = new();
}
