// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Styling.Abstractions;

namespace Honua.Core.Features.Styling;

public static class SharedStyleIdentity
{
    public static string ContentHash(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical ?? string.Empty))).ToLowerInvariant();

    // The Console rejects a style version below 1. A stored 0 is the pre-PUT revision.
    public static int ReportedVersion(int storedVersion) => storedVersion < 1 ? 1 : storedVersion;

    // Strong entity-tag value, including the surrounding quotes.
    public static string EntityTag(int storedVersion, string contentHash) =>
        $"\"v{storedVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{contentHash}\"";

    // Consumer identity only: a draft autosave changes its content, not who consumes the style.
    public static string ImpactHash(SharedStyleImpact impact)
    {
        var builder = new StringBuilder();
        builder.Append(impact.StyleId).Append('\n');
        builder.Append(impact.StoredVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        builder.Append(impact.ContentHash).Append('\n');
        builder.Append(impact.ImpactComplete ? "complete\n" : "partial\n");
        foreach (var association in impact.Associations.OrderBy(item => item.ResourceId, StringComparer.Ordinal))
        {
            builder.Append("a|").Append(association.ResourceId).Append('|')
                .Append(association.Name).Append('|').Append(association.ServiceId).Append('\n');
        }

        foreach (var consumer in impact.Consumers.OrderBy(item => item.ItemId, StringComparer.Ordinal)
            .ThenBy(item => item.VersionId, StringComparer.Ordinal)
            .ThenBy(item => item.DraftId, StringComparer.Ordinal))
        {
            builder.Append("c|").Append(consumer.Kind).Append('|').Append(consumer.ItemId).Append('|')
                .Append(consumer.VersionId).Append('|').Append(consumer.DraftId).Append('|')
                .Append(consumer.PublicationState).Append('|')
                .Append(consumer.IsCurrent ? "1" : "0").Append('\n');
        }

        return ContentHash(builder.ToString());
    }
}
