// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Deterministic intermediate artifact that translates source inventory into
/// target Honua migration intent.
/// </summary>
public sealed record MigrationManifestArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.manifest";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Source inventory artifact kind this manifest was translated from.
    /// </summary>
    public string SourceArtifactKind { get; init; } = "honua.migration.source-inventory";

    /// <summary>
    /// Source inventory artifact version this manifest was translated from.
    /// </summary>
    public string SourceArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Source kind identifier such as <c>geoserver-rest</c> or <c>arcgis-geoservices-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Identity and version information for the scanned source.
    /// </summary>
    public required MigrationSourceIdentity Source { get; init; }

    /// <summary>
    /// Deterministic translation summary.
    /// </summary>
    public required MigrationManifestSummary Summary { get; init; }

    /// <summary>
    /// Target resources that can be published or staged from the source inventory.
    /// </summary>
    public MigrationManifestTargetResource[] TargetResources { get; init; } = [];

    /// <summary>
    /// Target style actions. Unsupported styles are listed here as manual-review
    /// actions instead of being claimed as migrated.
    /// </summary>
    public MigrationManifestStyleAction[] StyleActions { get; init; } = [];

    /// <summary>
    /// Items that require operator review before migration can proceed.
    /// </summary>
    public MigrationManifestReviewItem[] ManualReviewItems { get; init; } = [];

    /// <summary>
    /// Items that cannot be translated into target Honua intent by this slice.
    /// </summary>
    public MigrationManifestReviewItem[] UnsupportedItems { get; init; } = [];
}

/// <summary>
/// Aggregate counts for a migration manifest artifact.
/// </summary>
public sealed record MigrationManifestSummary
{
    /// <summary>
    /// Number of source resources considered for translation.
    /// </summary>
    public int SourceResourceCount { get; init; }

    /// <summary>
    /// Number of target resources emitted into the manifest.
    /// </summary>
    public int TargetResourceCount { get; init; }

    /// <summary>
    /// Number of style actions emitted into the manifest.
    /// </summary>
    public int StyleActionCount { get; init; }

    /// <summary>
    /// Number of source items requiring manual review.
    /// </summary>
    public int ManualReviewCount { get; init; }

    /// <summary>
    /// Number of source items that are unsupported for deterministic translation.
    /// </summary>
    public int UnsupportedCount { get; init; }
}

/// <summary>
/// Target resource intent translated from one source inventory resource.
/// </summary>
public sealed record MigrationManifestTargetResource
{
    /// <summary>
    /// Source inventory resource identifier.
    /// </summary>
    public required string SourceResourceId { get; init; }

    /// <summary>
    /// Source inventory resource kind.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Target migration action such as <c>publish</c> or <c>manual-review</c>.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Target service name suggested for this resource.
    /// </summary>
    public required string TargetServiceName { get; init; }

    /// <summary>
    /// Target resource name suggested for this resource.
    /// </summary>
    public required string TargetResourceName { get; init; }

    /// <summary>
    /// Geometry type copied from the inventory when available.
    /// </summary>
    public string? GeometryType { get; init; }

    /// <summary>
    /// Field schema copied from the source inventory.
    /// </summary>
    public MigrationInventoryField[] Fields { get; init; } = [];

    /// <summary>
    /// Source capabilities copied into the manifest for operator review.
    /// </summary>
    public string[] Capabilities { get; init; } = [];

    /// <summary>
    /// Source spatial references copied into the manifest for operator review.
    /// </summary>
    public MigrationSpatialReferenceInfo[] SpatialReferences { get; init; } = [];

    /// <summary>
    /// Related style identifiers from the source inventory.
    /// </summary>
    public string[] StyleIds { get; init; } = [];

    /// <summary>
    /// Related external dependency identifiers from the source inventory.
    /// </summary>
    public string[] ExternalDependencyIds { get; init; } = [];

    /// <summary>
    /// Compatibility assessment that justified the target action.
    /// </summary>
    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

/// <summary>
/// Target style action translated from a source style or renderer.
/// </summary>
public sealed record MigrationManifestStyleAction
{
    /// <summary>
    /// Source style identifier.
    /// </summary>
    public required string SourceStyleId { get; init; }

    /// <summary>
    /// Target action such as <c>import</c> or <c>manual-review</c>.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Source style format.
    /// </summary>
    public string? Format { get; init; }

    /// <summary>
    /// Related source resource identifiers.
    /// </summary>
    public string[] ResourceIds { get; init; } = [];

    /// <summary>
    /// External dependencies that must be resolved before style migration.
    /// </summary>
    public string[] ExternalDependencyIds { get; init; } = [];

    /// <summary>
    /// Compatibility assessment that justified the target action.
    /// </summary>
    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

/// <summary>
/// Review or unsupported item emitted during manifest translation.
/// </summary>
public sealed record MigrationManifestReviewItem
{
    /// <summary>
    /// Source artifact identifier for the item.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Source item kind.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Stable machine-readable code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Review severity such as <c>manual-review</c> or <c>unsupported</c>.
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Human-readable reason.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Operator remediation guidance.
    /// </summary>
    public string[] ManualSteps { get; init; } = [];

    /// <summary>
    /// Warnings copied from the source compatibility assessment.
    /// </summary>
    public string[] Warnings { get; init; } = [];
}

/// <summary>
/// Options for translating source inventory into a target migration manifest.
/// </summary>
public sealed record MigrationManifestTranslationOptions
{
    /// <summary>
    /// Target service name to use for translated resources. When omitted, the
    /// source display name is normalized into a deterministic service name.
    /// </summary>
    public string? TargetServiceName { get; init; }
}
