// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Console.Collaboration.Models;

public sealed class StudioMapCommentThreadDto
{
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }

    [JsonPropertyName("featureLabel")]
    public required string FeatureLabel { get; init; }

    [JsonPropertyName("layerRef")]
    public required string LayerRef { get; init; }

    [JsonPropertyName("commentCount")]
    public required int CommentCount { get; init; }

    [JsonPropertyName("resolved")]
    public required bool Resolved { get; init; }

    [JsonPropertyName("xFraction")]
    public required double XFraction { get; init; }

    [JsonPropertyName("yFraction")]
    public required double YFraction { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<StudioMapCommentMessageDto> Messages { get; init; }
}

public sealed class StudioMapCommentMessageDto
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    [JsonPropertyName("authorName")]
    public required string AuthorName { get; init; }

    [JsonPropertyName("authorInitials")]
    public required string AuthorInitials { get; init; }

    [JsonPropertyName("authorColor")]
    public required string AuthorColor { get; init; }

    [JsonPropertyName("relativeTime")]
    public required string RelativeTime { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }
}

public sealed class StudioMapCommentThreadListResponse
{
    [JsonPropertyName("mapId")]
    public required string MapId { get; init; }

    [JsonPropertyName("threads")]
    public required IReadOnlyList<StudioMapCommentThreadDto> Threads { get; init; }
}

public sealed class StudioMapActivityEntryDto
{
    [JsonPropertyName("participantName")]
    public required string ParticipantName { get; init; }

    [JsonPropertyName("initials")]
    public required string Initials { get; init; }

    [JsonPropertyName("color")]
    public required string Color { get; init; }

    [JsonPropertyName("relativeTime")]
    public required string RelativeTime { get; init; }

    [JsonPropertyName("action")]
    public required string Action { get; init; }
}

public sealed class StudioMapActivityListResponse
{
    [JsonPropertyName("mapId")]
    public required string MapId { get; init; }

    [JsonPropertyName("activity")]
    public required IReadOnlyList<StudioMapActivityEntryDto> Activity { get; init; }
}

public sealed class CreateStudioMapCommentThreadRequest
{
    [Required]
    [JsonPropertyName("featureLabel")]
    public required string FeatureLabel { get; init; }

    [Required]
    [JsonPropertyName("layerRef")]
    public required string LayerRef { get; init; }

    [Range(0d, 1d)]
    [JsonPropertyName("xFraction")]
    public required double XFraction { get; init; }

    [Range(0d, 1d)]
    [JsonPropertyName("yFraction")]
    public required double YFraction { get; init; }

    [Required]
    [JsonPropertyName("body")]
    public required string Body { get; init; }
}

public sealed class CreateStudioMapCommentReplyRequest
{
    [Required]
    [JsonPropertyName("body")]
    public required string Body { get; init; }
}

public sealed class ResolveStudioMapCommentThreadRequest
{
    [JsonPropertyName("resolved")]
    public bool Resolved { get; init; } = true;
}
