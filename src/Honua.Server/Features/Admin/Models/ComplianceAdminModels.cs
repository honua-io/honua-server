// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Dashboard response listing every compliance control with its current status and
/// the evidence backing it. Shape stays flat (no nested optional fields) so the
/// Admin UI binder is straightforward.
/// </summary>
public sealed class ComplianceDashboardResponse
{
    /// <summary>UTC timestamp the snapshot was assembled (ISO 8601).</summary>
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Server version (informational version + commit) the snapshot reports against.</summary>
    [JsonPropertyName("serverVersion")]
    public required string ServerVersion { get; init; }

    /// <summary>Aggregate readiness summary (counts per status, readiness percent).</summary>
    [JsonPropertyName("summary")]
    public required ComplianceSummaryView Summary { get; init; }

    /// <summary>Encryption-at-rest posture (FIPS mode, algorithms, key version).</summary>
    [JsonPropertyName("encryption")]
    public required ComplianceEncryptionView Encryption { get; init; }

    /// <summary>Active data residency policy.</summary>
    [JsonPropertyName("residency")]
    public required ComplianceResidencyView Residency { get; init; }

    /// <summary>Per-control evidence rows.</summary>
    [JsonPropertyName("controls")]
    public required IReadOnlyList<ComplianceControlView> Controls { get; init; }
}

/// <summary>Per-status counts and readiness percent.</summary>
public sealed class ComplianceSummaryView
{
    /// <summary>Number of fully implemented controls.</summary>
    [JsonPropertyName("implemented")]
    public required int Implemented { get; init; }

    /// <summary>Number of partially implemented controls.</summary>
    [JsonPropertyName("partiallyImplemented")]
    public required int PartiallyImplemented { get; init; }

    /// <summary>Number of not-implemented controls.</summary>
    [JsonPropertyName("notImplemented")]
    public required int NotImplemented { get; init; }

    /// <summary>Number of N/A controls (e.g. framework readiness not claimed).</summary>
    [JsonPropertyName("notApplicable")]
    public required int NotApplicable { get; init; }

    /// <summary>Number of controls with no evidence yet collected.</summary>
    [JsonPropertyName("unknown")]
    public required int Unknown { get; init; }

    /// <summary>Implemented controls as a percentage of applicable controls.</summary>
    [JsonPropertyName("readinessPercent")]
    public required double ReadinessPercent { get; init; }
}

/// <summary>Encryption-at-rest dashboard projection.</summary>
public sealed class ComplianceEncryptionView
{
    /// <summary>Whether FIPS mode is active.</summary>
    [JsonPropertyName("fipsMode")]
    public required bool FipsMode { get; init; }

    /// <summary>Source / signal used to derive <see cref="FipsMode"/>.</summary>
    [JsonPropertyName("fipsSource")]
    public required string FipsSource { get; init; }

    /// <summary>Canonical algorithm identifiers in use.</summary>
    [JsonPropertyName("algorithms")]
    public required IReadOnlyList<string> Algorithms { get; init; }

    /// <summary>Currently active encryption-at-rest key version.</summary>
    [JsonPropertyName("activeKeyVersion")]
    public required int ActiveKeyVersion { get; init; }

    /// <summary>Number of historical key versions retained for decrypting older ciphertext.</summary>
    [JsonPropertyName("retainedKeyVersions")]
    public required int RetainedKeyVersions { get; init; }

    /// <summary>When the active key was issued.</summary>
    [JsonPropertyName("activeKeyIssuedAt")]
    public DateTimeOffset? ActiveKeyIssuedAt { get; init; }

    /// <summary>When the active key was last rotated.</summary>
    [JsonPropertyName("lastRotationAt")]
    public DateTimeOffset? LastRotationAt { get; init; }
}

/// <summary>Data residency dashboard projection.</summary>
public sealed class ComplianceResidencyView
{
    /// <summary>Whether residency is enforced (vs informational-only).</summary>
    [JsonPropertyName("enforced")]
    public required bool Enforced { get; init; }

