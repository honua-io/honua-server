// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Share.Domain;

// Secrets are named by reference and redacted on read, never stored in the definition.
public sealed record ShareExportDefinition
{
    public required string ExportId { get; init; }

    public string? ResourceId { get; init; }

    public required string ServiceName { get; init; }

    public required int LayerId { get; init; }

    public string? DisplayName { get; init; }

    public required ShareExportDestinationType DestinationType { get; init; }

    public required ShareExportDestinationStatus DestinationStatus { get; init; }

    public IReadOnlyDictionary<string, string> DestinationConfig { get; init; } =
        new Dictionary<string, string>();

    public required string Format { get; init; }

    public required string Schedule { get; init; }

    public ShareExportScheduleState ScheduleState { get; init; } = ShareExportScheduleState.Active;

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? LastRunAt { get; init; }

    public DateTimeOffset? NextRunAt { get; init; }
}

public sealed record ShareExportDefinitionQuery
{
    public string? ServiceName { get; init; }

    public string? ResourceId { get; init; }

    public int? LayerId { get; init; }

    public ShareExportDestinationType? DestinationType { get; init; }

    public ShareExportScheduleState? ScheduleState { get; init; }

    public string? Cursor { get; init; }

    public int Limit { get; init; } = 50;
}

public sealed record ShareExportDefinitionPage
{
    public required IReadOnlyList<ShareExportDefinition> Items { get; init; }

    public string? NextCursor { get; init; }
}
