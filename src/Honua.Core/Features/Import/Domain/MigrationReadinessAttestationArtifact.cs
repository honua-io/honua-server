// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Stable readiness statuses used by the cutover/readiness attestation produced by the migration
/// acceptance suite.
/// </summary>
public static class MigrationReadinessStatuses
{
    /// <summary>The source is safe to cut over.</summary>
    public const string Ready = "ready";

    /// <summary>
    /// The source can cut over only after operator review closes the cited manual-review or
    /// warning conditions.
    /// </summary>
    public const string Conditional = "conditional";

    /// <summary>The source is not safe to cut over.</summary>
    public const string NotReady = "not-ready";
}

/// <summary>
/// Stable severities for readiness reasons emitted by the readiness stage.
/// </summary>
public static class MigrationReadinessReasonSeverities
{
    /// <summary>Informational reason that does not by itself block readiness.</summary>
    public const string Info = "info";

    /// <summary>Reason that downgrades readiness to <c>conditional</c>.</summary>
    public const string ManualReview = "manual-review";

    /// <summary>Reason that warns about a non-fatal divergence (e.g. parity warn).</summary>
    public const string Warn = "warn";

    /// <summary>Reason that downgrades readiness to <c>not-ready</c>.</summary>
    public const string Fail = "fail";
}

/// <summary>
/// Per-source cutover/readiness attestation emitted by the migration acceptance readiness stage.
/// Says whether the source is safe to cut over (<c>ready</c>, <c>conditional</c>, or
/// <c>not-ready</c>), the deterministic reasons for that classification, and the cited evidence
/// artifact hashes (scan inventory + apply manifest + parity evidence) the attestation was derived
/// from.
/// </summary>
/// <remarks>
/// <para>
/// This artifact is the final per-source output of the acceptance suite described in issue #1024.
/// Downstream cutover gates consume it instead of re-deriving readiness from the upstream stage
/// reports, so the cited <see cref="MigrationReadinessEvidenceCitation"/> entries are the
/// authoritative record of which scan / apply / parity artifacts (by SHA-256 hash) supported the
/// classification.
/// </para>
/// </remarks>
public sealed record MigrationReadinessAttestationArtifact
{
    /// <summary>
    /// Stable artifact kind identifier.
    /// </summary>
    public string ArtifactKind { get; init; } = "honua.migration.readiness-attestation";

    /// <summary>
    /// Artifact schema version.
    /// </summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>
    /// Stable fixture identifier the attestation refers to.
    /// </summary>
    public required string FixtureId { get; init; }

    /// <summary>
    /// Source kind such as <c>arcgis-geoservices-rest</c>, <c>geoserver-rest</c>, or
    /// <c>ogc-api-features</c>.
    /// </summary>
    public required string SourceKind { get; init; }

    /// <summary>
    /// Aggregate readiness status: <c>ready</c>, <c>conditional</c>, or <c>not-ready</c>. See
    /// <see cref="MigrationReadinessStatuses"/> for the canonical values.
    /// </summary>
    public required string Status { get; init; }

    /// <summary>
    /// Human-readable summary suitable for operator review. Must not contain credentials.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Stable SHA-256 fingerprint over the readiness payload (status, reasons, citation hashes).
    /// Identical across re-runs of the same fixture set.
    /// </summary>
    public required string ReplayToken { get; init; }

    /// <summary>
    /// Per-source reasons that drove the classification. Ordered deterministically by severity
    /// then code so re-runs of the same inputs produce identical attestation payloads.
    /// </summary>
    public MigrationReadinessReason[] Reasons { get; init; } = [];

    /// <summary>
    /// Cited evidence artifacts (scan inventory, apply manifest, parity evidence pack) the
    /// attestation was derived from. Each citation carries an artifact kind plus a deterministic
    /// SHA-256 hash so cutover gates can verify the attestation against the same artifacts.
    /// </summary>
    public MigrationReadinessEvidenceCitation[] EvidenceCitations { get; init; } = [];
}

/// <summary>
/// One reason recorded by the readiness stage. Reasons surface the deterministic conditions that
/// downgraded readiness from <c>ready</c> (e.g. parity probe <c>fail</c>, apply manual-review
/// item, scan diagnostic).
/// </summary>
public sealed record MigrationReadinessReason
{
    /// <summary>
    /// Stable machine-readable reason code such as <c>readiness.parity.fail</c>,
    /// <c>readiness.apply.manual-review</c>, or <c>readiness.scan.diagnostic</c>.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Reason severity: <c>info</c>, <c>warn</c>, <c>manual-review</c>, or <c>fail</c>. See
    /// <see cref="MigrationReadinessReasonSeverities"/> for the canonical values.
    /// </summary>
    public required string Severity { get; init; }

    /// <summary>
    /// Upstream stage the reason was derived from: <c>scan</c>, <c>apply</c>, or <c>parity</c>.
    /// </summary>
    public required string Stage { get; init; }

    /// <summary>
    /// Optional source manifest item identifier the reason refers to.
    /// </summary>
    public string? SourceId { get; init; }

    /// <summary>
    /// Human-readable reason message. Must not contain credentials or other secrets.
    /// </summary>
    public required string Message { get; init; }
}

/// <summary>
/// One cited evidence artifact backing a readiness attestation. Pairs the stable artifact kind
/// with the deterministic SHA-256 hash of the artifact payload so cutover gates can verify the
/// attestation against the same artifacts.
/// </summary>
public sealed record MigrationReadinessEvidenceCitation
{
    /// <summary>
    /// Upstream stage that emitted the cited artifact: <c>scan</c>, <c>apply</c>, or
    /// <c>parity</c>.
    /// </summary>
    public required string Stage { get; init; }

    /// <summary>
    /// Artifact kind such as <c>honua.migration.source-inventory</c>,
    /// <c>honua.migration.manifest</c>, or <c>honua.migration.parity-evidence-pack</c>.
    /// </summary>
    public required string ArtifactKind { get; init; }

    /// <summary>
    /// Stable SHA-256 fingerprint of the cited artifact payload, in the same
    /// <c>sha256:&lt;hex&gt;</c> shape used by the parity stage replay token.
    /// </summary>
    public required string ArtifactHash { get; init; }

    /// <summary>
    /// Optional upstream replay token (e.g. parity replay token) the citation pairs with.
    /// </summary>
    public string? ReplayToken { get; init; }
}
