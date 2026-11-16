// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

public enum SpecDiagnosticSeverity
{
    Info,

    Warning,

    Error
}

public sealed record SpecDiagnostic(
    SpecDiagnosticCode Code,
    SpecDiagnosticSeverity Severity,
    string Message,
    SourceSpan Span,
    string? Path = null)
{
    public static SpecDiagnostic Error(SpecDiagnosticCode code, string message, SourceSpan span, string? path = null)
        => new(code, SpecDiagnosticSeverity.Error, message, span, path);

    public static SpecDiagnostic Warning(SpecDiagnosticCode code, string message, SourceSpan span, string? path = null)
        => new(code, SpecDiagnosticSeverity.Warning, message, span, path);

    public static SpecDiagnostic Info(SpecDiagnosticCode code, string message, SourceSpan span, string? path = null)
        => new(code, SpecDiagnosticSeverity.Info, message, span, path);
}
