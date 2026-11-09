// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Attachments.Domain;

namespace Honua.Protocols.GeoServices.FeatureServer;

internal readonly record struct AttachmentQueryFilter
{
    public ImmutableArray<string> AttachmentTypes { get; init; }

    public ImmutableArray<string> Keywords { get; init; }

    public long? MinSize { get; init; }

    public long? MaxSize { get; init; }

    public static AttachmentQueryFilter None => new()
    {
        AttachmentTypes = [],
        Keywords = [],
        MinSize = null,
        MaxSize = null
    };

    public bool HasAnyFacet =>
        !AttachmentTypes.IsDefaultOrEmpty
        || !Keywords.IsDefaultOrEmpty
        || MinSize.HasValue
        || MaxSize.HasValue;

    public bool Matches(Attachment attachment)
    {
        if (MinSize.HasValue && attachment.Size < MinSize.Value)
        {
            return false;
        }

        if (MaxSize.HasValue && attachment.Size > MaxSize.Value)
        {
            return false;
        }

        if (!AttachmentTypes.IsDefaultOrEmpty && !MatchesAnyType(attachment))
        {
            return false;
        }

        if (!Keywords.IsDefaultOrEmpty && !MatchesAnyKeyword(attachment))
        {
            return false;
        }

        return true;
    }

    private bool MatchesAnyType(Attachment attachment)
    {
        foreach (var type in AttachmentTypes)
        {
            if (string.Equals(attachment.ContentType, type, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Esri accepts file extensions (e.g. "jpg,png") as well as MIME types.
            var extension = type.StartsWith('.') ? type : "." + type;
            if (attachment.Filename.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool MatchesAnyKeyword(Attachment attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment.Keywords))
        {
            return false;
        }

        var attachmentKeywords = attachment.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Keywords.Any(requested =>
            attachmentKeywords.Any(present => string.Equals(present, requested, StringComparison.OrdinalIgnoreCase)));
    }
}
