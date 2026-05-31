// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Honua.Server.Features.Console.Collaboration.Models;

/// <summary>
/// Wire DTO for a feature-pinned comment thread. JSON shape matches the Console
/// <c>StudioMapCommentPin</c> view model (honua-console#124) so the comment drawer
/// binds directly: <c>threadId, featureLabel, layerRef, commentCount, resolved,
/// xFraction, yFraction, messages[]</c>.
/// </summary>
public sealed class StudioMapCommentThreadDto
{
    /// <summary>Stable thread identifier.</summary>
    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }

    /// <summary>Human-readable label of the anchored feature.</summary>
    [JsonPropertyName("featureLabel")]
    public required string FeatureLabel { get; init; }

    /// <summary>Layer reference the anchored feature belongs to.</summary>
    [JsonPropertyName("layerRef")]
    public required string LayerRef { get; init; }

    /// <summary>Number of messages in the thread.</summary>
    [JsonPropertyName("commentCount")]
    public required int CommentCount { get; init; }

    /// <summary>Whether the thread is resolved.</summary>
    [JsonPropertyName("resolved")]
    public required bool Resolved { get; init; }

    /// <summary>Normalized horizontal anchor position over the map canvas (0..1).</summary>
    [JsonPropertyName("xFraction")]
    public required double XFraction { get; init; }

    /// <summary>Normalized vertical anchor position over the map canvas (0..1).</summary>
    [JsonPropertyName("yFraction")]
    public required double YFraction { get; init; }

    /// <summary>Messages in the thread, oldest first.</summary>
    [JsonPropertyName("messages")]
    public required IReadOnlyList<StudioMapCommentMessageDto> Messages { get; init; }
}

/// <summary>
/// Wire DTO for a single comment message. JSON shape matches the Console
/// <c>StudioMapCommentMessage</c> view model.
/// </summary>
public sealed class StudioMapCommentMessageDto
{
    /// <summary>Stable message identifier.</summary>
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    /// <summary>Display name of the author.</summary>
    [JsonPropertyName("authorName")]
    public required string AuthorName { get; init; }

    /// <summary>Author initials for the avatar badge.</summary>
    [JsonPropertyName("authorInitials")]
    public required string AuthorInitials { get; init; }

    /// <summary>Deterministic author avatar color.</summary>
    [JsonPropertyName("authorColor")]
    public required string AuthorColor { get; init; }

    /// <summary>Relative time label (e.g. "2m ago").</summary>
    [JsonPropertyName("relativeTime")]
    public required string RelativeTime { get; init; }

    /// <summary>Message body text.</summary>
    [JsonPropertyName("body")]
    public required string Body { get; init; }
}

/// <summary>List response wrapper for comment threads on a map.</summary>
public sealed class StudioMapCommentThreadListResponse
{
    /// <summary>Map draft/package identifier.</summary>
    [JsonPropertyName("mapId")]
    public required string MapId { get; init; }

    /// <summary>Threads, newest activity first.</summary>
    [JsonPropertyName("threads")]
    public required IReadOnlyList<StudioMapCommentThreadDto> Threads { get; init; }
}

/// <summary>
/// Wire DTO for a collaboration activity-feed entry. JSON shape matches the Console
/// <c>StudioMapActivityEntry</c> view model: <c>participantName, initials, color,
/// relativeTime, action</c>.
/// </summary>
public sealed class StudioMapActivityEntryDto
{
    /// <summary>Display name of the participant.</summary>
    [JsonPropertyName("participantName")]
    public required string ParticipantName { get; init; }

    /// <summary>Participant initials for the avatar badge.</summary>
    [JsonPropertyName("initials")]
    public required string Initials { get; init; }

    /// <summary>Deterministic participant avatar color.</summary>
    [JsonPropertyName("color")]
    public required string Color { get; init; }

    /// <summary>Relative time label (e.g. "5m ago").</summary>
    [JsonPropertyName("relativeTime")]
    public required string RelativeTime { get; init; }

    /// <summary>Human-readable action summary.</summary>
    [JsonPropertyName("action")]
    public required string Action { get; init; }
}

/// <summary>List response wrapper for the activity feed on a map.</summary>
public sealed class StudioMapActivityListResponse
{
    /// <summary>Map draft/package identifier.</summary>
    [JsonPropertyName("mapId")]
    public required string MapId { get; init; }

    /// <summary>Activity entries, newest first.</summary>
    [JsonPropertyName("activity")]
    public required IReadOnlyList<StudioMapActivityEntryDto> Activity { get; init; }
}

/// <summary>Request body for opening a feature-pinned comment thread.</summary>
public sealed class CreateStudioMapCommentThreadRequest
{
    /// <summary>Human-readable label of the anchored feature.</summary>
    [Required]
    [JsonPropertyName("featureLabel")]
    public required string FeatureLabel { get; init; }

    /// <summary>Layer reference the anchored feature belongs to.</summary>
    [Required]
    [JsonPropertyName("layerRef")]
    public required string LayerRef { get; init; }

    /// <summary>Normalized horizontal anchor position over the map canvas (0..1).</summary>
    [Range(0d, 1d)]
    [JsonPropertyName("xFraction")]
    public required double XFraction { get; init; }

    /// <summary>Normalized vertical anchor position over the map canvas (0..1).</summary>
    [Range(0d, 1d)]
    [JsonPropertyName("yFraction")]
    public required double YFraction { get; init; }

    /// <summary>First message body.</summary>
    [Required]
    [JsonPropertyName("body")]
    public required string Body { get; init; }
}

/// <summary>Request body for replying to a comment thread.</summary>
public sealed class CreateStudioMapCommentReplyRequest
{
    /// <summary>Reply body text.</summary>
    [Required]
    [JsonPropertyName("body")]
    public required string Body { get; init; }
}

/// <summary>Request body for resolving or reopening a comment thread.</summary>
public sealed class ResolveStudioMapCommentThreadRequest
{
    /// <summary>Target resolved state. True resolves, false reopens.</summary>
    [JsonPropertyName("resolved")]
    public bool Resolved { get; init; } = true;
}