    /// <summary>Primary region for stored data.</summary>
    [JsonPropertyName("primaryRegion")]
    public required string PrimaryRegion { get; init; }

    /// <summary>Regions data may flow to (always includes the primary).</summary>
    [JsonPropertyName("allowedRegions")]
    public required IReadOnlyList<string> AllowedRegions { get; init; }
}

/// <summary>Per-control evidence projection for the dashboard.</summary>
public sealed class ComplianceControlView
{
    /// <summary>Framework identifier (<c>Soc2</c> or <c>FedRamp</c>).</summary>
    [JsonPropertyName("framework")]
    public required string Framework { get; init; }

    /// <summary>Stable control identifier (e.g. <c>soc2.cc6.1</c>).</summary>
    [JsonPropertyName("controlId")]
    public required string ControlId { get; init; }

    /// <summary>Auditor-facing title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>One-paragraph description.</summary>
    [JsonPropertyName("description")]
    public required string Description { get; init; }

    /// <summary>Roll-up status across all evidence.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Cross-references to related controls in the other framework.</summary>
    [JsonPropertyName("relatedControls")]
    public required IReadOnlyList<string> RelatedControls { get; init; }

    /// <summary>Evidence gap descriptions when status is not Implemented.</summary>
    [JsonPropertyName("gaps")]
    public required IReadOnlyList<string> Gaps { get; init; }

    /// <summary>Per-evidence rows.</summary>
    [JsonPropertyName("evidence")]
    public required IReadOnlyList<ComplianceEvidenceView> Evidence { get; init; }
}

/// <summary>Per-evidence projection.</summary>
public sealed class ComplianceEvidenceView
{
    /// <summary>Where the evidence was collected from.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>UTC timestamp of collection.</summary>
    [JsonPropertyName("collectedAt")]
    public required DateTimeOffset CollectedAt { get; init; }

    /// <summary>One-line claim.</summary>
    [JsonPropertyName("claim")]
    public required string Claim { get; init; }

    /// <summary>Status implied by this evidence row.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Optional supporting detail.</summary>
    [JsonPropertyName("detail")]
    public required string Detail { get; init; }
}

/// <summary>Request body for residency evaluation queries.</summary>
public sealed class ComplianceResidencyEvaluationRequest
{
    /// <summary>Region to evaluate against the active policy.</summary>
    [JsonPropertyName("region")]
    public string? Region { get; init; }
}

/// <summary>Response from a residency evaluation query.</summary>
public sealed class ComplianceResidencyEvaluationResponse
{
    /// <summary>Whether the region is allowed under the active policy.</summary>
    [JsonPropertyName("allowed")]
    public required bool Allowed { get; init; }

    /// <summary>Region the policy was evaluated against.</summary>
    [JsonPropertyName("region")]
    public required string Region { get; init; }

    /// <summary>Human-readable reason for the decision.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    /// <summary>The active policy view returned for context.</summary>
    [JsonPropertyName("policy")]
    public required ComplianceResidencyView Policy { get; init; }
}

/// <summary>
/// Response from a compliance key-version rotation request. The endpoint advances
/// an auditor-facing posture counter and writes an audit event; it does not
/// re-encrypt data or rotate cipher material (see <c>IConnectionEncryptionService</c>
/// for actual key-material rotation).
/// </summary>
public sealed class ComplianceKeyRotationResponse
{
    /// <summary>Whether the posture-advance event was recorded.</summary>
    [JsonPropertyName("succeeded")]
    public required bool Succeeded { get; init; }

    /// <summary>Auditor-facing posture version before the rotation event.</summary>
    [JsonPropertyName("previousVersion")]
    public required int PreviousVersion { get; init; }

    /// <summary>Auditor-facing posture version after the rotation event.</summary>
    [JsonPropertyName("newVersion")]
    public required int NewVersion { get; init; }

    /// <summary>UTC timestamp of the rotation event.</summary>
    [JsonPropertyName("rotatedAt")]
    public required DateTimeOffset RotatedAt { get; init; }

    /// <summary>Sanitized status message.</summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
