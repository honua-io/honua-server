// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Alerts.Domain;

namespace Honua.Alerts.Ops;

/// <summary>
/// A composed operations notification: a deploy-workflow or job terminal event to be
/// delivered through the shared alert delivery outbox. Producers build this; the
/// <see cref="OpsNotificationService"/> maps it onto an ops alert event.
/// </summary>
internal sealed record OpsNotification
{
    /// <summary>Ops source discriminator (e.g. <c>deploy-workflow</c>, <c>job</c>).</summary>
    public required string Source { get; init; }

    /// <summary>Severity assigned to the event (Error maps to <see cref="AlertSeverity.Critical"/>).</summary>
    public required AlertSeverity Severity { get; init; }

    /// <summary>Short human-readable title.</summary>
    public required string Title { get; init; }

    /// <summary>Human-readable body describing the event.</summary>
    public required string Body { get; init; }

    /// <summary>
    /// Stable identifier for the terminal event, unique per (source, event). Combined
    /// with <see cref="Source"/> to form the outbox dedupe key so the same terminal
    /// transition is enqueued at most once.
    /// </summary>
    public required string DedupeIdentifier { get; init; }

    /// <summary>Optional structured identifiers (operationId, workload, status, phase).</summary>
    public IReadOnlyDictionary<string, string>? Attributes { get; init; }
}

/// <summary>
/// Wire payload persisted on an ops alert event and delivered to channel sinks.
/// </summary>
internal sealed record OpsAlertPayload
{
    /// <summary>Ops source discriminator.</summary>
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    /// <summary>Canonical severity name.</summary>
    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    /// <summary>Notification title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>Notification body.</summary>
    [JsonPropertyName("body")]
    public required string Body { get; init; }

    /// <summary>Structured identifiers, when present.</summary>
    [JsonPropertyName("attributes")]
    public IReadOnlyDictionary<string, string>? Attributes { get; init; }
}

/// <summary>
/// AOT-safe serializer context for the ops notification payload.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(OpsAlertPayload))]
internal sealed partial class OpsNotificationJsonContext : JsonSerializerContext
{
}
