// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Share.Domain;

public sealed record ShareExportRun
{
    public required string RunId { get; init; }

    public required string ExportId { get; init; }

    public required ShareExportTriggerKind TriggerKind { get; init; }

    public required ShareExportRunStatus Status { get; init; }

    public string? JobRunId { get; init; }

    public required DateTimeOffset TriggeredAt { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public string? TargetSummary { get; init; }

    public IReadOnlyList<string> ResultArtifacts { get; init; } = Array.Empty<string>();

    public string? LastError { get; init; }
}

public sealed record ShareExportRunPage
{
    public required IReadOnlyList<ShareExportRun> Items { get; init; }

    public string? NextCursor { get; init; }
}
