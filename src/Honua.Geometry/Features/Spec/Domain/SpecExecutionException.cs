// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

public sealed class SpecExecutionException : Exception
{
    public SpecExecutionException(string code, string message, string? nodeId = null, string? remedy = null)
        : base(message)
    {
        Code = code;
        NodeId = nodeId;
        Remedy = remedy;
    }

    public string Code { get; }

    public string? NodeId { get; }

    public string? Remedy { get; }

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
