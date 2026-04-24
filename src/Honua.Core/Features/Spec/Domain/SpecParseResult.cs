// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Result of a parse call. Parsing is diagnostic-collecting — even when
/// <see cref="Document"/> is non-null, callers must check
/// <see cref="Diagnostics"/> for errors (recovery emits a partial AST).
/// </summary>
/// <param name="Document">Partial or complete AST; <c>null</c> only when parsing failed before any top-level node could be recovered.</param>
/// <param name="Diagnostics">Diagnostics accumulated across the lex + parse passes.</param>
public sealed record SpecParseResult(
    SpecDocument? Document,
    IReadOnlyList<SpecDiagnostic> Diagnostics)
{
    /// <summary>
    /// <c>true</c> when no diagnostic has <see cref="SpecDiagnosticSeverity.Error"/> severity.
    /// </summary>
    public bool IsSuccess
    {
        get
        {
            foreach (var diagnostic in Diagnostics)
            {
                if (diagnostic.Severity == SpecDiagnosticSeverity.Error)
                {
                    return false;
                }
            }

            return Document is not null;
        }
    }
}

/// <summary>
/// Result of a validate call.
/// </summary>
/// <param name="Diagnostics">All diagnostics produced by the resolve/type-check/semantic passes.</param>
public sealed record SpecValidationResult(IReadOnlyList<SpecDiagnostic> Diagnostics)
{
    /// <summary>
    /// <c>true</c> when no diagnostic has <see cref="SpecDiagnosticSeverity.Error"/> severity.
    /// </summary>
    public bool IsValid
    {
        get
        {
            foreach (var diagnostic in Diagnostics)
            {
                if (diagnostic.Severity == SpecDiagnosticSeverity.Error)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
