// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Protocols.GeoServices.Models;

/// <summary>
/// External governance link projected into additive GeoServices metadata.
/// </summary>
public sealed class GeoServicesGovernanceLink
{
    /// <summary>Absolute documentation URL.</summary>
    [JsonPropertyName("href")]
    public required string Href { get; init; }

    /// <summary>RFC 8288 link relation.</summary>
    [JsonPropertyName("rel")]
    public required string Rel { get; init; }

    /// <summary>Linked document media type, when authored.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Human-readable link title, when authored.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Linked document language, when authored.</summary>
    [JsonPropertyName("hreflang")]
    public string? HrefLang { get; init; }
}

/// <summary>
/// Projects only public license and source-documentation links from canonical metadata.
/// </summary>
internal static class GeoServicesGovernanceProjection
{
    internal static GeoServicesGovernanceLink[]? ProjectLinks(MetadataV2ObjectMetadata metadata)
    {
        var links = metadata.Links
            .Where(link =>
                string.Equals(link.Rel, "license", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(link.Rel, "describedby", StringComparison.OrdinalIgnoreCase))
            .Select(link => new GeoServicesGovernanceLink
            {
                Href = link.Href,
                Rel = link.Rel,
                Type = link.Type,
                Title = link.Title,
                HrefLang = link.Hreflang
            })
            .ToList();

        if (!links.Any(link => string.Equals(link.Rel, "license", StringComparison.OrdinalIgnoreCase)) &&
            SpdxLicensePolicy.GetLicenseUrl(metadata.License) is { } derivedLicenseUrl)
        {
            links.Add(new GeoServicesGovernanceLink
            {
                Href = derivedLicenseUrl,
                Rel = "license",
                Title = metadata.License
            });
        }

        return links.Count == 0 ? null : [.. links];
    }
}
