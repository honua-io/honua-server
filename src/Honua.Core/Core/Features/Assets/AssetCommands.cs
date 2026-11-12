// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Assets;

public static class AssetCommandOutcome
{
    public const string Accepted = "accepted";
    public const string Duplicate = "duplicate";
    public const string Conflict = "conflict";
    public const string Rejected = "rejected";
}

public static class AssetCommandSubject
{
    public const string AssetProfile = "asset_profile";
    public const string WorkTask = "work_task";
}

// Command IDs are scoped to a service. RequestSha256 covers the actor, action, route subject and canonical request,
// so reuse of an ID by another actor or with another body is detectable.
public sealed record AssetCommand(Guid CommandId, string ServiceId, string Action, string Actor, string RequestSha256);

public enum AssetCommandStatus
{
    Accepted,
    Duplicate,
    VersionConflict,
    CommandReused,
    NotFound,
    Forbidden,
    Rejected,
}

// ResponseJson is the stored receipt body: identical for the first acceptance and every replay.
public sealed record AssetCommandResult(AssetCommandStatus Status, string? ResponseJson, string? Reason = null)
{
    public string Field { get; init; } = "action";

    public static AssetCommandResult NotFound { get; } = new(AssetCommandStatus.NotFound, null);

    public static AssetCommandResult CommandReused { get; } = new(AssetCommandStatus.CommandReused, null,
        "The command ID was already used with a different request.");

    public static AssetCommandResult VersionConflict(Guid commandId, long currentVersion, string subjectName)
        => new(AssetCommandStatus.VersionConflict, JsonSerializer.Serialize(
            new AssetCommandConflict
            {
                CommandId = commandId,
                Reason = $"The {subjectName} changed since it was read; reload and retry.",
                CurrentVersion = currentVersion,
            },
            AssetsJsonContext.Default.AssetCommandConflict));
}

public sealed record AssetFieldError(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("message")] string Message);

public sealed record AssetValidationProblem
{
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = AssetCommandOutcome.Rejected;

    [JsonPropertyName("errors")]
    public required IReadOnlyList<AssetFieldError> Errors { get; init; }
}

public sealed record AssetCommandConflict
{
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = AssetCommandOutcome.Conflict;

    [JsonPropertyName("commandId")]
    public required Guid CommandId { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("currentVersion")]
    public long? CurrentVersion { get; init; }
}
