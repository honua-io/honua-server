// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Attachments.Domain;

namespace Honua.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Server-side filter for the Esri <c>queryAttachments</c> operation. Mirrors the
/// documented Esri parameters (<c>attachmentTypes</c>, <c>keywords</c>, <c>size</c>)
/// that narrow the per-feature attachment list before it is returned.
/// </summary>
/// <remarks>
/// Filtering is applied in-memory over the per-feature attachment list returned by
/// <see cref="Honua.Core.Features.Attachments.Abstractions.IAttachmentStore.ListAsync"/>;
/// the attachment store does not expose a server-side predicate API. The filter is a
/// conjunction: an attachment is included only when it satisfies every supplied facet.
/// </remarks>
internal readonly record struct AttachmentQueryFilter
{
    /// <summary>
    /// Content types / file extensions to include (case-insensitive). An attachment
    /// matches when its MIME content type equals any entry, or when its filename ends
    /// with a supplied extension (with or without a leading dot). Empty means no filter.
    /// </summary>
    public ImmutableArray<string> AttachmentTypes { get; init; }

    /// <summary>
    /// Keyword tokens to match (case-insensitive). An attachment matches when its
    /// comma-separated keyword list contains any supplied token. Empty means no filter.
    /// </summary>
    public ImmutableArray<string> Keywords { get; init; }

    /// <summary>
    /// Inclusive lower bound (bytes) on attachment size, when supplied.
    /// </summary>
    public long? MinSize { get; init; }

    /// <summary>
    /// Inclusive upper bound (bytes) on attachment size, when supplied.
    /// </summary>
    public long? MaxSize { get; init; }

    /// <summary>
    /// An empty filter that admits every attachment.
    /// </summary>
    public static AttachmentQueryFilter None => new()
    {
        AttachmentTypes = [],
        Keywords = [],
        MinSize = null,
        MaxSize = null
    };

    /// <summary>
    /// Whether the filter narrows the result at all.
    /// </summary>
    public bool HasAnyFacet =>
        !AttachmentTypes.IsDefaultOrEmpty
        || !Keywords.IsDefaultOrEmpty
        || MinSize.HasValue
        || MaxSize.HasValue;

    /// <summary>
    /// Evaluates the filter against a single attachment.
    /// </summary>
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
        foreach (var requested in Keywords)
        {
            foreach (var present in attachmentKeywords)
            {
                if (string.Equals(present, requested, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
