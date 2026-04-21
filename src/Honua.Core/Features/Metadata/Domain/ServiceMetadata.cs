// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Unified metadata for a geospatial service, containing all information needed by protocol formatters.
/// Consolidates service definition, computed metadata, and protocol-specific information.
/// </summary>
public sealed record ServiceMetadata
{
    /// <summary>
    /// Core service definition from the catalog.
    /// </summary>
    public ServiceDefinition Definition { get; init; } = null!;

    /// <summary>
    /// Service identity and descriptive information.
    /// </summary>
    public ServiceIdentity Identity { get; init; } = null!;

    /// <summary>
    /// Layers available in this service with their computed metadata.
    /// </summary>
    public LayerMetadata[] Layers { get; init; } = Array.Empty<LayerMetadata>();

    /// <summary>
    /// Supported operations and capabilities for this service.
    /// </summary>
    public ServiceCapabilities Capabilities { get; init; } = new();

    /// <summary>
    /// Spatial extent information for the service.
    /// </summary>
    public ServiceSpatialInfo SpatialInfo { get; init; } = new();

    /// <summary>
    /// Temporal information for time-aware services.
    /// </summary>
    public ServiceTemporalInfo? TemporalInfo { get; init; }

    /// <summary>
    /// Security and access control information.
    /// </summary>
    public AccessControlInfo AccessControl { get; init; } = new();

    /// <summary>
    /// Protocol-specific links and references.
    /// </summary>
    public ServiceLinks Links { get; init; } = new();

    /// <summary>
    /// Additional metadata for specific protocols.
    /// </summary>
    public Dictionary<string, object> ProtocolExtensions { get; init; } = new();

    /// <summary>
    /// When this metadata was generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Version or sequence number for caching and change detection.
    /// </summary>
    public string Version { get; init; } = string.Empty;
}

/// <summary>
/// Service identity and descriptive information.
/// </summary>
public sealed record ServiceIdentity
{
    /// <summary>
    /// Service name (URL segment identifier).
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable service title.
    /// </summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>
    /// Service description.
    /// </summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Keywords for service discovery.
    /// </summary>
    public string[] Keywords { get; init; } = Array.Empty<string>();

    /// <summary>
    /// License identifier or description.
    /// </summary>
    public string? License { get; init; }

    /// <summary>
    /// Contact information for the service provider.
    /// </summary>
    public ContactInfo? Contact { get; init; }

    /// <summary>
    /// Service provider information.
    /// </summary>
    public ProviderInfo? Provider { get; init; }
}

/// <summary>
/// Contact information for service metadata.
/// </summary>
public sealed record ContactInfo
{
    /// <summary>
    /// Contact person name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Contact organization.
    /// </summary>
    public string? Organization { get; init; }

    /// <summary>
    /// Contact email address.
    /// </summary>
    public string? Email { get; init; }

    /// <summary>
    /// Contact website.
    /// </summary>
    public string? Website { get; init; }

    /// <summary>
    /// Contact phone number.
    /// </summary>
    public string? Phone { get; init; }
}

/// <summary>
/// Service provider information.
/// </summary>
public sealed record ProviderInfo
{
    /// <summary>
    /// Provider name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Provider website.
    /// </summary>
    public string? Website { get; init; }

    /// <summary>
    /// Provider contact information.
    /// </summary>
    public ContactInfo? Contact { get; init; }
}

/// <summary>
/// Service capabilities and supported operations.
/// </summary>
public sealed record ServiceCapabilities
{
    /// <summary>
    /// Supported query operations.
    /// </summary>
    public QueryCapabilities Query { get; init; } = new();

    /// <summary>
    /// Supported editing operations (null if editing not supported).
    /// </summary>
    public EditCapabilities? Edit { get; init; }

    /// <summary>
    /// Supported output formats.
    /// </summary>
    public FormatCapabilities Formats { get; init; } = new();

    /// <summary>
    /// Spatial operations and coordinate system support.
    /// </summary>
    public SpatialCapabilities Spatial { get; init; } = new();

    /// <summary>
    /// Protocol-specific operation lists.
    /// </summary>
    public Dictionary<string, string[]> ProtocolOperations { get; init; } = new();

    /// <summary>
    /// Service-wide limitations and constraints.
    /// </summary>
    public ServiceLimits Limits { get; init; } = new();
}

/// <summary>
/// Query capabilities for the service.
/// </summary>
public sealed record QueryCapabilities
{
    /// <summary>
    /// Whether the service supports advanced queries.
    /// </summary>
    public bool SupportsAdvancedQueries { get; init; } = true;

