// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts.Ops;

internal sealed record OpsNotification
{
    public required string Source { get; init; }

    public required AlertSeverity Severity { get; init; }

    public required string Title { get; init; }

    public required string Body { get; init; }

    public required string DedupeIdentifier { get; init; }

    public string? JobId { get; init; }
    public string? OperationInstanceId { get; init; }
    public string? CorrelationId { get; init; }
    public string? AuditId { get; init; }
    public string? ProposalId { get; init; }

    public IReadOnlyDictionary<string, string>? Attributes { get; init; }
}

internal sealed record OpsAlertPayload
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }

    [JsonPropertyName("attributes")]
    public IReadOnlyDictionary<string, string>? Attributes { get; init; }

    [JsonPropertyName("jobId")]
    public string? JobId { get; init; }

    [JsonPropertyName("operationInstanceId")]
    public string? OperationInstanceId { get; init; }

    [JsonPropertyName("correlationId")]
    public string? CorrelationId { get; init; }

    [JsonPropertyName("auditId")]
    public string? AuditId { get; init; }

    [JsonPropertyName("proposalId")]
    public string? ProposalId { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(OpsAlertPayload))]
internal sealed partial class OpsNotificationJsonContext : JsonSerializerContext
{
}
