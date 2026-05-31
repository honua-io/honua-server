// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Console.Collaboration.Domain;
using Honua.Server.Features.Console.Collaboration.Models;

namespace Honua.Server.Features.Console.Collaboration;

/// <summary>
/// Projects durable collaboration domain records onto the Console-facing wire DTOs,
/// deriving presentation-only fields (avatar initials/color, relative time) that the
/// Console comment drawer and activity sidebar render.
/// </summary>
internal static class StudioMapCollaborationMapper
{
    // Deterministic palette mirrored from the Console collaboration mockup so the
    // same author keeps the same avatar color across server-rendered surfaces.
    private static readonly string[] _palette =
    [
        "#2563eb", "#7c3aed", "#db2777", "#ea580c",
        "#16a34a", "#0891b2", "#ca8a04", "#dc2626",
    ];

    public static StudioMapCommentThreadDto ToThreadDto(StudioMapCommentThread thread, DateTimeOffset now)
        => new()
        {
            ThreadId = thread.ThreadId.ToString("D", CultureInfo.InvariantCulture),
            FeatureLabel = thread.FeatureLabel,
            LayerRef = thread.LayerRef,
            CommentCount = thread.Messages.Count,
            Resolved = thread.Resolved,
            XFraction = thread.AnchorX,
            YFraction = thread.AnchorY,
            Messages = thread.Messages.Select(m => ToMessageDto(m, now)).ToList(),
        };

    public static StudioMapCommentMessageDto ToMessageDto(StudioMapCommentMessage message, DateTimeOffset now)
        => new()
        {
            MessageId = message.MessageId.ToString("D", CultureInfo.InvariantCulture),
            AuthorName = message.AuthorName,
            AuthorInitials = Initials(message.AuthorName),
            AuthorColor = Color(message.AuthorName),
            RelativeTime = RelativeTime(message.CreatedAt, now),
            Body = message.Body,
        };

    public static StudioMapActivityEntryDto ToActivityDto(StudioMapActivityEvent activity, DateTimeOffset now)
        => new()
        {
            ParticipantName = activity.ActorName,
            Initials = Initials(activity.ActorName),
            Color = Color(activity.ActorName),
            RelativeTime = RelativeTime(activity.OccurredAt, now),
            Action = activity.Action,
        };

    internal static string Initials(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var parts = name.Split([' ', '\t', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "?";
        }

        if (parts.Length == 1)
        {
            return parts[0].Length == 1
                ? parts[0].ToUpperInvariant()
                : parts[0][..2].ToUpperInvariant();
        }

        return string.Concat(
            char.ToUpperInvariant(parts[0][0]),
            char.ToUpperInvariant(parts[^1][0]));
    }

    internal static string Color(string name)
    {
        var key = name ?? string.Empty;
        var hash = 0;
        foreach (var ch in key)
        {
            hash = unchecked((hash * 31) + ch);
        }

        var index = (int)((uint)hash % (uint)_palette.Length);
        return _palette[index];
    }

    internal static string RelativeTime(DateTimeOffset at, DateTimeOffset now)
    {
        var delta = now - at;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalSeconds < 60)
        {
            return "just now";
        }

        if (delta.TotalMinutes < 60)
        {
            var minutes = (int)delta.TotalMinutes;
            return $"{minutes}m ago";
        }

        if (delta.TotalHours < 24)
        {
            var hours = (int)delta.TotalHours;
            return $"{hours}h ago";
        }

        var days = (int)delta.TotalDays;
        return $"{days}d ago";
    }
}
