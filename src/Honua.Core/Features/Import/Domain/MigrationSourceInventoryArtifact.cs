// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Deterministic planning artifact describing a migration source inventory.
/// </summary>
public sealed record MigrationSourceInventoryArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.source-inventory";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Source kind identifier such as <c>geoserver-rest</c> or <c>arcgis-geoservices-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Identity and version information for the scanned source.
    /// </summary>
    public required MigrationSourceIdentity Source { get; init; }

    /// <summary>
    /// Authentication posture observed during the scan.
    /// </summary>
    public required MigrationInventoryAuthPosture AuthPosture { get; init; }

    /// <summary>
    /// Completeness information for the resulting inventory.
    /// </summary>
    public required MigrationInventoryCompleteness ScanCompleteness { get; init; }

    /// <summary>
    /// Aggregate counts for the scanned inventory.
    /// </summary>
    public required MigrationInventorySummary Summary { get; init; }

    /// <summary>
    /// Overall compatibility assessment for the scanned source.
    /// </summary>
    public required MigrationCompatibilityAssessment OverallCompatibility { get; init; }

    /// <summary>
    /// Logical containers such as workspaces or services.
    /// </summary>
    public MigrationInventoryContainer[] Containers { get; init; } = [];

    /// <summary>
    /// Data resources such as layers, tables, or layer groups.
    /// </summary>
    public MigrationInventoryResource[] Resources { get; init; } = [];

    /// <summary>
    /// Styles or renderers discovered during the scan.
    /// </summary>
    public MigrationInventoryStyle[] Styles { get; init; } = [];

    /// <summary>
    /// External dependencies referenced by the source inventory.
    /// </summary>
    public MigrationExternalDependency[] ExternalDependencies { get; init; } = [];
}

/// <summary>
/// Identifies the scanned source environment.
/// </summary>
public sealed record MigrationSourceIdentity
{
    /// <summary>
    /// Human-readable source name.
    /// </summary>
    public required string DisplayName { get; init; }

    /// <summary>
    /// Canonical source URL used for the scan.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>
    /// Source product name.
    /// </summary>
    public string? Product { get; init; }

    /// <summary>
    /// Reported source version.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Optional build or revision identifier.
    /// </summary>
    public string? Build { get; init; }

    /// <summary>
    /// Optional service type or protocol subtype.
    /// </summary>
    public string? ServiceType { get; init; }
}

/// <summary>
/// Describes the authentication posture of a scan.
/// </summary>
public sealed record MigrationInventoryAuthPosture
{
    /// <summary>
    /// Posture label such as <c>anonymous</c>, <c>basic</c>, or <c>auth-required</c>.
    /// </summary>
    public required string Mode { get; init; }

    /// <summary>
    /// Whether the scanner had a complete, usable credential set for the scan.
    /// </summary>
    public bool CredentialsSupplied { get; init; }

    /// <summary>
    /// Whether the scan confirmed access with the supplied or anonymous posture.
    /// </summary>
    public bool AccessConfirmed { get; init; }

    /// <summary>
    /// Additional posture notes.
    /// </summary>
    public string[] Notes { get; init; } = [];
}

/// <summary>
/// Describes how complete a scan result is.
/// </summary>
public sealed record MigrationInventoryCompleteness
{
    /// <summary>
    /// Completeness state such as <c>complete</c>, <c>partial</c>, or <c>failed</c>.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Warnings that affected completeness.
    /// </summary>
    public string[] Warnings { get; init; } = [];

    /// <summary>
    /// Inventory areas that could not be scanned.
    /// </summary>
    public string[] MissingArtifacts { get; init; } = [];
}

/// <summary>
/// Aggregate counts for the inventory artifact.
/// </summary>
public sealed record MigrationInventorySummary
{
    /// <summary>
    /// Number of discovered containers.
    /// </summary>
    public int ContainerCount { get; init; }

    /// <summary>
    /// Number of discovered resources.
    /// </summary>
    public int ResourceCount { get; init; }

    /// <summary>
    /// Number of discovered styles or renderers.
    /// </summary>
    public int StyleCount { get; init; }

    /// <summary>
    /// Number of discovered external dependencies.
    /// </summary>
    public int ExternalDependencyCount { get; init; }

    /// <summary>
    /// Number of compatible inventory items.
    /// </summary>
    public int CompatibleCount { get; init; }

    /// <summary>
    /// Number of partially compatible inventory items.
    /// </summary>
    public int PartiallyCompatibleCount { get; init; }

    /// <summary>
    /// Number of incompatible inventory items.
    /// </summary>
    public int IncompatibleCount { get; init; }
}

/// <summary>
/// Generic compatibility assessment for a scanned inventory item.
/// </summary>
public sealed record MigrationCompatibilityAssessment
{
    /// <summary>
    /// Compatibility level such as <c>compatible</c>, <c>partial</c>, or <c>incompatible</c>.
    /// </summary>
    public required string Level { get; init; }

    /// <summary>
    /// Primary explanation for the assigned level.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Compatibility warnings for the item.
    /// </summary>
    public string[] Warnings { get; init; } = [];

