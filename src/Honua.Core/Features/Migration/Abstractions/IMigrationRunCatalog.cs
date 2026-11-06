// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Abstractions;

// A repeat call with the same run id does not insert a second row, and the evidence body is stored and returned verbatim.
public interface IMigrationRunCatalog
{
    Task<MigrationRunRecord> RecordStartedAsync(
        MigrationRunRecord record,
        CancellationToken cancellationToken = default);

    // A terminal run is sticky, so a late success cannot overwrite a cancel or a failure.
    Task<MigrationRunRecord?> RecordCompletedAsync(
        string runId,
        MigrationRunStatus status,
        DateTimeOffset completedAt,
        string? evidencePackRef,
        string? evidencePackFingerprint,
        string? evidencePackBody,
        string? statusNote,
        CancellationToken cancellationToken = default);

    Task<MigrationRunRecord?> CancelAsync(
        string runId,
        DateTimeOffset cancelledAt,
        string? statusNote,
        CancellationToken cancellationToken = default);

    Task<MigrationRunRecord?> GetAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task<MigrationRunListPage> ListAsync(
        MigrationRunListQuery query,
        CancellationToken cancellationToken = default);

    Task<string?> GetEvidencePackAsync(
        string runId,
        CancellationToken cancellationToken = default);

    Task<MigrationRunRecord?> RecordScorecardAsync(
        string runId,
        string scorecardFingerprint,
        string scorecardBody,
        CancellationToken cancellationToken = default);

    Task<string?> GetScorecardAsync(
        string runId,
        CancellationToken cancellationToken = default);
}

public sealed record MigrationRunListQuery
{
    public int Limit { get; init; } = 25;

    public int Offset { get; init; }

    public string? SourceKind { get; init; }

    public MigrationRunStatus? Status { get; init; }
}

public sealed record MigrationRunListPage
{
    public required IReadOnlyList<MigrationRunRecord> Items { get; init; }

    public required long TotalCount { get; init; }

    public required int Limit { get; init; }

    public required int Offset { get; init; }
}
