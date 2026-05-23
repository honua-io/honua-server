// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Compliance.Domain;

/// <summary>
/// A single evidence record attached to a compliance control. Evidence is collected
/// from server state (configuration, audit log probes, encryption posture) — it is
/// never user-authored, so callers don't need a separate authoring API.
/// </summary>
public sealed record ComplianceEvidence
{
    /// <summary>The control this evidence supports.</summary>
    public required string ControlId { get; init; }

    /// <summary>
    /// When the evidence was collected. Use UTC so reports round-trip through CSV/PDF
    /// without timezone drift between auditor and customer.
    /// </summary>
    public required DateTimeOffset CollectedAt { get; init; }

    /// <summary>
    /// Source of the evidence (e.g. <c>configuration</c>, <c>audit-log</c>, <c>self-test</c>).
    /// Used to group rows in CSV exports.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>One-line claim the evidence substantiates.</summary>
    public required string Claim { get; init; }

    /// <summary>
    /// Status implied by this evidence row. The control's overall status is the rollup
    /// across all evidence rows (worst-case wins for gaps).
    /// </summary>
    public required ComplianceControlStatus Status { get; init; }

    /// <summary>
    /// Optional supporting detail (configuration values, counts, audit query summary).
    /// Pre-sanitized text — the collector is responsible for redacting secrets.
    /// </summary>
    public string Detail { get; init; } = string.Empty;
}