    /// <summary>
    /// Manual steps needed to complete migration.
    /// </summary>
    public string[] ManualSteps { get; init; } = [];
}

/// <summary>
/// Container entry such as a GeoServer workspace or GeoServices service.
/// </summary>
public sealed record MigrationInventoryContainer
{
    /// <summary>
    /// Stable artifact-local identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Container kind.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Container name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional display title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Whether the container is the default container for the source.
    /// </summary>
    public bool? IsDefault { get; init; }

    /// <summary>
    /// Compatibility assessment for the container.
    /// </summary>
    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

/// <summary>
/// Resource entry such as a layer, table, or layer group.
/// </summary>
public sealed record MigrationInventoryResource
{
    /// <summary>
    /// Stable artifact-local identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Parent container identifier.
    /// </summary>
    public required string ContainerId { get; init; }

    /// <summary>
    /// Resource kind.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Resource name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Optional display title.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Optional description.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Geometry type when the resource carries geometry.
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Reported feature count when available.
    /// </summary>
    public int? FeatureCount { get; init; }

    /// <summary>
    /// Whether the resource advertises attachments. Omitted when the source does not report attachment state.
    /// </summary>
    public bool? HasAttachments { get; init; }

    /// <summary>
    /// Advertised resource capabilities.
    /// </summary>
    public string[] Capabilities { get; init; } = [];

    /// <summary>
    /// CRS, datum, and unit details relevant to migration planning.
    /// </summary>
    public MigrationSpatialReferenceInfo[] SpatialReferences { get; init; } = [];

    /// <summary>
    /// Related style or renderer identifiers.
    /// </summary>
    public string[] StyleIds { get; init; } = [];

    /// <summary>
    /// Related external dependency identifiers.
    /// </summary>
    public string[] ExternalDependencyIds { get; init; } = [];

    /// <summary>
    /// Compatibility assessment for the resource.
    /// </summary>
    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

/// <summary>
/// Style or renderer entry discovered from the source.
/// </summary>
public sealed record MigrationInventoryStyle
{
    /// <summary>
    /// Stable artifact-local identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Parent container identifier.
    /// </summary>
    public required string ContainerId { get; init; }

    /// <summary>
    /// Style kind such as <c>style</c> or <c>renderer</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Style or renderer name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Style or renderer format.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// Resources associated with the style or renderer.
    /// </summary>
    public string[] ResourceIds { get; init; } = [];

    /// <summary>
    /// Related external dependency identifiers.
    /// </summary>
    public string[] ExternalDependencyIds { get; init; } = [];

    /// <summary>
    /// Additional deterministic metadata for planning. Raw style documents are not echoed in this field.
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = [];

    /// <summary>
    /// Compatibility assessment for the style or renderer.
    /// </summary>
    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

/// <summary>
/// External dependency discovered during the scan.
/// </summary>
public sealed record MigrationExternalDependency
{
    /// <summary>
    /// Stable artifact-local identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Parent container identifier.
    /// </summary>
    public required string ContainerId { get; init; }

    /// <summary>
    /// Optional related layer/table identifier or owning style/renderer identifier.
    /// </summary>
    public string? ResourceId { get; init; }

    /// <summary>
    /// Dependency kind such as <c>datastore</c> or <c>external-graphic</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Dependency name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Dependency subtype or source-specific type label.
    /// </summary>
    public string? DependencyType { get; init; }

    /// <summary>
    /// Dependency address or endpoint when available. External URLs are normalized to a secret-safe form.
    /// </summary>
    public string? Address { get; init; }

    /// <summary>
    /// Additional deterministic metadata for planning.
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = [];

    /// <summary>
    /// CRS, datum, and unit details relevant to the dependency.
    /// </summary>
    public MigrationSpatialReferenceInfo[] SpatialReferences { get; init; } = [];

    /// <summary>
    /// Compatibility assessment for the dependency.
    /// </summary>
    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

/// <summary>
/// Spatial reference details extracted for migration planning.
/// </summary>
public sealed record MigrationSpatialReferenceInfo
{
    /// <summary>
    /// Role of the spatial reference entry, such as <c>declared</c>, <c>native</c>, or <c>service</c>.
    /// </summary>
    public required string Role { get; init; }

    /// <summary>
    /// Source-provided CRS text or identifier.
    /// </summary>
    public string? SourceValue { get; init; }

    /// <summary>
    /// Resolved SRID when available.
    /// </summary>
    public int? Srid { get; init; }

    /// <summary>
    /// Canonical CRS URI when resolved or safely normalized to EPSG.
    /// </summary>
    public string? CrsUri { get; init; }

    /// <summary>
    /// Datum name when it could be inferred.
    /// </summary>
    public string? Datum { get; init; }

    /// <summary>
    /// Linear or angular unit name when it could be inferred.
    /// </summary>
    public string? Unit { get; init; }

    /// <summary>
    /// Axis order label when known.
    /// </summary>
    public string? AxisOrder { get; init; }

    /// <summary>
    /// Whether the CRS is geographic.
    /// </summary>
    public bool? IsGeographic { get; init; }
}
