// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Deterministic envelope produced by the post-publish catalog reconciliation pass. The migration
/// acceptance parity stage (<see cref="MigrationParityStageReport"/>) keeps an intentionally small
/// probe set (feature count, schema field-names, axis-aligned bbox overlap) sufficient for the
/// dry-run acceptance suite. This reconciliation report goes further by comparing the published
/// <see cref="MetadataV2Resource"/> graph entry against the source service definition captured in
/// the <see cref="MigrationSourceInventoryArtifact"/>: per-field name+type, geometry type, declared
/// SRID, declared extent, ObjectID/GlobalID identifier semantic-roles, and the persistence of
/// inventory-captured domains and manifest-captured relationships.
/// </summary>
/// <remarks>
/// Counts can match while the schema is wrong (see issue #1248). This report captures those deeper
/// mismatches per layer with a stable finding code, expected value, and actual value so operators
/// can decide whether to roll a publish back or accept it.
/// </remarks>
public sealed record MigrationCatalogReconciliationReport
{
    /// <summary>Stable artifact kind identifier.</summary>
    public string ArtifactKind { get; init; } = "honua.migration.catalog-reconciliation-report";

    /// <summary>Artifact schema version.</summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>Stable identifier for this reconciliation run.</summary>
    public required string RunId { get; init; }

    /// <summary>Source kind such as <c>arcgis-geoservices-rest</c> or <c>geoserver-rest</c>.</summary>
    public required string SourceKind { get; init; }

    /// <summary>Aggregate counts across all per-resource reconciliation outcomes.</summary>
    public required MigrationCatalogReconciliationSummary Summary { get; init; }

    /// <summary>
    /// Per-resource reconciliation outcomes, ordered deterministically by
    /// <see cref="MigrationCatalogReconciliationResource.SourceResourceId"/>.
    /// </summary>
    public MigrationCatalogReconciliationResource[] Resources { get; init; } = [];
}

/// <summary>
/// Aggregate counts rolled up across all <see cref="MigrationCatalogReconciliationResource"/>
/// entries in a <see cref="MigrationCatalogReconciliationReport"/>.
/// </summary>
public sealed record MigrationCatalogReconciliationSummary
{
    /// <summary>Total number of source resources reconciled.</summary>
    public int ResourceCount { get; init; }

    /// <summary>Number of resources classified as <c>pass</c> (no findings).</summary>
    public int PassResourceCount { get; init; }

    /// <summary>Number of resources classified as <c>warn</c> (warn findings only).</summary>
    public int WarnResourceCount { get; init; }

    /// <summary>Number of resources classified as <c>fail</c> (at least one fail finding).</summary>
    public int FailResourceCount { get; init; }

    /// <summary>
    /// Number of resources classified as <c>not-applicable</c> because the reconciler had no
    /// published catalog entry to compare against (e.g. publish skipped or the source resource was
    /// routed to manual review).
    /// </summary>
    public int NotApplicableResourceCount { get; init; }

    /// <summary>Total number of findings emitted across all per-resource outcomes.</summary>
    public int FindingCount { get; init; }
}

/// <summary>
/// One per-source-resource reconciliation outcome.
/// </summary>
public sealed record MigrationCatalogReconciliationResource
{
    /// <summary>Source inventory resource identifier.</summary>
    public required string SourceResourceId { get; init; }

    /// <summary>
    /// Target Honua resource identifier when a publish target was supplied (mirrors the manifest
    /// target resource id when known).
    /// </summary>
    public string? TargetResourceId { get; init; }

    /// <summary>
    /// Aggregate per-resource classification: <c>pass</c>, <c>warn</c>, <c>fail</c>, or
    /// <c>not-applicable</c>. Mirrors the highest severity finding for the resource.
    /// </summary>
    public required string Classification { get; init; }

