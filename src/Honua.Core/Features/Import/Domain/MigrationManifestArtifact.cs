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
    /// Optional attachment migration classification records derived from the source inventory.
    /// Each record reports the source attachment posture (file size, content type) and an
    /// automation classification so target apply can decide whether to pull, defer, or skip
    /// per-attachment migration. Empty when the source did not advertise attachments.
    /// </summary>
    public MigrationManifestAttachmentRecord[] Attachments { get; init; } = [];

    /// <summary>
    /// Optional relationship migration classification records derived from the source inventory.
    /// Each record captures the relationship cardinality, related layer ids, and an automation
    /// classification so target apply can recreate or document the relationship. Empty when the
    /// source did not advertise relationships.
    /// </summary>
    public MigrationManifestRelationshipRecord[] Relationships { get; init; } = [];

    /// <summary>
    /// Optional renderer migration diagnostic records derived from the source inventory's
    /// <c>drawingInfo.renderer</c> metadata. Each record captures the renderer kind, an
    /// automation classification, and an optional suggested target style id so target apply can
    /// pick the assisted/manual-review lane per renderer. Empty when the source did not advertise
    /// any renderers tied to the resource.
    /// </summary>
    public MigrationManifestRendererDiagnostic[] RendererDiagnostics { get; init; } = [];

    /// <summary>
    /// Optional label-class migration diagnostic records derived from the source inventory's
    /// <c>drawingInfo.labelingInfo[]</c> metadata. Each record captures the label expression
    /// (when advertised), the expression engine, and an automation classification so target apply
    /// can either recreate, document, or skip a label class. Empty when the source did not
    /// advertise any label classes tied to the resource.
    /// </summary>
    public MigrationManifestLabelClassDiagnostic[] LabelClassDiagnostics { get; init; } = [];

    /// <summary>
    /// Compatibility assessment that justified the target action.
    /// </summary>
    public required MigrationCompatibilityAssessment Compatibility { get; init; }
}

/// <summary>
/// Stable renderer classification values emitted by manifest translation.
/// </summary>
public static class MigrationManifestRendererClassifications
{
    /// <summary>Renderer can be migrated automatically (typically a <c>simple</c> renderer).</summary>
    public const string Automated = "automated";

    /// <summary>
    /// Renderer can be migrated with operator-supervised follow-up
    /// (typically <c>uniqueValue</c> or small <c>classBreaks</c> renderers).
    /// </summary>
    public const string Assisted = "assisted";

    /// <summary>
    /// Renderer was captured for explicit operator review before migration
    /// (e.g. <c>dictionary</c>, complex expressions, raster shading, very large category sets).
    /// </summary>
    public const string ManualReview = "manual-review";

    /// <summary>Renderer cannot be migrated by this slice.</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>
/// Per-renderer migration diagnostic record emitted by the manifest translator.
/// </summary>
public sealed record MigrationManifestRendererDiagnostic
{
    /// <summary>
    /// Source style identifier the renderer belongs to (e.g. <c>renderer:{service}:{layerId}</c>).
    /// </summary>
    public required string SourceStyleId { get; init; }

    /// <summary>
    /// Source resource identifier the renderer is bound to.
    /// </summary>
    public required string SourceResourceId { get; init; }

    /// <summary>
    /// Renderer kind as advertised by the source (e.g. <c>simple</c>, <c>uniqueValue</c>,
    /// <c>classBreaks</c>, <c>dictionary</c>, <c>rasterShaded</c>).
    /// </summary>
    public required string RendererKind { get; init; }

    /// <summary>
    /// Number of categories advertised for <c>uniqueValue</c> renderers and number of breaks
    /// advertised for <c>classBreaks</c> renderers. <c>null</c> when the source did not advertise
    /// a category count or the renderer kind does not have one.
    /// </summary>
    public int? CategoryCount { get; init; }

    /// <summary>
    /// Automation classification. One of
    /// <see cref="MigrationManifestRendererClassifications.Automated"/>,
    /// <see cref="MigrationManifestRendererClassifications.Assisted"/>,
    /// <see cref="MigrationManifestRendererClassifications.ManualReview"/>, or
    /// <see cref="MigrationManifestRendererClassifications.Unsupported"/>.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Optional suggested target style identifier that target apply may pre-bind for this
    /// renderer when the classification is automated or assisted. Mirrors the deterministic
    /// target style id reservation pattern (<c>target:style:{service}:{resource}:{kind}</c>).
    /// </summary>
    public string? SuggestedTargetStyleId { get; init; }

