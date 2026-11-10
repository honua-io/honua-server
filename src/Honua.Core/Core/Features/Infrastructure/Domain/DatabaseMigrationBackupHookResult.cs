// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

public sealed record DatabaseMigrationBackupHookResult
{
    public required string Outcome { get; init; }

    public required bool Succeeded { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    public required long DurationMilliseconds { get; init; }

    public int? ExitCode { get; init; }

    public string? Stderr { get; init; }

    public IReadOnlyList<string> PendingContractScripts { get; init; } = Array.Empty<string>();

    public string? MigrationRunId { get; init; }

    public string? CorrelationId { get; init; }
}
