// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Monitoring;

namespace Honua.Server.Features.Admin.Models;

internal sealed class OperateEventResponse
{
    public required string EventId { get; init; }

    public required string Kind { get; init; }

    public required string Severity { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string Title { get; init; }

    public string? Summary { get; init; }

    public string? ServiceId { get; init; }

    public int? LayerId { get; init; }

    public long? ObjectId { get; init; }

    public string? Actor { get; init; }

    public string? CorrelationId { get; init; }

    public string? TraceId { get; init; }

    public string? RequestId { get; init; }

    public string? OperationId { get; init; }

    public string? ReleaseId { get; init; }

    public string? ReplicaId { get; init; }

    public string? ChangeSetId { get; init; }

    public string? ResourceRef { get; init; }

    public IReadOnlyList<OperateProviderLinkResponse>? ProviderLinks { get; init; }

    public string? DetailsJson { get; init; }
}

internal sealed class OperateProviderLinkResponse
{
    public required string Provider { get; init; }

    public required string Label { get; init; }

    public required string Url { get; init; }
}

internal sealed class OperateEventPageResponse
{
    public EvidencePosture? EvidencePosture { get; init; }

    public required IReadOnlyList<OperateEventResponse> Items { get; init; }

    public bool PartialResult { get; init; }

    public IReadOnlyDictionary<string, string>? SourceErrors { get; init; }
}

internal sealed class OperateLogEntryResponse
{
    public required DateTimeOffset Timestamp { get; init; }

    public required string Level { get; init; }

    public string? Path { get; init; }

    public required int StatusCode { get; init; }

    public required string CorrelationId { get; init; }

    public required string Message { get; init; }
}

internal sealed class OperateLogPageResponse
{
    public required string InstanceId { get; init; }

    public required int Capacity { get; init; }

    public required IReadOnlyList<OperateLogEntryResponse> Items { get; init; }
}
