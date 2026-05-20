// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Website-linkable migration performance and cost evidence artifact emitted by the
/// release-gated workflow for issue #1033 slice 4.
/// </summary>
/// <remarks>
/// <para>
/// Bundles the slice-1 <see cref="MigrationRunMetricsArtifact"/> (raw measurements),
/// the slice-2 <see cref="MigrationRunMetricsBaselineArtifact"/> (Pass/Warn/Fail
/// classification against a baseline), the fixture metadata used to size the run, a
/// SHA-256 fingerprint over a canonical view of the inputs, and a redaction posture
/// proving the artifact intentionally excludes source URLs, credentials, and source
/// data.
/// </para>
/// <para>
/// The fingerprint lets release reviewers prove that the published artifact summarizes
/// the same measurements and baseline that the workflow actually evaluated. Building
/// the same inputs twice produces byte-identical output (modulo the
/// <c>GeneratedAt</c> timestamp the harness controls).
/// </para>
/// </remarks>
public sealed record MigrationPerformanceEvidenceArtifact
{
    /// <summary>Stable artifact kind identifier.</summary>
    public string ArtifactKind { get; init; } = "honua.migration.performance-evidence";

    /// <summary>Artifact schema version.</summary>
    public string ArtifactVersion { get; init; } = "1.0";

    /// <summary>Issue number this artifact is published for (#1033 slice 4).</summary>
    public string IssueReference { get; init; } = "honua-server#1033";

    /// <summary>Stable source family identifier (see <see cref="MigrationCostPerformanceSourceFamilies"/>).</summary>
    public required string SourceFamily { get; init; }

    /// <summary>Fixture size identifier (see <see cref="MigrationCostPerformanceFixtureSizes"/>).</summary>
    public required string FixtureSize { get; init; }

    /// <summary>Baseline profile name used to classify the run.</summary>
    public required string BaselineProfile { get; init; }

    /// <summary>Aggregate run status (<c>Pass</c>, <c>Warn</c>, or <c>Fail</c>).</summary>
    public required string Status { get; init; }

    /// <summary>Reviewer-facing one-line summary of the run.</summary>
    public required string Summary { get; init; }

    /// <summary>Optional run identifier copied from the slice-1 metrics artifact.</summary>
    public string? RunId { get; init; }

    /// <summary>Measurement scope copied from the slice-1 metrics artifact.</summary>
    public required string MeasurementScope { get; init; }

    /// <summary>Wall-clock instant the artifact was emitted by the builder.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Expected fixture envelope used to size the baseline.</summary>
    public required MigrationFixtureSizeProfile FixtureProfile { get; init; }

    /// <summary>Slice-1 raw run metrics included verbatim for traceability.</summary>
    public required MigrationRunMetricsArtifact RunMetrics { get; init; }

    /// <summary>Slice-2 baseline classification result included verbatim for traceability.</summary>
    public required MigrationRunMetricsBaselineArtifact BaselineEvaluation { get; init; }

    /// <summary>
    /// SHA-256 fingerprint computed over the canonical JSON of the run metrics and
    /// baseline evaluation. Hex-encoded lowercase, prefixed with <c>sha256:</c>.
    /// </summary>
    public required string Fingerprint { get; init; }

    /// <summary>Redaction posture for the bundled artifact.</summary>
    public required MigrationPerformanceEvidenceRedactionPosture Redaction { get; init; }
}

/// <summary>
/// Redaction posture for a <see cref="MigrationPerformanceEvidenceArtifact"/>.
/// </summary>
/// <remarks>
/// Slice 4 enforces a deny-by-default posture: URLs, credentials, source feature
/// payloads, and operator-identifying values are never present. The posture is a
/// first-class artifact field so reviewers can confirm it without re-reading the
/// builder source.
/// </remarks>
public sealed record MigrationPerformanceEvidenceRedactionPosture
{
    /// <summary>Always <c>false</c>; source URLs are stripped before emission.</summary>
    public bool SourceUrlsIncluded { get; init; }

    /// <summary>Always <c>false</c>; credential values are stripped before emission.</summary>
    public bool CredentialValuesIncluded { get; init; }

    /// <summary>Always <c>false</c>; source feature/coverage payloads are never bundled.</summary>
    public bool SourceDataIncluded { get; init; }

    /// <summary>Always <c>false</c>; operator-identifying values are stripped before emission.</summary>
    public bool OperatorIdentitiesIncluded { get; init; }

    /// <summary>Deterministic list of field categories intentionally omitted from the artifact.</summary>
    public string[] OmittedFields { get; init; } = [];

    /// <summary>Short reviewer-facing explanation of the posture.</summary>
    public required string Summary { get; init; }
}
