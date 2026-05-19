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
    /// Service-level migration plans for protocol surfaces that require operator
    /// review before Honua can publish an equivalent service.
    /// </summary>
    public MigrationManifestServicePlan[] ServicePlans { get; init; } = [];

    /// <summary>
    /// Stable source-to-target identity remaps emitted by manifest translation.
    /// </summary>
    public MigrationManifestIdentityRemap[] IdentityRemaps { get; init; } = [];

    /// <summary>
    /// Optional source-id to target-id pairs that app migration tooling must apply when
    /// the manifest could not preserve a source identifier verbatim. Only entries whose
    /// identity stability is not <see cref="MigrationManifestIdentityStabilities.Preserved"/>
    /// are included so consumers can short-circuit when the identity was kept stable.
    /// </summary>
    public Dictionary<string, string> IdentityRemapping { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Optional fidelity matrix enriched with target identity mappings from this manifest.
    /// </summary>
    public MigrationFidelityMatrix? FidelityMatrix { get; init; }

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
    /// Number of service-level migration plans emitted into the manifest.
    /// </summary>
    public int ServicePlanCount { get; init; }

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
    /// Stable target resource identifier derived from the manifest target service and resource names.
    /// </summary>
    public required string TargetResourceId { get; init; }

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
    /// Migration mode selected for the resource, such as <c>feature-import</c>
    /// for WFS feature sources.
    /// </summary>
    public string? MigrationMode { get; init; }

    /// <summary>
    /// Source protocol used to stage the resource when it is protocol-specific.
    /// </summary>
    public string? SourceProtocol { get; init; }

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
    /// Optional source/target identity record. App migration tooling consults this to
    /// decide whether the source layer identifier was preserved on the target or remapped.
    /// </summary>
    public MigrationManifestResourceIdentity? Identity { get; init; }

    /// <summary>
    /// Compatibility assessment that justified the target action.
    /// </summary>
    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

/// <summary>
/// Service-level migration planning item translated from a source container.
/// </summary>
public sealed record MigrationManifestServicePlan
{
    /// <summary>
    /// Source inventory container identifier.
    /// </summary>
    public required string SourceContainerId { get; init; }

    /// <summary>
    /// Source inventory container kind.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Target migration action such as <c>manual-review</c>.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Target service name suggested for this plan.
    /// </summary>
    public required string TargetServiceName { get; init; }

    /// <summary>
    /// Source service type or protocol, such as <c>WMS</c> or <c>WMTS</c>.
    /// </summary>
    public string? ServiceType { get; init; }

    /// <summary>
    /// Source resources covered by the plan.
    /// </summary>
    public string[] ResourceIds { get; init; } = [];

    /// <summary>
    /// Source styles covered by the plan.
    /// </summary>
    public string[] StyleIds { get; init; } = [];

    /// <summary>
    /// External dependencies covered by the plan.
    /// </summary>
    public string[] ExternalDependencyIds { get; init; } = [];

    /// <summary>
    /// Optional source/target identity record for the planned service. Mirrors
    /// <see cref="MigrationManifestTargetResource.Identity"/> so app migration tooling can
    /// remap service-level identifiers consistently.
    /// </summary>
    public MigrationManifestServiceIdentity? Identity { get; init; }

    /// <summary>
    /// Compatibility assessment that justified the service plan action.
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
    /// Stable target style identifier reserved for this source style or renderer.
    /// </summary>
    public required string TargetStyleId { get; init; }

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
/// Stable source-to-target identity mapping emitted by manifest translation.
/// </summary>
public sealed record MigrationManifestIdentityRemap
{
    /// <summary>
    /// Source inventory identifier.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Source inventory kind.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Stable target identifier.
    /// </summary>
    public required string TargetId { get; init; }

    /// <summary>
    /// Target item kind.
    /// </summary>
    public required string TargetKind { get; init; }

    /// <summary>
    /// Target item name.
    /// </summary>
    public required string TargetName { get; init; }

    /// <summary>
    /// Manifest action associated with the mapping.
    /// </summary>
    public required string Action { get; init; }

    /// <summary>
    /// Identity stability classification for the mapping. One of
    /// <see cref="MigrationManifestIdentityStabilities.Preserved"/>,
    /// <see cref="MigrationManifestIdentityStabilities.Remapped"/>, or
    /// <see cref="MigrationManifestIdentityStabilities.Synthesized"/>.
    /// </summary>
    public string? IdentityStability { get; init; }

    /// <summary>
    /// Optional explanation when <see cref="IdentityStability"/> is
    /// <see cref="MigrationManifestIdentityStabilities.Remapped"/> or
    /// <see cref="MigrationManifestIdentityStabilities.Synthesized"/>.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Stable identity stability values emitted by manifest translation.
/// </summary>
public static class MigrationManifestIdentityStabilities
{
    /// <summary>The target identifier exactly matches the source identifier.</summary>
    public const string Preserved = "preserved";

    /// <summary>The target identifier was changed from the source identifier and the mapping is recorded.</summary>
    public const string Remapped = "remapped";

    /// <summary>The source did not advertise an identifier, so the manifest synthesized one.</summary>
    public const string Synthesized = "synthesized";
}

/// <summary>
/// Source and target identity record for a manifest target resource (layer or table).
/// </summary>
public sealed record MigrationManifestResourceIdentity
{
    /// <summary>
    /// Source service identifier (e.g. ArcGIS service key such as <c>Roads</c>).
    /// </summary>
    public string? SourceServiceId { get; init; }

    /// <summary>
    /// Source layer or table identifier as advertised by the source (e.g. ArcGIS integer layer id).
    /// Kept as a string so non-numeric identifiers can be carried verbatim.
    /// </summary>
    public string? SourceLayerId { get; init; }

    /// <summary>
    /// Fully qualified source resource name, including service or workspace prefix when applicable.
    /// </summary>
    public string? SourceQualifiedName { get; init; }

    /// <summary>
    /// Optional folder path the source resource lives under, when the source models a folder hierarchy.
    /// </summary>
    public string? SourceFolderPath { get; init; }

    /// <summary>
    /// Stable target Honua service identifier. Mirrors the source value when the source identifier was preserved.
    /// </summary>
    public string? TargetServiceId { get; init; }

    /// <summary>
    /// Stable target Honua layer or table identifier. Mirrors the source value when the source identifier was preserved.
    /// </summary>
    public string? TargetLayerId { get; init; }

    /// <summary>
    /// Target resource name on Honua.
    /// </summary>
    public string? TargetName { get; init; }

    /// <summary>
    /// Optional folder path the target resource will be placed under.
    /// </summary>
    public string? TargetFolderPath { get; init; }

    /// <summary>
    /// Identity stability classification. One of
    /// <see cref="MigrationManifestIdentityStabilities.Preserved"/>,
    /// <see cref="MigrationManifestIdentityStabilities.Remapped"/>, or
    /// <see cref="MigrationManifestIdentityStabilities.Synthesized"/>.
    /// </summary>
    public required string IdentityStability { get; init; }

    /// <summary>
    /// Optional explanation when the identity could not be preserved verbatim.
    /// </summary>
    public string? IdentityRemapReason { get; init; }
}

/// <summary>
/// Source and target identity record for a manifest service plan.
/// </summary>
public sealed record MigrationManifestServiceIdentity
{
    /// <summary>
    /// Source service identifier.
    /// </summary>
    public string? SourceServiceId { get; init; }

    /// <summary>
    /// Fully qualified source service name.
    /// </summary>
    public string? SourceQualifiedName { get; init; }

    /// <summary>
    /// Optional folder path the source service lives under.
    /// </summary>
    public string? SourceFolderPath { get; init; }

    /// <summary>
    /// Stable target Honua service identifier.
    /// </summary>
    public string? TargetServiceId { get; init; }

    /// <summary>
    /// Target service name on Honua.
    /// </summary>
    public string? TargetName { get; init; }

    /// <summary>
    /// Optional folder path the target service will be placed under.
    /// </summary>
    public string? TargetFolderPath { get; init; }

    /// <summary>
    /// Identity stability classification.
    /// </summary>
    public required string IdentityStability { get; init; }

    /// <summary>
    /// Optional explanation when the identity could not be preserved verbatim.
    /// </summary>
    public string? IdentityRemapReason { get; init; }
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