    /// <summary>
    /// Findings emitted for this resource, ordered deterministically by
    /// <see cref="MigrationCatalogReconciliationFinding.Code"/> then
    /// <see cref="MigrationCatalogReconciliationFinding.Subject"/>.
    /// </summary>
    public MigrationCatalogReconciliationFinding[] Findings { get; init; } = [];
}

/// <summary>
/// One reconciliation finding emitted by the catalog reconciler.
/// </summary>
public sealed record MigrationCatalogReconciliationFinding
{
    /// <summary>
    /// Stable, machine-readable finding code. See <see cref="MigrationCatalogReconciliationCodes"/>
    /// for the canonical set.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Finding severity: <see cref="MigrationCatalogReconciliationSeverities.Fail"/>,
    /// <see cref="MigrationCatalogReconciliationSeverities.Warn"/>, or
    /// <see cref="MigrationCatalogReconciliationSeverities.Info"/>.
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Optional subject the finding refers to inside the resource (e.g. a field name,
    /// <c>spatial</c>, <c>identifier</c>, or a relationship identifier). Used to disambiguate
    /// multiple findings with the same <see cref="Code"/>.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>Optional expected value (sourced from the inventory or the manifest).</summary>
    public string? Expected { get; init; }

    /// <summary>Optional observed value (sourced from the published catalog entry).</summary>
    public string? Actual { get; init; }

    /// <summary>Human-readable summary suitable for operator review.</summary>
    public required string Summary { get; init; }
}

/// <summary>
/// Per-resource input to the catalog reconciler. Pairs a source inventory resource with the
/// published catalog entry that the migration apply stage materialized (when one was published).
/// Relationship records emitted by manifest translation are passed alongside so the reconciler can
/// verify each captured relationship was persisted onto the published resource.
/// </summary>
public sealed record MigrationCatalogReconciliationInput
{
    /// <summary>Source inventory resource captured during the scan stage.</summary>
    public required MigrationInventoryResource Resource { get; init; }

    /// <summary>
    /// Published Metadata v2 catalog entry observed after apply. <c>null</c> when the source
    /// resource was routed to manual review or otherwise was not published; the reconciler emits a
    /// single <see cref="MigrationCatalogReconciliationCodes.ResourceMissing"/> finding in that
    /// case.
    /// </summary>
    public MetadataV2Resource? PublishedResource { get; init; }

    /// <summary>
    /// Relationship records captured during manifest translation for this source resource. Used to
    /// verify each captured relationship was persisted onto the published catalog entry. Defaults
    /// to an empty array so callers that did not capture any relationships do not have to thread an
    /// optional through.
    /// </summary>
    public MigrationManifestRelationshipRecord[] ExpectedRelationships { get; init; } = [];

    /// <summary>
    /// Optional axis-aligned source extent in the inventory's declared CRS, as
    /// <c>[minX, minY, maxX, maxY]</c>. Inventory does not carry a typed bbox on the resource
    /// itself (the source-specific extent metadata is captured in the scan-stage source documents);
    /// callers that have the source extent supply it here so the reconciler can probe extent
    /// parity. <c>null</c> skips the extent probe.
    /// </summary>
    public double[]? SourceBbox { get; init; }

    /// <summary>
    /// Optional target resource identifier (e.g. from the manifest's identity record) recorded
    /// alongside each outcome so reports can cross-reference the catalog entry.
    /// </summary>
    public string? TargetResourceId { get; init; }
}

/// <summary>
/// Stable finding codes emitted by <see cref="Services.MigrationCatalogReconciler"/>.
/// </summary>
public static class MigrationCatalogReconciliationCodes
{
    /// <summary>The source resource has no published catalog entry to reconcile against.</summary>
    public const string ResourceMissing = "catalog.resource.missing";

    /// <summary>An inventory field is missing from the published resource schema.</summary>
    public const string FieldMissing = "catalog.field.missing";

    /// <summary>An inventory field's type does not match the published canonical field type.</summary>
    public const string FieldTypeMismatch = "catalog.field.type-mismatch";