    /// <summary>
    /// Optional human-readable explanation for the classification.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Stable label-class classification values emitted by manifest translation.
/// </summary>
public static class MigrationManifestLabelClassClassifications
{
    /// <summary>Label class can be migrated automatically.</summary>
    public const string Automated = "automated";

    /// <summary>Label class can be migrated with operator-supervised follow-up.</summary>
    public const string Assisted = "assisted";

    /// <summary>Label class was captured for explicit operator review before migration.</summary>
    public const string ManualReview = "manual-review";

    /// <summary>
    /// Label class cannot be migrated by this slice (e.g. <c>VBScript</c> expressions).
    /// </summary>
    public const string Unsupported = "unsupported";
}

/// <summary>
/// Per-label-class migration diagnostic record emitted by the manifest translator.
/// </summary>
public sealed record MigrationManifestLabelClassDiagnostic
{
    /// <summary>
    /// Source style identifier the label class is bound to.
    /// </summary>
    public required string SourceStyleId { get; init; }

    /// <summary>
    /// Source resource identifier the label class is bound to.
    /// </summary>
    public required string SourceResourceId { get; init; }

    /// <summary>
    /// Zero-based index of the label class as advertised in the source <c>labelingInfo</c> array.
    /// </summary>
    public int LabelClassIndex { get; init; }

    /// <summary>
    /// Label expression text as advertised by the source. May be a field token (<c>[NAME]</c>),
    /// an Arcade expression, a VBScript expression, or a Python expression. <c>null</c> when the
    /// source did not advertise an expression.
    /// </summary>
    public string? Expression { get; init; }

    /// <summary>
    /// Label expression engine as advertised by the source (e.g. <c>Arcade</c>, <c>VBScript</c>,
    /// <c>Python</c>, <c>JScript</c>). <c>null</c> when the engine was not advertised; defaults
    /// to the source's documented field-substitution syntax.
    /// </summary>
    public string? ExpressionEngine { get; init; }

    /// <summary>
    /// Automation classification. One of
    /// <see cref="MigrationManifestLabelClassClassifications.Automated"/>,
    /// <see cref="MigrationManifestLabelClassClassifications.Assisted"/>,
    /// <see cref="MigrationManifestLabelClassClassifications.ManualReview"/>, or
    /// <see cref="MigrationManifestLabelClassClassifications.Unsupported"/>.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Optional human-readable explanation for the classification.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Stable attachment classification values emitted by manifest translation.
/// </summary>
public static class MigrationManifestAttachmentClassifications
{
    /// <summary>Attachment is small and a supported content type; it can be migrated automatically.</summary>
    public const string Automated = "automated";

    /// <summary>Attachment is captured but requires an operator-supervised follow-up migration step.</summary>
    public const string Assisted = "assisted";

    /// <summary>Attachment metadata is captured for explicit operator review before migration.</summary>
    public const string ManualReview = "manual-review";

    /// <summary>Attachment cannot be migrated by this slice (e.g. disallowed content type).</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>
/// Per-attachment migration classification record emitted by the manifest translator.
/// </summary>
public sealed record MigrationManifestAttachmentRecord
{
    /// <summary>
    /// Source attachment identifier as advertised by the source service (e.g. the ArcGIS attachmentId).
    /// </summary>
    public required string SourceAttachmentId { get; init; }

    /// <summary>
    /// Source resource identifier the attachment belongs to.
    /// </summary>
    public required string SourceResourceId { get; init; }

    /// <summary>
    /// Optional attachment file name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Optional advertised content type (MIME) for the attachment.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Optional attachment size in bytes when advertised by the source.
    /// </summary>
    public long? Size { get; init; }

    /// <summary>
    /// Automation classification. One of
    /// <see cref="MigrationManifestAttachmentClassifications.Automated"/>,
    /// <see cref="MigrationManifestAttachmentClassifications.Assisted"/>,
    /// <see cref="MigrationManifestAttachmentClassifications.ManualReview"/>, or
    /// <see cref="MigrationManifestAttachmentClassifications.Unsupported"/>.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Optional stable target attachment reference (e.g. <c>target:attachment:{service}:{resource}:{id}</c>)
    /// that target apply will materialize when the classification is automated or assisted.
    /// </summary>
    public string? TargetAttachmentRef { get; init; }

    /// <summary>
    /// Optional human-readable explanation for the classification.
    /// </summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Stable relationship classification values emitted by manifest translation.
/// </summary>
public static class MigrationManifestRelationshipClassifications
{
    /// <summary>Relationship can be recreated automatically by target apply.</summary>
    public const string Automated = "automated";

    /// <summary>Relationship is captured but requires an operator-supervised follow-up step.</summary>
    public const string Assisted = "assisted";

