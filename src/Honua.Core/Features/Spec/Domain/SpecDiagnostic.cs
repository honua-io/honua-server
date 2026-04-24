// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Severity of a <see cref="SpecDiagnostic"/>.
/// </summary>
public enum SpecDiagnosticSeverity
{
    /// <summary>Informational; validation still succeeds.</summary>
    Info,

    /// <summary>Best-effort warning; does not stop the pipeline.</summary>
    Warning,

    /// <summary>Hard error; stops the pipeline at the end of the current pass.</summary>
    Error
}

/// <summary>
/// Structured diagnostic emitted by the spec lexer, parser, resolver, type
/// checker, or semantic checker. All user-facing spec errors are diagnostics
/// — exceptions are reserved for internal invariant violations.
/// </summary>
/// <param name="Code">Well-known category code.</param>
/// <param name="Severity">Diagnostic severity.</param>
/// <param name="Message">Human-readable explanation (already localized to en-US).</param>
/// <param name="Span">Source location of the offending token or node.</param>
/// <param name="Path">JSON-Pointer path into the canonical AST, or <c>null</c> when syntax prevented building one.</param>
public sealed record SpecDiagnostic(
    SpecDiagnosticCode Code,
    SpecDiagnosticSeverity Severity,
    string Message,
    SourceSpan Span,
    string? Path = null)
{
    /// <summary>
    /// Convenience factory for an <see cref="SpecDiagnosticSeverity.Error"/>.
    /// </summary>
    public static SpecDiagnostic Error(SpecDiagnosticCode code, string message, SourceSpan span, string? path = null)
        => new(code, SpecDiagnosticSeverity.Error, message, span, path);

    /// <summary>
    /// Convenience factory for a <see cref="SpecDiagnosticSeverity.Warning"/>.
    /// </summary>
    public static SpecDiagnostic Warning(SpecDiagnosticCode code, string message, SourceSpan span, string? path = null)
        => new(code, SpecDiagnosticSeverity.Warning, message, span, path);

    /// <summary>
    /// Convenience factory for a <see cref="SpecDiagnosticSeverity.Info"/>.
    /// </summary>
    public static SpecDiagnostic Info(SpecDiagnosticCode code, string message, SourceSpan span, string? path = null)
        => new(code, SpecDiagnosticSeverity.Info, message, span, path);
}
