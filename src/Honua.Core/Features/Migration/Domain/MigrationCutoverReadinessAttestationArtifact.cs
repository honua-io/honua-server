// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Deterministic cutover-readiness attestation emitted by the migration
/// acceptance evidence suite. One artifact is produced per fixture run and
/// summarizes the fidelity classification counts the acceptance gate inspects
/// before allowing a release-level migration claim.
/// </summary>
/// <remarks>
/// This artifact is intentionally minimal. It is the contract that ties the
/// release-gated workflow runs (see <c>migration-acceptance.yml</c>) back to
/// the per-source artifact chain produced by the upstream import slices.
/// Schema changes here are observed by the artifact schema stability test
/// suite and must be co-ordinated with the workflow consumers.
/// </remarks>
public sealed record MigrationCutoverReadinessAttestationArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.cutover-readiness-attestation";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Stable run identifier supplied by the workflow or release gate
    /// (for example a GitHub Actions <c>run_id</c>).
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>
    /// Stable source identifier whose fidelity is being attested. This is
    /// typically the source-inventory artifact's <c>source.displayName</c>
    /// slug or a workflow-supplied identifier.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Source kind identifier such as <c>geoserver-rest</c> or
    /// <c>arcgis-geoservices-rest</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Stable fixture identifier the attestation was generated against
    /// (for example <c>geoserver-public-pilot</c> or
    /// <c>arcgis-anonymous-baseline</c>).
    /// </summary>
    public required string FixtureName { get; init; }

    /// <summary>
    /// UTC timestamp the attestation was generated. Stored as a normalized
    /// ISO-8601 string so the artifact remains stable across serializers.
    /// </summary>
    public required string GeneratedAtUtc { get; init; }

    /// <summary>
    /// Aggregate fidelity classification counts inspected by the acceptance
    /// suite gate.
    /// </summary>
    public required MigrationCutoverReadinessClassificationCounts ClassificationCounts { get; init; }
}

/// <summary>
/// Aggregate fidelity classification counts for a migration cutover-readiness
/// attestation. Counts mirror the automation statuses defined by
/// <see cref="MigrationFidelityAutomationStatuses"/>.
/// </summary>
public sealed record MigrationCutoverReadinessClassificationCounts
{
    /// <summary>
    /// Number of source classifications that can be carried by the current
    /// automated migration path.
    /// </summary>
    public int Automated { get; init; }

    /// <summary>
    /// Number of source classifications that need assisted operator input
    /// after import.
    /// </summary>
    public int Assisted { get; init; }

    /// <summary>
    /// Number of source classifications captured only for explicit operator
    /// review.
    /// </summary>
    public int ManualReview { get; init; }

    /// <summary>
    /// Number of source classifications that are unsupported by this
    /// migration slice.
    /// </summary>
    public int Unsupported { get; init; }
}
