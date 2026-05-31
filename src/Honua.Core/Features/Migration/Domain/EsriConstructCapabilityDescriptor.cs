// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Capability metadata for one Esri construct category consumed by the import
/// fidelity classifier and surfaced to the facade, GP, and reporting lanes.
/// </summary>
/// <remarks>
/// The descriptor pairs the happy-path classification (tier, code, reason,
/// manual steps) with an optional conditional fallback used when the
/// construct is present but a required runtime condition is not satisfied.
/// Capability flags advertise whether an automated transform exists, whether
/// the construct can be served after import, and whether an operator check
/// is always required.
/// </remarks>
public sealed record EsriConstructCapabilityDescriptor
{
    /// <summary>Registry lookup key (e.g. <c>resource.identity</c>, <c>resource.capabilities</c>).</summary>
    public required string ConstructKey { get; init; }

    /// <summary>
    /// Automation status applied when the construct is supported.
    /// One of <see cref="MigrationFidelityAutomationStatuses"/>.
    /// </summary>
    public required string AutomationStatus { get; init; }

    /// <summary>Compatibility code applied when the construct is supported.</summary>
    public required string Code { get; init; }

    /// <summary>Human-readable reason emitted when the construct is supported.</summary>
    public required string Reason { get; init; }

    /// <summary>Operator follow-up steps emitted when the construct is supported.</summary>
    public string[] ManualSteps { get; init; } = [];

    /// <summary>
    /// Automation status applied when a required runtime condition is not met.
    /// Leave <see langword="null"/> for constructs whose tier is unconditional.
    /// </summary>
    public string? UnsupportedAutomationStatus { get; init; }

    /// <summary>Compatibility code applied when the required condition is not met.</summary>
    public string? UnsupportedCode { get; init; }

    /// <summary>Reason emitted when the required condition is not met.</summary>
    public string? UnsupportedReason { get; init; }

    /// <summary>Operator follow-up steps emitted when the required condition is not met.</summary>
    public string[] UnsupportedManualSteps { get; init; } = [];

    /// <summary>True when an automated transform/import path exists for this construct.</summary>
    public bool CanTransform { get; init; }

    /// <summary>True when this construct can be served from Honua after import.</summary>
    public bool CanServe { get; init; }

    /// <summary>True when an explicit operator check is required even in the happy path.</summary>
    public bool RequiresCheck { get; init; }

    /// <summary>Internal notes for maintainers and reviewers; not surfaced in fidelity records.</summary>
    public string? Notes { get; init; }
}