    /// <summary>
    /// Whether the service supports statistical queries.
    /// </summary>
    public bool SupportsStatistics { get; init; } = true;

    /// <summary>
    /// Whether the service supports pagination.
    /// </summary>
    public bool SupportsPagination { get; init; } = true;

    /// <summary>
    /// Whether the service supports sorting.
    /// </summary>
    public bool SupportsSorting { get; init; } = true;

    /// <summary>
    /// Whether the service supports distinct queries.
    /// </summary>
    public bool SupportsDistinct { get; init; } = true;

    /// <summary>
    /// Supported spatial relationships for geometry queries.
    /// </summary>
    public string[] SupportedSpatialRelations { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported filter capabilities.
    /// </summary>
    public FilterCapabilities Filter { get; init; } = new();
}

/// <summary>
/// Filter capabilities for query operations.
/// </summary>
public sealed record FilterCapabilities
{
    /// <summary>
    /// Whether WHERE clauses are supported.
    /// </summary>
    public bool SupportsWhere { get; init; } = true;

    /// <summary>
    /// Whether geometry filters are supported.
    /// </summary>
    public bool SupportsGeometry { get; init; } = true;

    /// <summary>
    /// Whether temporal filters are supported.
    /// </summary>
    public bool SupportsTemporal { get; init; } = false;

