// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

/// <summary>
/// A point-in-time aggregation of every control's collected evidence — the structure
/// the Admin UI compliance dashboard renders and the PDF / CSV exports serialize.
/// </summary>
public sealed record ComplianceSnapshot
{
    /// <summary>UTC timestamp the snapshot was assembled.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>
    /// Server release identifier (semantic version + commit) the snapshot was taken on.
    /// Used to correlate evidence with the deployed build.
    /// </summary>
    public required string ServerVersion { get; init; }

    /// <summary>Control-status rows for every control in the catalog.</summary>
    public required IReadOnlyList<ComplianceControlEvidenceRow> Controls { get; init; }

    /// <summary>Encryption-at-rest posture (inputs to the FedRAMP encryption control).</summary>
    public required EncryptionPosture Encryption { get; init; }

    /// <summary>Active residency policy (input to the FedRAMP / SOC 2 boundary control).</summary>
    public required DataResidencyPolicy Residency { get; init; }

    /// <summary>
    /// Aggregate readiness — number of controls in each status. Equivalent to grouping
    /// <see cref="Controls"/> by status but pre-computed so renderers stay simple.
    /// </summary>
    public required ComplianceReadinessSummary Summary { get; init; }
}

/// <summary>One row of the dashboard: a control plus the rollup of its evidence.</summary>
public sealed record ComplianceControlEvidenceRow
{
    /// <summary>The control metadata.</summary>
    public required ComplianceControl Control { get; init; }

    /// <summary>Status rollup across all evidence rows.</summary>
    public required ComplianceControlStatus Status { get; init; }

    /// <summary>Evidence rows backing this control (audit trail entries, configuration probes).</summary>
    public required IReadOnlyList<ComplianceEvidence> Evidence { get; init; }

    /// <summary>
    /// Human-readable gap descriptions when <see cref="Status"/> is not
    /// <see cref="ComplianceControlStatus.Implemented"/>. Empty when fully implemented.
    /// </summary>
    public required IReadOnlyList<string> Gaps { get; init; }
}

/// <summary>Per-status counts and overall readiness percent.</summary>
public sealed record ComplianceReadinessSummary
{
    /// <summary>Count of controls evaluated as <see cref="ComplianceControlStatus.Implemented"/>.</summary>
    public required int Implemented { get; init; }

    /// <summary>Count of controls evaluated as <see cref="ComplianceControlStatus.PartiallyImplemented"/>.</summary>
    public required int PartiallyImplemented { get; init; }

    /// <summary>Count of controls evaluated as <see cref="ComplianceControlStatus.NotImplemented"/>.</summary>
    public required int NotImplemented { get; init; }

    /// <summary>Count of controls evaluated as <see cref="ComplianceControlStatus.NotApplicable"/>.</summary>
    public required int NotApplicable { get; init; }

    /// <summary>Count of controls evaluated as <see cref="ComplianceControlStatus.Unknown"/>.</summary>
    public required int Unknown { get; init; }

    /// <summary>
    /// Implemented controls as a percentage of applicable controls (excluding N/A and Unknown).
    /// Returns 0 when the denominator is 0 to keep renderers free of edge-case branches.
    /// </summary>
    public required double ReadinessPercent { get; init; }
}
