// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Represents metadata about a GeoServer instance discovered from a REST API URL.
/// </summary>
public sealed record GeoServerServiceInfo
{
    /// <summary>
    /// The URL of the GeoServer REST API.
    /// </summary>
    public required string GeoServerRestUrl { get; init; }

    /// <summary>
    /// GeoServer version information (e.g., "2.23.0", "2.22.1").
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// GeoServer build timestamp.
    /// </summary>
    public string? BuildTimestamp { get; init; }

    /// <summary>
    /// Git revision of the GeoServer build.
    /// </summary>
    public string? GitRevision { get; init; }

    /// <summary>
    /// Global settings of the GeoServer instance.
    /// </summary>
    public GeoServerGlobalSettings? GlobalSettings { get; init; }

    /// <summary>
    /// Workspaces available in this GeoServer instance.
    /// </summary>
    public GeoServerWorkspaceInfo[] Workspaces { get; init; } = [];

    /// <summary>
    /// Datastores available across all workspaces.
    /// </summary>
    public GeoServerDataStoreInfo[] DataStores { get; init; } = [];

    /// <summary>
    /// Coverage stores available across all workspaces.
    /// </summary>
    public GeoServerCoverageStoreInfo[] CoverageStores { get; init; } = [];

    /// <summary>
    /// Layers available across all workspaces.
    /// </summary>
    public GeoServerLayerInfo[] Layers { get; init; } = [];

    /// <summary>
    /// Layer groups available across all workspaces.
    /// </summary>
    public GeoServerLayerGroupInfo[] LayerGroups { get; init; } = [];

    /// <summary>
    /// Styles available across all workspaces.
    /// </summary>
    public GeoServerStyleInfo[] Styles { get; init; } = [];

    /// <summary>
    /// Migration compatibility assessment.
    /// </summary>
    public GeoServerMigrationCompatibility? CompatibilityAssessment { get; init; }

    /// <summary>
    /// When this discovery was performed.
    /// </summary>
    public DateTimeOffset DiscoveredAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Represents a GeoServer workspace.
/// </summary>
public sealed record GeoServerWorkspaceInfo
{
    /// <summary>
    /// Name of the workspace.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Description of the workspace.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether this is the default workspace.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// Whether the workspace is isolated.
    /// </summary>
    public bool IsIsolated { get; init; }

    /// <summary>
    /// When the workspace was created.
    /// </summary>
    public DateTimeOffset? DateCreated { get; init; }

    /// <summary>
    /// When the workspace was last modified.
    /// </summary>
    public DateTimeOffset? DateModified { get; init; }
}

/// <summary>
/// Represents a GeoServer datastore.
/// </summary>
public sealed record GeoServerDataStoreInfo
{
    /// <summary>
    /// Name of the datastore.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Workspace the datastore belongs to.
    /// </summary>
    public required string WorkspaceName { get; init; }

    /// <summary>
    /// Description of the datastore.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Type of datastore (e.g., "PostGIS", "Shapefile", "Oracle").
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Connection parameters for the datastore.
    /// </summary>
    public IReadOnlyDictionary<string, object> ConnectionParameters { get; init; } = new Dictionary<string, object>();

    /// <summary>
    /// Whether the datastore is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether this is the default datastore for the workspace.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// Compatibility assessment for migration to Honua.
    /// </summary>
    public GeoServerResourceCompatibility? Compatibility { get; init; }
}

/// <summary>
/// Represents a GeoServer coverage store (for raster data).
/// </summary>
public sealed record GeoServerCoverageStoreInfo
{
    /// <summary>
    /// Name of the coverage store.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Workspace the coverage store belongs to.
    /// </summary>
    public required string WorkspaceName { get; init; }

    /// <summary>
    /// Description of the coverage store.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Type of coverage store (e.g., "GeoTIFF", "ArcGrid", "ImageMosaic").
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// Connection parameters for the coverage store.
    /// </summary>
    public IReadOnlyDictionary<string, object> ConnectionParameters { get; init; } = new Dictionary<string, object>();