    /// <summary>Relationship metadata is captured for explicit operator review.</summary>
    public const string ManualReview = "manual-review";

    /// <summary>Relationship is not supported by this slice and must be manually re-modeled.</summary>
    public const string Unsupported = "unsupported";
}

/// <summary>
/// Per-relationship migration classification record emitted by the manifest translator.
/// </summary>
public sealed record MigrationManifestRelationshipRecord
{
    /// <summary>
    /// Source relationship identifier as advertised by the source service.
    /// </summary>
    public required string SourceRelationshipId { get; init; }

    /// <summary>
    /// Source resource identifier that owns or originates the relationship.
    /// </summary>
    public required string SourceResourceId { get; init; }

    /// <summary>
    /// Optional relationship display name.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Optional relationship cardinality such as <c>1:1</c>, <c>1:N</c>, or <c>M:N</c>.
    /// </summary>
    public string? Cardinality { get; init; }

    /// <summary>
    /// Optional relationship subtype such as <c>simple</c>, <c>composite</c>, or <c>attributed</c>.
    /// </summary>
    public string? RelationshipType { get; init; }

    /// <summary>
    /// Related source layer or table identifiers (e.g. parent and child layer ids).
    /// </summary>
    public string[] RelatedLayerIds { get; init; } = [];

    /// <summary>
    /// Automation classification. One of
    /// <see cref="MigrationManifestRelationshipClassifications.Automated"/>,
    /// <see cref="MigrationManifestRelationshipClassifications.Assisted"/>,
    /// <see cref="MigrationManifestRelationshipClassifications.ManualReview"/>, or
    /// <see cref="MigrationManifestRelationshipClassifications.Unsupported"/>.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Optional stable target relationship reference that target apply will materialize when the
    /// classification is automated or assisted.
    /// </summary>
    public string? TargetRelationshipRef { get; init; }

    /// <summary>
    /// Optional human-readable explanation for the classification.
    /// </summary>
    public string? Reason { get; init; }
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
    /// Classified per-area migration plan entries for render-only (WMS/WMTS)
    /// services. Each entry tags whether layer metadata, style references, tile
    /// sets, or render endpoints are <c>automated</c>, <c>assisted</c>,
    /// <c>manual-review</c>, or <c>unsupported</c> for migration. Populated by
    /// the dedicated WMS/WMTS migration planners for render-only sources; empty
    /// for non render-only translations.
    /// </summary>
    public MigrationManifestPlanEntry[] PlanEntries { get; init; } = [];

    /// <summary>
    /// Diagnostics captured during planning (for example, SLD/SE references that
    /// were noted but not auto-imported by this slice). Empty when there is
    /// nothing to flag.
    /// </summary>
    public MigrationManifestPlanDiagnostic[] Diagnostics { get; init; } = [];

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
/// One classified planning entry within a render-only service migration plan.
/// </summary>
public sealed record MigrationManifestPlanEntry
{
    /// <summary>
    /// Stable plan-local identifier for the entry.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Source artifact identifier this entry was derived from (resource, style, or dependency id).
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Source item kind, such as <c>render-layer</c>, <c>tile-layer</c>,
    /// <c>wms-style</c>, <c>wmts-style</c>, <c>tile-matrix-set</c>, or
    /// <c>ogc-endpoint</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Plan category, such as <c>layer-metadata</c>, <c>style</c>,
    /// <c>tile-set</c>, or <c>render-endpoint</c>.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>
    /// Automation status: <c>automated</c>, <c>assisted</c>,
    /// <c>manual-review</c>, or <c>unsupported</c>.
    /// </summary>
    public required string AutomationStatus { get; init; }

    /// <summary>
    /// Stable machine-readable code for this entry's disposition.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Optional source display name for the entry.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Human-readable reason for the assigned automation status.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Operator remediation guidance for assisted, manual-review, or unsupported entries.
    /// </summary>
    public string[] ManualSteps { get; init; } = [];

    /// <summary>
    /// Deterministic planning metadata (no raw style or capabilities document text).
    /// </summary>
    public Dictionary<string, string> Metadata { get; init; } = [];
}

/// <summary>
/// One diagnostic captured during render-only migration planning.
/// </summary>
public sealed record MigrationManifestPlanDiagnostic
{
    /// <summary>
    /// Source artifact identifier the diagnostic relates to.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Stable machine-readable diagnostic code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Diagnostic severity, such as <c>info</c>, <c>warning</c>, or <c>error</c>.
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Diagnostic message describing the captured detail.
    /// </summary>
    public required string Message { get; init; }
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
