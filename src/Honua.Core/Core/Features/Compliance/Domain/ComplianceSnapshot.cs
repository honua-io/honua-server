// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

public sealed record ComplianceSnapshot
{
    public required DateTimeOffset GeneratedAt { get; init; }

    public required string ServerVersion { get; init; }

    public required IReadOnlyList<ComplianceControlEvidenceRow> Controls { get; init; }

    public required EncryptionPosture Encryption { get; init; }

    public required DataResidencyPolicy Residency { get; init; }

    public required ComplianceReadinessSummary Summary { get; init; }
}

public sealed record ComplianceControlEvidenceRow
{
    public required ComplianceControl Control { get; init; }

    public required ComplianceControlStatus Status { get; init; }

    public required IReadOnlyList<ComplianceEvidence> Evidence { get; init; }

    public required IReadOnlyList<string> Gaps { get; init; }
}

public sealed record ComplianceReadinessSummary
{
    public required int Implemented { get; init; }

    public required int PartiallyImplemented { get; init; }

    public required int NotImplemented { get; init; }

    public required int NotApplicable { get; init; }

    public required int Unknown { get; init; }

    public required double ReadinessPercent { get; init; }
}