    /// <summary>
    /// Whether the coverage store is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether this is the default coverage store for the workspace.
    /// </summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// Compatibility assessment for migration to Honua.
    /// </summary>
    public GeoServerResourceCompatibility? Compatibility { get; init; }
}

/// <summary>
/// Represents a GeoServer layer.
/// </summary>
public sealed record GeoServerLayerInfo
{
    /// <summary>
    /// Name of the layer.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Workspace the layer belongs to.
    /// </summary>
    public required string WorkspaceName { get; init; }

    /// <summary>
    /// Title of the layer.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Abstract/description of the layer.
    /// </summary>
    public string? Abstract { get; init; }

    /// <summary>
    /// Default style for the layer.
    /// </summary>
    public string? DefaultStyle { get; init; }

    /// <summary>
    /// Alternative styles available for the layer.
    /// </summary>
    public string[] AlternativeStyles { get; init; } = [];

    /// <summary>
    /// Datastore this layer is associated with.
    /// </summary>
    public string? DataStoreName { get; init; }

    /// <summary>
    /// Coverage store this layer is associated with.
    /// </summary>
    public string? CoverageStoreName { get; init; }

    /// <summary>
    /// Native name in the datastore.
    /// </summary>
    public string? NativeName { get; init; }

    /// <summary>
    /// Spatial reference system (SRS) code.
    /// </summary>
    public string? SRS { get; init; }

    /// <summary>
    /// Native coordinate reference system.
    /// </summary>
    public string? NativeCRS { get; init; }

    /// <summary>
    /// Geometry column name exposed by the backing feature type when known.
    /// </summary>
    public string? GeometryColumn { get; init; }

    /// <summary>
    /// Geometry type exposed by the backing feature type when known.
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Bounding box in lat/lon coordinates.
    /// </summary>
    public GeoServerBoundingBox? LatLonBoundingBox { get; init; }

    /// <summary>
    /// Native bounding box in the layer's CRS.
    /// </summary>
    public GeoServerBoundingBox? NativeBoundingBox { get; init; }

    /// <summary>
    /// Whether the layer is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Whether the layer is queryable.
    /// </summary>
    public bool Queryable { get; init; } = true;

    /// <summary>
    /// Whether the layer is opaque.
    /// </summary>
    public bool Opaque { get; init; }

    /// <summary>
    /// Keywords associated with the layer.
    /// </summary>
    public string[] Keywords { get; init; } = [];

    /// <summary>
    /// Additional metadata for the layer.
    /// </summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();

    /// <summary>
    /// Compatibility assessment for migration to Honua.
    /// </summary>
    public GeoServerResourceCompatibility? Compatibility { get; init; }
}

/// <summary>
/// Represents a GeoServer layer group.
/// </summary>
public sealed record GeoServerLayerGroupInfo
{
    /// <summary>
    /// Name of the layer group.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Workspace the layer group belongs to.
    /// </summary>
    public required string WorkspaceName { get; init; }

    /// <summary>
    /// Title of the layer group.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Abstract/description of the layer group.
    /// </summary>
    public string? Abstract { get; init; }

    /// <summary>
    /// Mode of the layer group (e.g., "SINGLE", "OPAQUE", "CONTAINER").
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// Layers contained in this group.
    /// </summary>
    public GeoServerLayerGroupEntry[] Layers { get; init; } = [];

    /// <summary>
    /// Styles associated with the layers in the group.
    /// </summary>
    public GeoServerLayerGroupEntry[] Styles { get; init; } = [];

    /// <summary>
    /// Bounding box of the layer group.
    /// </summary>
    public GeoServerBoundingBox? Bounds { get; init; }

    /// <summary>
    /// Whether the layer group is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Compatibility assessment for migration to Honua.
    /// </summary>
    public GeoServerResourceCompatibility? Compatibility { get; init; }
}

/// <summary>
/// Entry in a layer group (layer or nested group).
/// </summary>
public sealed record GeoServerLayerGroupEntry
{
    /// <summary>
    /// Name of the layer or nested group.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Workspace the resource belongs to.
    /// </summary>
    public string? WorkspaceName { get; init; }