    /// <summary>An inventory field carried a domain but the published field has no domain attached.</summary>
    public const string DomainMissing = "catalog.field.domain-missing";

    /// <summary>The published domain name differs from the inventory-captured domain name.</summary>
    public const string DomainNameMismatch = "catalog.field.domain-name-mismatch";

    /// <summary>Coded values captured at inventory time are not all present on the published domain.</summary>
    public const string DomainValuesMismatch = "catalog.field.domain-values-mismatch";

    /// <summary>The published domain type (e.g. <c>codedValue</c> vs <c>range</c>) differs from the inventory-captured domain type.</summary>
    public const string DomainTypeMismatch = "catalog.field.domain-type-mismatch";

    /// <summary>The published range-domain bounds differ from the inventory-captured bounds.</summary>
    public const string DomainRangeMismatch = "catalog.field.domain-range-mismatch";

    /// <summary>The inventory captured a coded-value domain over the capture cap; the published catalog intentionally omits the values to stay consistent with the inventory artifact.</summary>
    public const string DomainTruncated = "catalog.field.domain-truncated";

    /// <summary>The inventory truncated a coded-value domain but the published catalog still exposes values, violating the truncation parity contract.</summary>
    public const string DomainTruncationMismatch = "catalog.field.domain-truncation-mismatch";

    /// <summary>The published geometry type differs from the inventory geometry type.</summary>
    public const string GeometryTypeMismatch = "catalog.spatial.geometry-type-mismatch";

    /// <summary>The published SRID differs from the inventory-declared SRID.</summary>
    public const string SridMismatch = "catalog.spatial.srid-mismatch";

    /// <summary>The published resource advertises no extent even though the inventory did.</summary>
    public const string ExtentMissing = "catalog.spatial.extent-missing";

    /// <summary>The published extent is disjoint from the inventory-declared extent.</summary>
    public const string ExtentMismatch = "catalog.spatial.extent-mismatch";

    /// <summary>The published schema has no field carrying the primary identifier semantic role.</summary>
    public const string ObjectIdMissing = "catalog.identifier.object-id-missing";

    /// <summary>
    /// The inventory advertised a GlobalID-style identifier but the published resource has no
    /// matching <c>Editing.GlobalIdField</c> binding.
    /// </summary>
    public const string GlobalIdMissing = "catalog.identifier.global-id-missing";

    /// <summary>A relationship captured at manifest time is missing from the published resource.</summary>
    public const string RelationshipMissing = "catalog.relationship.missing";
}

/// <summary>
/// Stable severity values used by <see cref="MigrationCatalogReconciliationFinding.Severity"/>.
/// </summary>
public static class MigrationCatalogReconciliationSeverities
{
    /// <summary>Mismatch breaks the published catalog contract for this resource.</summary>
    public const string Fail = "fail";

    /// <summary>Mismatch deserves operator review but is not necessarily a publish blocker.</summary>
    public const string Warn = "warn";

    /// <summary>Informational finding that should not gate a publish.</summary>
    public const string Info = "info";
}

/// <summary>
/// Stable classification values used by
/// <see cref="MigrationCatalogReconciliationResource.Classification"/> and rolled up into
/// <see cref="MigrationCatalogReconciliationSummary"/>.
/// </summary>
public static class MigrationCatalogReconciliationClassifications
{
    /// <summary>No findings; the published catalog entry matches the source service definition.</summary>
    public const string Pass = "pass";

    /// <summary>At least one warn-severity finding but no fail-severity findings.</summary>
    public const string Warn = "warn";

    /// <summary>At least one fail-severity finding.</summary>
    public const string Fail = "fail";

    /// <summary>
    /// No published catalog entry was supplied (e.g. publish skipped because the manifest routed
    /// the resource to manual review).
    /// </summary>
    public const string NotApplicable = "not-applicable";
}
