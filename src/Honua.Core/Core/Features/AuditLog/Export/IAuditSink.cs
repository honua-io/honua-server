// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;

namespace Honua.Core.Features.AuditLog.Export;

public interface IAuditSink
{
    string SinkType { get; }

    // The region is checked before any audit record leaves the process.
    string? Region { get; }

    // Expected transport failures are returned as retryable or permanent results and are not thrown.
    Task<AuditSinkResult> SendAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct);
}

public sealed record AuditSinkResult
{
    public required bool Succeeded { get; init; }

    public required bool Retryable { get; init; }

    public string? Error { get; init; }

    public static AuditSinkResult Success() => new() { Succeeded = true, Retryable = false };

    public static AuditSinkResult TransientFailure(string error)
        => new() { Succeeded = false, Retryable = true, Error = error };

    public static AuditSinkResult PermanentFailure(string error)
        => new() { Succeeded = false, Retryable = false, Error = error };
}