    /// <summary>
    /// Type of entry ("LAYER" or "GROUP").
    /// </summary>
    public required string Type { get; init; }
}

/// <summary>
/// Represents a GeoServer style.
/// </summary>
public sealed record GeoServerStyleInfo
{
    /// <summary>
    /// Name of the style.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Workspace the style belongs to (null for global styles).
    /// </summary>
    public string? WorkspaceName { get; init; }

    /// <summary>
    /// Filename of the style.
    /// </summary>
    public string? Filename { get; init; }

    /// <summary>
    /// Format of the style (e.g., "sld", "css").
    /// </summary>
    public string Format { get; init; } = "sld";

    /// <summary>
    /// Language version (e.g., "1.0.0", "1.1.0").
    /// </summary>
    public string? LanguageVersion { get; init; }

    /// <summary>
    /// SLD content (if requested in discovery).
    /// </summary>
    public string? SldContent { get; init; }

    /// <summary>
    /// When the style was created.
    /// </summary>
    public DateTimeOffset? DateCreated { get; init; }

    /// <summary>
    /// When the style was last modified.
    /// </summary>
    public DateTimeOffset? DateModified { get; init; }

    /// <summary>
    /// Compatibility assessment for migration to Honua (requires issue #375).
    /// </summary>
    public GeoServerResourceCompatibility? Compatibility { get; init; }
}

/// <summary>
/// Bounding box coordinates.
/// </summary>
public sealed record GeoServerBoundingBox
{
    /// <summary>
    /// Minimum X coordinate.
    /// </summary>
    public double MinX { get; init; }

    /// <summary>
    /// Minimum Y coordinate.
    /// </summary>
    public double MinY { get; init; }

    /// <summary>
    /// Maximum X coordinate.
    /// </summary>
    public double MaxX { get; init; }

    /// <summary>
    /// Maximum Y coordinate.
    /// </summary>
    public double MaxY { get; init; }

    /// <summary>
    /// Coordinate reference system.
    /// </summary>
    public string? CRS { get; init; }
}

/// <summary>
/// GeoServer global settings.
/// </summary>
public sealed record GeoServerGlobalSettings
{
    /// <summary>
    /// Contact information.
    /// </summary>
    public GeoServerContactInfo? ContactInfo { get; init; }

    /// <summary>
    /// Service title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Service abstract.
    /// </summary>
    public string? Abstract { get; init; }

    /// <summary>
    /// Online resource URL.
    /// </summary>
    public string? OnlineResource { get; init; }

    /// <summary>
    /// Schema base URL.
    /// </summary>
    public string? SchemaBaseUrl { get; init; }

    /// <summary>
    /// Character set.
    /// </summary>
    public string? Charset { get; init; }

    /// <summary>
    /// Proxy base URL.
    /// </summary>
    public string? ProxyBaseUrl { get; init; }

    /// <summary>
    /// Number of decimals for coordinates.
    /// </summary>
    public int? NumDecimals { get; init; }

    /// <summary>
    /// Whether verbose messages are enabled.
    /// </summary>
    public bool? VerboseMessages { get; init; }

    /// <summary>
    /// Whether verbose exceptions are enabled.
    /// </summary>
    public bool? VerboseExceptions { get; init; }
}

/// <summary>
/// GeoServer contact information.
/// </summary>
public sealed record GeoServerContactInfo
{
    /// <summary>
    /// Contact person name.
    /// </summary>
    public string? ContactPerson { get; init; }

    /// <summary>
    /// Contact organization.
    /// </summary>
    public string? ContactOrganization { get; init; }

    /// <summary>
    /// Contact position/title.
    /// </summary>
    public string? ContactPosition { get; init; }

    /// <summary>
    /// Address type.
    /// </summary>
    public string? AddressType { get; init; }

    /// <summary>
    /// Street address.
    /// </summary>
    public string? Address { get; init; }

    /// <summary>
    /// City.
    /// </summary>
    public string? AddressCity { get; init; }

    /// <summary>
    /// State/Province.
    /// </summary>
    public string? AddressState { get; init; }

    /// <summary>
    /// Postal code.
    /// </summary>
    public string? AddressPostalCode { get; init; }

    /// <summary>
    /// Country.
    /// </summary>
    public string? AddressCountry { get; init; }

