// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Severity of a <see cref="SpecWarning"/>. Errors are structural and block
/// plan / apply; warnings inform the operator but do not stop execution.
/// </summary>
public enum SpecDiagnosticSeverity
{
    /// <summary>Informational — surfaces a non-blocking observation.</summary>
    Info,

    /// <summary>Warning — non-blocking but operator attention recommended.</summary>
    Warning,

    /// <summary>Error — structural; apply must not proceed.</summary>
    Error
}

/// <summary>
/// Structured diagnostic emitted by the plan / apply engine. Transports the
/// stable <see cref="Code"/> (one of <see cref="SpecDiagnosticCodes"/>) plus
/// an operator-facing <see cref="Message"/> and optional remediation hint.
/// </summary>
public sealed record SpecWarning
{
    /// <summary>
    /// Stable diagnostic code. Use <see cref="SpecDiagnosticCodes"/> constants.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Operator-facing message; safe for log and UI display.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Severity of the diagnostic.
    /// </summary>
    public SpecDiagnosticSeverity Severity { get; init; } = SpecDiagnosticSeverity.Warning;

    /// <summary>
    /// Optional node identifier this diagnostic applies to. Null means the
    /// diagnostic applies at document level.
    /// </summary>
    public string? NodeId { get; init; }

    /// <summary>
    /// Optional hint for remediation (e.g. <c>"pin source version with @v1"</c>).
    /// </summary>
    public string? Remedy { get; init; }
}