    /// <summary>
    /// Supported operators for attribute queries.
    /// </summary>
    public string[] SupportedOperators { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported functions in filter expressions.
    /// </summary>
    public string[] SupportedFunctions { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Edit capabilities for the service.
/// </summary>
public sealed record EditCapabilities
{
    /// <summary>
    /// Whether creating new features is supported.
    /// </summary>
    public bool SupportsCreate { get; init; } = false;

    /// <summary>
    /// Whether updating existing features is supported.
    /// </summary>
    public bool SupportsUpdate { get; init; } = false;

    /// <summary>
    /// Whether deleting features is supported.
    /// </summary>
    public bool SupportsDelete { get; init; } = false;

    /// <summary>
    /// Whether batch operations are supported.
    /// </summary>
    public bool SupportsBatchOperations { get; init; } = false;

    /// <summary>
    /// Whether transactions (rollback) are supported.
    /// </summary>
    public bool SupportsTransactions { get; init; } = false;

    /// <summary>
    /// Whether attachment operations are supported.
    /// </summary>
    public bool SupportsAttachments { get; init; } = false;
}

/// <summary>
/// Format capabilities for input/output.
/// </summary>
public sealed record FormatCapabilities
{
    /// <summary>
    /// Supported query output formats.
    /// </summary>
    public string[] QueryFormats { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported input formats for editing.
    /// </summary>
    public string[] InputFormats { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Supported image formats for map rendering.
    /// </summary>
    public string[] ImageFormats { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether geometry precision can be controlled.
    /// </summary>
    public bool SupportsGeometryPrecision { get; init; } = false;
}

/// <summary>
/// Spatial capabilities and coordinate system support.
/// </summary>
public sealed record SpatialCapabilities
{
    /// <summary>
    /// Default spatial reference system for the service.
    /// </summary>
    public SpatialReference DefaultSrs { get; init; } = SpatialReference.WGS84;

    /// <summary>
    /// All supported coordinate reference systems.
    /// </summary>
    public SpatialReference[] SupportedSrs { get; init; } = Array.Empty<SpatialReference>();

    /// <summary>
    /// Whether coordinate transformation is supported.
    /// </summary>
    public bool SupportsTransformation { get; init; } = true;

    /// <summary>
    /// Supported geometry types across all layers.
    /// </summary>
    public GeometryType[] SupportedGeometryTypes { get; init; } = Array.Empty<GeometryType>();

    /// <summary>
    /// Whether 3D geometry is supported.
    /// </summary>
    public bool Supports3D { get; init; } = false;

    /// <summary>
    /// Whether measured geometry is supported.
    /// </summary>
    public bool SupportsM { get; init; } = false;
}

/// <summary>
/// Service-wide limits and constraints.
/// </summary>
public sealed record ServiceLimits
{
    /// <summary>
    /// Maximum number of features returned in a single query.
    /// </summary>
    public int MaxRecordCount { get; init; } = 1000;

    /// <summary>
    /// Default number of features returned when not specified.
    /// </summary>
    public int DefaultRecordCount { get; init; } = 100;

    /// <summary>
    /// Maximum timeout for expensive operations.
    /// </summary>
    public TimeSpan MaxTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum size for uploaded data.
    /// </summary>
    public long MaxUploadSize { get; init; } = 50 * 1024 * 1024; // 50 MB

    /// <summary>
    /// Maximum complexity for geometry operations.
    /// </summary>
    public int MaxGeometryComplexity { get; init; } = 10_000;
}

/// <summary>
/// Service spatial information including extents.
/// </summary>
public sealed record ServiceSpatialInfo
{
    /// <summary>
    /// Overall spatial extent of the service data.
    /// </summary>
    public FeatureExtent? Extent { get; init; }

    /// <summary>
    /// Spatial reference of the extent.
    /// </summary>
    public SpatialReference? ExtentSrs { get; init; }

    /// <summary>
    /// Individual layer extents.
    /// </summary>
    public Dictionary<int, FeatureExtent> LayerExtents { get; init; } = new();

    /// <summary>
    /// Whether extent information is computed or cached.
    /// </summary>
    public bool ExtentIsComputed { get; init; } = false;
}

/// <summary>
/// Service temporal information for time-aware services.
/// </summary>
public sealed record ServiceTemporalInfo
{
    /// <summary>
    /// Whether any layers have temporal data.
    /// </summary>
    public bool HasTemporalData { get; init; } = false;

    /// <summary>
    /// Overall time extent of all temporal layers.
    /// </summary>
    public DateTimeOffset? TimeExtentStart { get; init; }

    /// <summary>
    /// End of overall time extent.
    /// </summary>
    public DateTimeOffset? TimeExtentEnd { get; init; }

    /// <summary>
    /// Layers with temporal information.
    /// </summary>
    public Dictionary<int, LayerTimeInfo> LayerTimeInfo { get; init; } = new();

    /// <summary>
    /// Supported temporal query capabilities.
    /// </summary>
    public TemporalCapabilities Capabilities { get; init; } = new();
}

/// <summary>
/// Temporal capabilities for time-aware queries.
/// </summary>
public sealed record TemporalCapabilities
{
    /// <summary>
    /// Whether point-in-time queries are supported.
    /// </summary>
    public bool SupportsTimeInstant { get; init; } = false;

    /// <summary>
    /// Whether time range queries are supported.
    /// </summary>
    public bool SupportsTimeRange { get; init; } = false;

    /// <summary>
    /// Supported temporal relationships.
    /// </summary>
    public string[] SupportedTimeRelations { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Access control information for the service.
/// </summary>
public sealed record AccessControlInfo
{
    /// <summary>
    /// Whether the service requires authentication.
    /// </summary>
    public bool RequiresAuthentication { get; init; } = false;

    /// <summary>
    /// Whether anonymous access is allowed.
    /// </summary>
    public bool AllowsAnonymousAccess { get; init; } = true;

    /// <summary>
    /// Required roles for service access.
    /// </summary>
    public string[] RequiredRoles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Required roles for write access.
    /// </summary>
    public string[] RequiredWriteRoles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Layers with access restrictions.
    /// </summary>
    public Dictionary<int, LayerAccessInfo> LayerAccessInfo { get; init; } = new();
}

/// <summary>
/// Access control information for individual layers.
/// </summary>
public sealed record LayerAccessInfo
{
    /// <summary>
    /// Whether the layer requires authentication.
    /// </summary>
    public bool RequiresAuthentication { get; init; } = false;

    /// <summary>
    /// Required roles for layer access.
    /// </summary>
    public string[] RequiredRoles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Required roles for write access.
    /// </summary>
    public string[] RequiredWriteRoles { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Protocol-specific links for the service.
/// </summary>
public sealed record ServiceLinks
{
    /// <summary>
    /// Self-reference links for different protocols.
    /// </summary>
    public Dictionary<string, string> SelfLinks { get; init; } = new();

    /// <summary>
    /// Metadata/capabilities links for different protocols.
    /// </summary>
    public Dictionary<string, string> MetadataLinks { get; init; } = new();

    /// <summary>
    /// Query endpoint links for different protocols.
    /// </summary>
    public Dictionary<string, string> QueryLinks { get; init; } = new();

    /// <summary>
    /// Additional protocol-specific links.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ProtocolLinks { get; init; } = new();

    /// <summary>
    /// Base URL used for link generation.
    /// </summary>
    public string BaseUrl { get; init; } = string.Empty;
}