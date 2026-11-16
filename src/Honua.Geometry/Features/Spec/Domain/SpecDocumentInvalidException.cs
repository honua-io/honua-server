// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

public sealed class SpecDocumentInvalidException : Exception
{
    public SpecDocumentInvalidException(IReadOnlyList<SpecWarning> diagnostics)
        : base(BuildMessage(diagnostics))
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (diagnostics.Count == 0)
        {
            throw new ArgumentException(
                "SpecDocumentInvalidException requires at least one diagnostic.",
                nameof(diagnostics));
        }

        Diagnostics = diagnostics;
    }

    public IReadOnlyList<SpecWarning> Diagnostics { get; }

    public SpecWarning PrimaryDiagnostic => Diagnostics[0];

    private static string BuildMessage(IReadOnlyList<SpecWarning> diagnostics) =>
        diagnostics is { Count: > 0 }
            ? diagnostics[0].Message
            : "Spec document is invalid.";
}
