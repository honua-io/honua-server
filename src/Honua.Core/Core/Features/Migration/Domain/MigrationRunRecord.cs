// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
namespace Honua.Core.Features.Migration.Domain;

// The source URL must already have userinfo, query, and fragment stripped before it is stored.
public sealed record MigrationRunRecord
{
    public required string RunId { get; init; }

    public required string SourceKind { get; init; }

    public string SourceUrl { get; init; } = string.Empty;

    public string? SourceDisplayName { get; init; }

    public required MigrationRunStatus Status { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? EvidencePackRef { get; init; }

    public string? EvidencePackFingerprint { get; init; }

    public string? StatusNote { get; init; }

    public string? ReconciliationScorecardFingerprint { get; init; }
}

public enum MigrationRunStatus
{
    Running,

    Succeeded,

    Failed,

    Cancelled
}
