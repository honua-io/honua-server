// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Thrown by <see cref="Honua.Core.Features.Spec.Abstractions.ISpecApplyEngine.StartAsync"/>
/// when the planner reports fatal document-level diagnostics (duplicate ids,
/// cycles, unresolved references, reserved kinds with no executable path).
/// The REST and gRPC adapters translate this into
/// <c>400 Bad Request</c> / <c>InvalidArgument</c> without opening a stream.
/// </summary>
public sealed class SpecDocumentInvalidException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SpecDocumentInvalidException"/> class.
    /// </summary>
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

    /// <summary>Fatal diagnostics reported by the planner.</summary>
    public IReadOnlyList<SpecWarning> Diagnostics { get; }

    /// <summary>First fatal diagnostic — used for the adapter response envelope.</summary>
    public SpecWarning PrimaryDiagnostic => Diagnostics[0];

    private static string BuildMessage(IReadOnlyList<SpecWarning> diagnostics) =>
        diagnostics is { Count: > 0 }
            ? diagnostics[0].Message
            : "Spec document is invalid.";
}
