// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

public interface IAuditLogIntegrityVerifier
{
    Task<AuditIntegrityReport> VerifyAsync(CancellationToken cancellationToken = default);
}

public sealed record AuditIntegrityReport
{
    public required bool Verified { get; init; }

    public required long RowsChecked { get; init; }

    public required long UnhashedRows { get; init; }

    public long AttestedGapRows { get; init; }

    public long? FirstBrokenAuditId { get; init; }

    public string? FailureReason { get; init; }
}
