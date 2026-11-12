// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Assets;

public static class WorkTaskStatus
{
    public const string Unassigned = "unassigned";
    public const string Assigned = "assigned";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } = [Unassigned, Assigned, InProgress, Completed, Cancelled];

    public static bool IsTerminal(string status) => status is Completed or Cancelled;
}

public static class WorkTaskType
{
    public const string Inspection = "inspection";
    public const string Maintenance = "maintenance";
    public const string Repair = "repair";
    public const string Request = "request";

    public static IReadOnlyList<string> All { get; } = [Inspection, Maintenance, Repair, Request];
}

public static class WorkTaskAction
{
    public const string Create = "create";
    public const string Assign = "assign";
    public const string Unassign = "unassign";
    public const string Start = "start";
    public const string Reassign = "reassign";
    public const string Cancel = "cancel";
    public const string Complete = "complete";

    public static IReadOnlyList<string> Transitions { get; } = [Assign, Unassign, Start, Reassign, Cancel];
}

public sealed record WorkTaskFormReference
{
    [JsonPropertyName("formId")]
    public required string FormId { get; init; }

    [JsonPropertyName("version")]
    public required int Version { get; init; }
}

public sealed record WorkTaskCreateRequest
{
    [JsonPropertyName("commandId")]
    public Guid? CommandId { get; init; }

    [JsonPropertyName("taskId")]
    public Guid? TaskId { get; init; }

    [JsonPropertyName("serviceId")]
    public string? ServiceId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("profileId")]
    public Guid? ProfileId { get; init; }

    [JsonPropertyName("assetId")]
    public string? AssetId { get; init; }

    // GeoJSON geometry in WGS 84; required only when the task has no asset.
    [JsonPropertyName("location")]
    public JsonElement? Location { get; init; }

    [JsonPropertyName("assignee")]
    public string? Assignee { get; init; }

    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("dueAt")]
    public DateTimeOffset? DueAt { get; init; }

    [JsonPropertyName("formId")]
    public string? FormId { get; init; }
}

public sealed record WorkTaskTransitionRequest
{
    [JsonPropertyName("commandId")]
    public Guid? CommandId { get; init; }

    [JsonPropertyName("expectedVersion")]
    public long? ExpectedVersion { get; init; }

    [JsonPropertyName("action")]
    public string? Action { get; init; }

    [JsonPropertyName("assignee")]
    public string? Assignee { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("observedAt")]
    public DateTimeOffset? ObservedAt { get; init; }
}

public sealed record WorkTask
{
    [JsonPropertyName("taskId")]
    public required Guid TaskId { get; init; }

    [JsonPropertyName("objectId")]
    public long ObjectId { get; init; }

    [JsonPropertyName("serviceId")]
    public required string ServiceId { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("version")]
    public required long Version { get; init; }

    [JsonPropertyName("profileId")]
    public Guid? ProfileId { get; init; }

    [JsonPropertyName("assetId")]
    public string? AssetId { get; init; }

    [JsonPropertyName("geometry")]
    public JsonElement Geometry { get; init; }

    [JsonPropertyName("geometrySource")]
    public required string GeometrySource { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("assignee")]
    public string? Assignee { get; init; }

    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("dueAt")]
    public DateTimeOffset? DueAt { get; init; }

    [JsonPropertyName("form")]
    public required WorkTaskFormReference Form { get; init; }

    [JsonPropertyName("createdBy")]
    public required string CreatedBy { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; init; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("cancelledAt")]
    public DateTimeOffset? CancelledAt { get; init; }

    [JsonPropertyName("cancelReason")]
    public string? CancelReason { get; init; }

    [JsonPropertyName("permittedActions")]
    public IReadOnlyList<string>? PermittedActions { get; init; }
}

public sealed record WorkTaskCommandResponse
{
    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("commandId")]
    public required Guid CommandId { get; init; }

    [JsonPropertyName("task")]
    public required WorkTask Task { get; init; }

    [JsonPropertyName("inspectionId")]
    public Guid? InspectionId { get; init; }

    [JsonPropertyName("serviceRecordId")]
    public Guid? ServiceRecordId { get; init; }
}

public sealed record WorkTaskListResult
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<WorkTask> Items { get; init; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

public sealed record WorkTaskHistoryEvent
{
    [JsonPropertyName("eventId")]
    public required Guid EventId { get; init; }

    [JsonPropertyName("commandId")]
    public required Guid CommandId { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }

    [JsonPropertyName("oldVersion")]
    public long? OldVersion { get; init; }

    [JsonPropertyName("newVersion")]
    public required long NewVersion { get; init; }

    [JsonPropertyName("oldStatus")]
    public string? OldStatus { get; init; }

    [JsonPropertyName("newStatus")]
    public required string NewStatus { get; init; }

    [JsonPropertyName("actor")]
    public required string Actor { get; init; }

    [JsonPropertyName("recordedAt")]
    public required DateTimeOffset RecordedAt { get; init; }

    [JsonPropertyName("observedAt")]
    public DateTimeOffset? ObservedAt { get; init; }

    [JsonPropertyName("changedFields")]
    public required IReadOnlyList<string> ChangedFields { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record WorkTaskHistoryResult
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<WorkTaskHistoryEvent> Items { get; init; }

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

public sealed record WorkTaskQuery
{
    public required string ServiceId { get; init; }

    public string? Status { get; init; }

    public string? Assignee { get; init; }

    public string? Type { get; init; }

    public long AfterObjectId { get; init; }

    public int Limit { get; init; }
}

public sealed record WorkTaskHistoryQuery(Guid TaskId, long AfterVersion, int Limit);

// Geometry arrives as WKB in its source SRID: copied from the asset feature or parsed from the request location.
public sealed record WorkTaskDraft(WorkTask Task, byte[] GeometryWkb, int GeometrySrid);

public sealed record WorkTaskActor(string Id, bool IsPlanner);

public sealed record WorkTaskTransitionCommand(Guid TaskId, WorkTaskTransitionRequest Request, WorkTaskActor Actor, AssetCommand Command);
