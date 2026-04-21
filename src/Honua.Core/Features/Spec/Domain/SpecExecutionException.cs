// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Domain exception carrying a stable diagnostic code for endpoint translation.
/// </summary>
public sealed class SpecExecutionException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SpecExecutionException"/> class.
    /// </summary>
    /// <param name="code">Stable diagnostic code from <see cref="SpecDiagnosticCodes"/>.</param>
    /// <param name="message">Operator-facing message.</param>
    /// <param name="nodeId">Optional node identifier for per-node failures.</param>
    /// <param name="remedy">Optional remediation hint.</param>
    public SpecExecutionException(string code, string message, string? nodeId = null, string? remedy = null)
        : base(message)
    {
        Code = code;
        NodeId = nodeId;
        Remedy = remedy;
    }

    /// <summary>Stable diagnostic code from <see cref="SpecDiagnosticCodes"/>.</summary>
    public string Code { get; }

    /// <summary>Optional node identifier for per-node failures.</summary>
    public string? NodeId { get; }

    /// <summary>Optional remediation hint safe for operator display.</summary>
    public string? Remedy { get; }

    /// <summary>
    /// Converts this exception to a structured <see cref="SpecWarning"/> diagnostic.
    /// </summary>
    public SpecWarning ToWarning(SpecDiagnosticSeverity severity = SpecDiagnosticSeverity.Error) =>
        new()
        {
            Code = Code,
            Message = Message,
            Severity = severity,
            NodeId = NodeId,
            Remedy = Remedy
        };
}
