// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.DisasterRecovery.Domain;

public sealed record BackupRecord(
    string Id,
    BackupKind Kind,
    DateTimeOffset CompletedAt,
    bool Succeeded,
    long? SizeBytes = null,
    string? FailureReason = null)
{
    public bool IsDataProtecting
        => Kind is BackupKind.PostgresBase or BackupKind.PostgresWal;
}
