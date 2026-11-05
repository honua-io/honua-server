// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

public sealed class ComplianceDashboardResponse
{
    [JsonPropertyName("generatedAt")]
    public required DateTimeOffset GeneratedAt { get; init; }

    [JsonPropertyName("serverVersion")]
    public required string ServerVersion { get; init; }

    [JsonPropertyName("summary")]
    public required ComplianceSummaryView Summary { get; init; }

    [JsonPropertyName("encryption")]
    public required ComplianceEncryptionView Encryption { get; init; }

    [JsonPropertyName("residency")]
    public required ComplianceResidencyView Residency { get; init; }

    [JsonPropertyName("controls")]
    public required IReadOnlyList<ComplianceControlView> Controls { get; init; }
}

public sealed class ComplianceSummaryView
{
    [JsonPropertyName("implemented")]
    public required int Implemented { get; init; }

    [JsonPropertyName("partiallyImplemented")]
    public required int PartiallyImplemented { get; init; }

    [JsonPropertyName("notImplemented")]
    public required int NotImplemented { get; init; }

    [JsonPropertyName("notApplicable")]
    public required int NotApplicable { get; init; }

    [JsonPropertyName("unknown")]
    public required int Unknown { get; init; }

    [JsonPropertyName("readinessPercent")]
    public required double ReadinessPercent { get; init; }
}

// These rotation counters are audit evidence and are not cipher key material.
public sealed class ComplianceEncryptionView
{
    [JsonPropertyName("fipsMode")]
    public required bool FipsMode { get; init; }

    [JsonPropertyName("fipsSource")]
    public required string FipsSource { get; init; }

    [JsonPropertyName("algorithms")]
    public required IReadOnlyList<string> Algorithms { get; init; }

    [JsonPropertyName("activeKeyVersion")]
    public required int ActiveKeyVersion { get; init; }

    [JsonPropertyName("retainedKeyVersions")]
    public required int RetainedKeyVersions { get; init; }

    [JsonPropertyName("activeKeyIssuedAt")]
    public DateTimeOffset? ActiveKeyIssuedAt { get; init; }

    [JsonPropertyName("lastRotationAt")]
    public DateTimeOffset? LastRotationAt { get; init; }
}

public sealed class ComplianceResidencyView
{
    [JsonPropertyName("enforced")]
    public required bool Enforced { get; init; }

    [JsonPropertyName("primaryRegion")]
    public required string PrimaryRegion { get; init; }

    [JsonPropertyName("allowedRegions")]
    public required IReadOnlyList<string> AllowedRegions { get; init; }
}

public sealed class ComplianceControlView
{
    [JsonPropertyName("framework")]
    public required string Framework { get; init; }

    [JsonPropertyName("controlId")]
    public required string ControlId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("relatedControls")]
    public required IReadOnlyList<string> RelatedControls { get; init; }

    [JsonPropertyName("gaps")]
    public required IReadOnlyList<string> Gaps { get; init; }

    [JsonPropertyName("evidence")]
    public required IReadOnlyList<ComplianceEvidenceView> Evidence { get; init; }
}

public sealed class ComplianceEvidenceView
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("collectedAt")]
    public required DateTimeOffset CollectedAt { get; init; }

    [JsonPropertyName("claim")]
    public required string Claim { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("detail")]
    public required string Detail { get; init; }
}

public sealed class ComplianceResidencyEvaluationRequest
{
    [JsonPropertyName("region")]
    public string? Region { get; init; }
}

public sealed class ComplianceResidencyEvaluationResponse
{
    [JsonPropertyName("allowed")]
    public required bool Allowed { get; init; }

    [JsonPropertyName("region")]
    public required string Region { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("policy")]
    public required ComplianceResidencyView Policy { get; init; }
}

public sealed class ComplianceKeyRotationResponse
{
    [JsonPropertyName("succeeded")]
    public required bool Succeeded { get; init; }

    [JsonPropertyName("previousVersion")]
    public required int PreviousVersion { get; init; }

    [JsonPropertyName("newVersion")]
    public required int NewVersion { get; init; }

    [JsonPropertyName("rotatedAt")]
    public required DateTimeOffset RotatedAt { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("auditRecorded")]
    public required bool AuditRecorded { get; init; }
}
