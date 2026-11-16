// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin;

internal static class AdminRealtimeContract
{
    internal const string LivePath = "/api/v1/admin/live";
    internal const string Protocol = "sse";
    internal const string StatusChangedEventName = "status";

    internal const string ProposalsTopic = "proposals";

    internal const string ProposalPendingEventName = "ProposalPending";

    internal const string ProposalResolvedEventName = "ProposalResolved";

    internal const string OpsHealthTopic = "ops-health";

    internal const string OpsHealthSnapshotEventName = "OpsHealthSnapshot";

    internal const string OperateEventsTopic = "operate-events";

    internal const string OperateEventEventName = "OperateEvent";

    internal const string DeployOperationsTopic = "deploy-operations";

    internal const string DeployOperationEventName = "DeployOperationTransition";
    internal static readonly string[] Topics = [ProposalsTopic, OpsHealthTopic, OperateEventsTopic, DeployOperationsTopic];
}


internal sealed record AdminLiveReset(string Reason, long Head);

internal sealed record AdminRealtimeStatus(
    string Status,
    string InstanceId,
    string EnvironmentName,
    string MigrationStatus,
    bool MigrationReady,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<string> Topics);

// The proposal event carries only dashboard fields and never the execution payload.
internal sealed record ProposalRealtimeEvent(
    string ProposalId,
    string Kind,
    string Status,
    string? RequestedBy,
    string RiskLevel,
    DateTimeOffset GeneratedAt);

// The deploy transition carries only identifiers and lifecycle classification, and delivery is at-least-once so the client deduplicates it.
internal sealed record DeployOperationRealtimeEvent(
    string EventId,
    string OperationId,
    string TransitionKind,
    string Status,
    string? Phase,
    string? TargetId,
    string? ReleaseId,
    string? CorrelationId,
    string? OperationInstanceId,
    string? AuditId,
    string? ProposalId,
    DateTimeOffset OccurredAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AdminRealtimeStatus))]
[JsonSerializable(typeof(ProposalRealtimeEvent))]
[JsonSerializable(typeof(AdminLiveReset))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class AdminRealtimeJsonContext : JsonSerializerContext;

// Kept separate from the status and proposal context so these payloads serialize like their REST counterparts.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpsHealthSnapshotResponse))]
[JsonSerializable(typeof(OperateEventResponse))]
[JsonSerializable(typeof(DeployOperationRealtimeEvent))]
internal sealed partial class AdminRealtimeGroupsJsonContext : JsonSerializerContext;