    /// <summary>
    /// Phone number.
    /// </summary>
    public string? ContactVoice { get; init; }

    /// <summary>
    /// Fax number.
    /// </summary>
    public string? ContactFacsimile { get; init; }

    /// <summary>
    /// Email address.
    /// </summary>
    public string? ContactEmail { get; init; }
}

/// <summary>
/// Migration compatibility assessment for the entire GeoServer instance.
/// </summary>
public sealed record GeoServerMigrationCompatibility
{
    /// <summary>
    /// Number of resources that can be migrated without issues.
    /// </summary>
    public int FullyCompatibleResources { get; init; }

    /// <summary>
    /// Number of resources that can be migrated with minor issues.
    /// </summary>
    public int PartiallyCompatibleResources { get; init; }

    /// <summary>
    /// Number of resources that cannot be migrated automatically.
    /// </summary>
    public int IncompatibleResources { get; init; }

    /// <summary>
    /// Overall compatibility percentage (0-100).
    /// </summary>
    public double OverallCompatibilityPercentage => FullyCompatibleResources + PartiallyCompatibleResources + IncompatibleResources > 0
        ? (double)(FullyCompatibleResources + PartiallyCompatibleResources) / (FullyCompatibleResources + PartiallyCompatibleResources + IncompatibleResources) * 100
        : 0;

    /// <summary>
    /// Breakdown by resource type.
    /// </summary>
    public GeoServerResourceTypeCompatibility[] ResourceTypeBreakdown { get; init; } = [];

    /// <summary>
    /// List of unsupported features that require manual intervention.
    /// </summary>
    public string[] UnsupportedFeatures { get; init; } = [];

    /// <summary>
    /// Required manual steps for successful migration.
    /// </summary>
    public string[] RequiredManualSteps { get; init; } = [];

    /// <summary>
    /// Migration warnings and recommendations.
    /// </summary>
    public string[] Warnings { get; init; } = [];
}

/// <summary>
/// Compatibility assessment for a specific resource.
/// </summary>
public sealed record GeoServerResourceCompatibility
{
    /// <summary>
    /// Compatibility level.
    /// </summary>
    public required GeoServerCompatibilityLevel CompatibilityLevel { get; init; }

    /// <summary>
    /// Reason for compatibility level.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Required manual steps (if any).
    /// </summary>
    public string[] RequiredManualSteps { get; init; } = [];

    /// <summary>
    /// Warnings for this resource.
    /// </summary>
    public string[] Warnings { get; init; } = [];

    /// <summary>
    /// Whether automatic migration is possible.
    /// </summary>
    public bool CanMigrateAutomatically => CompatibilityLevel != GeoServerCompatibilityLevel.Incompatible;
}

/// <summary>
/// Compatibility breakdown by resource type.
/// </summary>
public sealed record GeoServerResourceTypeCompatibility
{
    /// <summary>
    /// Resource type name.
    /// </summary>
    public required string ResourceType { get; init; }

    /// <summary>
    /// Number of fully compatible resources.
    /// </summary>
    public int FullyCompatible { get; init; }

    /// <summary>
    /// Number of partially compatible resources.
    /// </summary>
    public int PartiallyCompatible { get; init; }

    /// <summary>
    /// Number of incompatible resources.
    /// </summary>
    public int Incompatible { get; init; }

    /// <summary>
    /// Total number of resources.
    /// </summary>
    public int Total => FullyCompatible + PartiallyCompatible + Incompatible;

    /// <summary>
    /// Compatibility percentage for this resource type.
    /// </summary>
    public double CompatibilityPercentage => Total > 0
        ? (double)(FullyCompatible + PartiallyCompatible) / Total * 100
        : 0;
}

/// <summary>
/// Compatibility levels for GeoServer resources.
/// </summary>
public enum GeoServerCompatibilityLevel
{
    /// <summary>
    /// Fully compatible - can be migrated without issues.
    /// </summary>
    FullyCompatible,

    /// <summary>
    /// Partially compatible - can be migrated with minor manual intervention.
    /// </summary>
    PartiallyCompatible,

    /// <summary>
    /// Incompatible - cannot be migrated automatically, requires significant manual work.
    /// </summary>
    Incompatible
}
