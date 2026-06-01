// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Console.Domain;
using Honua.Server.Features.Console.Models;

namespace Honua.Server.Features.Console;

/// <summary>
/// Pure projection helpers that map Console open-data page state (joined with the
/// content item and its effective share tier) into DCAT-US 3.0/data.json, STAC
/// catalog/collection/item, and Schema.org Dataset shapes, and that evaluate
/// open-data eligibility and DCAT validation. No I/O — endpoints supply the
/// already-resolved state and these helpers shape the wire response.
/// </summary>
internal static class ConsoleOpenDataMapper
{
    // Stable machine reason codes for the eligibility decision.
    public const string ReasonEligible = "eligible";
    public const string ReasonNotDistributable = "not-distributable-type";
    public const string ReasonNotPublicIndexed = "not-public-indexed";

    /// <summary>
    /// Item types that can be distributed as open data. Compositions (saved maps,
    /// dashboards, reports, generated apps) are not open-data datasets. Mirrors
    /// the distributable-type rule used by the Share projection.
    /// </summary>
    public static bool IsDistributableType(ConsoleContentItemType itemType) => itemType switch
    {
        ConsoleContentItemType.Layer
            or ConsoleContentItemType.Service
            or ConsoleContentItemType.OpenData => true,
        _ => false,
    };

    /// <summary>
    /// Evaluates whether an item may be published as open data, returning a stable
    /// reason code and a human-readable explanation. Open data requires a
    /// distributable item type that is public-indexed (publicly readable).
    /// </summary>
    public static ConsoleOpenDataEligibilityResponse EvaluateEligibility(
        ConsoleContentItem item,
        ConsoleShareAccessTier effectiveTier,
        bool hasPage)
    {
        if (!IsDistributableType(item.ItemType))
        {
            return new ConsoleOpenDataEligibilityResponse
            {
                ItemId = item.Id,
                ItemType = item.ItemType,
                Eligible = false,
                ReasonCode = ReasonNotDistributable,
                Reason = $"Item type '{item.ItemType}' cannot be distributed as open data; only layers, services, and open-data items are distributable.",
                AccessTier = effectiveTier,
                HasPage = hasPage,
            };
        }

        if (effectiveTier != ConsoleShareAccessTier.PublicIndexed)
        {
            return new ConsoleOpenDataEligibilityResponse
            {
                ItemId = item.Id,
                ItemType = item.ItemType,
                Eligible = false,
                ReasonCode = ReasonNotPublicIndexed,
                Reason = "Set the item's share access tier to public-indexed before publishing it as open data.",
                AccessTier = effectiveTier,
                HasPage = hasPage,
            };
        }

        return new ConsoleOpenDataEligibilityResponse
        {
            ItemId = item.Id,
            ItemType = item.ItemType,
            Eligible = true,
            ReasonCode = ReasonEligible,
            Reason = "Item is a public-indexed distributable type and may be published as open data.",
            AccessTier = effectiveTier,
            HasPage = hasPage,
        };
    }

    /// <summary>
    /// Builds the page projection surfaced to Console, filling presentation
    /// defaults from the content item where the page leaves a field blank. The
    /// returned page is a read view; persisted writes go through the store.
    /// </summary>
    public static ConsoleOpenDataPage BuildPageProjection(ConsoleContentItem item, ConsoleOpenDataPage? stored)
    {
        if (stored is null)
        {
            // No page authored yet — surface a defaults-only page so Console can
            // render an editable form pre-filled from item metadata.
            return new ConsoleOpenDataPage
            {
                ItemId = item.Id,
                Title = item.Title ?? item.Name,
                Description = item.Description,
                Tags = item.Tags,
            };
        }

        return stored with
        {
            Title = stored.Title ?? item.Title ?? item.Name,
            Description = stored.Description ?? item.Description,
            Tags = stored.Tags.Count > 0 ? stored.Tags : item.Tags,
        };
    }

    /// <summary>
    /// Validates a page's readiness for a DCAT/data.json export. Required fields
    /// (title, description, publisher, contact, license) produce <c>error</c>
    /// findings that fail the export; recommended fields produce <c>warning</c>
    /// findings that document validation exceptions without blocking.
    /// </summary>
    public static ConsoleOpenDataValidationResult ValidateForDcat(ConsoleOpenDataPage page)
    {
        var issues = new List<ConsoleOpenDataValidationIssue>();

        if (string.IsNullOrWhiteSpace(page.Title))
        {
            issues.Add(Error("title", "A dataset title is required for DCAT-US export."));
        }

        if (string.IsNullOrWhiteSpace(page.Description))
        {
            issues.Add(Error("description", "A dataset description is required for DCAT-US export."));
        }

        if (string.IsNullOrWhiteSpace(page.PublisherName))
        {
            issues.Add(Error("publisherName", "A publisher name is required for DCAT-US export."));
        }

        if (string.IsNullOrWhiteSpace(page.ContactName))
        {
            issues.Add(Error("contactName", "A point-of-contact name is required for DCAT-US export."));
        }

        if (string.IsNullOrWhiteSpace(page.ContactEmail))
        {
            issues.Add(Error("contactEmail", "A point-of-contact email is required for DCAT-US export."));
        }
        else if (!IsLikelyEmail(page.ContactEmail))
        {
            issues.Add(Error("contactEmail", "The point-of-contact email is not a valid email address."));
        }

        if (string.IsNullOrWhiteSpace(page.License))
        {
            issues.Add(Error("license", "A license is required for DCAT-US export."));
        }

        // Recommended-but-not-required fields.
        if (page.Distributions.Count == 0)
        {
            issues.Add(Warning("distributions", "No distributions are defined; harvesters cannot link to the data."));
        }
        else
        {
            for (var i = 0; i < page.Distributions.Count; i++)
            {
                if (!IsLikelyHttpUrl(page.Distributions[i].AccessUrl))
                {
                    issues.Add(Error($"distributions[{i}].accessUrl", "Distribution access URL must be an absolute http(s) URL."));
                }
            }
        }

        if (page.SpatialExtent is null)
        {
            issues.Add(Warning("spatialExtent", "No spatial extent is set; the dataset will lack spatial coverage metadata."));
        }
        else if (!IsValidExtent(page.SpatialExtent))
        {
            issues.Add(Error("spatialExtent", "Spatial extent is out of range: longitudes must be within [-180,180] and latitudes within [-90,90], with west<=east and south<=north."));
        }

        if (!string.IsNullOrWhiteSpace(page.LandingPage) && !IsLikelyHttpUrl(page.LandingPage))
        {
            issues.Add(Error("landingPage", "Landing page must be an absolute http(s) URL."));
        }

        var hasError = issues.Exists(issue => issue.Severity == ConsoleOpenDataValidationSeverity.Error);
        return new ConsoleOpenDataValidationResult
        {
            IsValid = !hasError,
            Issues = issues,
        };
    }

    /// <summary>
    /// Builds a single-dataset DCAT-US 3.0 / data.json catalog from a page.
    /// </summary>
    public static DcatCatalog BuildDcatCatalog(ConsoleOpenDataPage page)
        => new()
        {
            Dataset = new[] { BuildDcatDataset(page) },
        };

    private static DcatDataset BuildDcatDataset(ConsoleOpenDataPage page)
        => new()
        {
            Identifier = page.ItemId,
            Title = page.Title ?? page.ItemId,
            Description = page.Description,
            Keyword = page.Tags.Count > 0 ? page.Tags : null,
            Modified = page.UpdatedAt?.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
            Publisher = string.IsNullOrWhiteSpace(page.PublisherName) ? null : new DcatPublisher { Name = page.PublisherName },
            ContactPoint = string.IsNullOrWhiteSpace(page.ContactName)
                ? null
                : new DcatContactPoint
                {
                    Fn = page.ContactName,
                    HasEmail = string.IsNullOrWhiteSpace(page.ContactEmail) ? null : $"mailto:{page.ContactEmail}",
                },
            License = page.License,
            LandingPage = page.LandingPage,
            Spatial = page.SpatialExtent is { } extent ? FormatBboxString(extent) : null,
            Temporal = FormatTemporalInterval(page.TemporalExtent),
            Distribution = page.Distributions.Count == 0
                ? null
                : page.Distributions
                    .Select(d => new DcatDistribution
                    {
                        Title = d.Title,
                        AccessUrl = d.AccessUrl,
                        MediaType = d.MediaType,
                        Format = d.Format,
                    })
                    .ToArray(),
        };

    /// <summary>
    /// Builds a STAC Collection projection for a published item.
    /// </summary>
    public static StacProjectionCollection BuildStacCollection(
        ConsoleOpenDataPage page,
        ConsoleStacPublicationState publication,
        string basePath)
    {
        var collectionId = publication.CollectionId ?? page.ItemId;
        var assets = BuildStacAssets(page);
        return new StacProjectionCollection
        {
            Id = collectionId,
            Title = page.Title,
            Description = page.Description ?? page.Title ?? collectionId,
            License = string.IsNullOrWhiteSpace(page.License) ? "other" : page.License,
            Keywords = page.Tags.Count > 0 ? page.Tags : null,
            Extent = BuildStacExtent(page),
            Links = new[]
            {
                new StacProjectionLink { Rel = "self", Href = $"{basePath}/collections/{collectionId}" },
                new StacProjectionLink { Rel = "root", Href = basePath },
                new StacProjectionLink { Rel = "parent", Href = basePath },
                new StacProjectionLink { Rel = "item", Href = $"{basePath}/collections/{collectionId}/items/{collectionId}" },
            },
            Assets = assets,
        };
    }

    /// <summary>
    /// Builds the representative STAC Item projection for a published item.
    /// </summary>
    public static StacProjectionItem BuildStacItem(
        ConsoleOpenDataPage page,
        ConsoleStacPublicationState publication,
        string basePath)
    {
        var collectionId = publication.CollectionId ?? page.ItemId;
        var bbox = page.SpatialExtent is { } e ? new[] { e.West, e.South, e.East, e.North } : null;
        var datetime = page.TemporalExtent?.End
            ?? page.TemporalExtent?.Start
            ?? publication.FirstPublishedAt
            ?? page.UpdatedAt;

        var properties = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["datetime"] = datetime?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrWhiteSpace(page.Title))
        {
            properties["title"] = page.Title;
        }
        if (!string.IsNullOrWhiteSpace(page.Description))
        {
            properties["description"] = page.Description;
        }

        return new StacProjectionItem
        {
            Id = collectionId,
            Collection = collectionId,
            Bbox = bbox,
            Geometry = page.SpatialExtent is { } extent ? BuildBboxPolygon(extent) : null,
            Properties = properties,
            Links = new[]
            {
                new StacProjectionLink { Rel = "self", Href = $"{basePath}/collections/{collectionId}/items/{collectionId}" },
                new StacProjectionLink { Rel = "root", Href = basePath },
                new StacProjectionLink { Rel = "parent", Href = $"{basePath}/collections/{collectionId}" },
                new StacProjectionLink { Rel = "collection", Href = $"{basePath}/collections/{collectionId}" },
            },
            Assets = BuildStacAssets(page),
        };
    }

    /// <summary>
    /// Builds the STAC Catalog root projection over the supplied published items.
    /// </summary>
    public static StacProjectionCatalog BuildStacCatalog(
        IReadOnlyList<(ConsoleStacPublicationState Publication, ConsoleOpenDataPage Page)> published,
        string basePath)
    {
        var links = new List<StacProjectionLink>
        {
            new() { Rel = "self", Href = basePath },
            new() { Rel = "root", Href = basePath },
        };
        foreach (var (publication, page) in published)
        {
            var collectionId = publication.CollectionId ?? page.ItemId;
            links.Add(new StacProjectionLink
            {
                Rel = "child",
                Href = $"{basePath}/collections/{collectionId}",
                Title = page.Title,
            });
        }

        return new StacProjectionCatalog
        {
            Id = "honua-open-data",
            Title = "Honua Open Data Catalog",
            Description = "STAC catalog of Honua open-data datasets.",
            Links = links,
        };
    }

    /// <summary>
    /// Builds a Schema.org Dataset JSON-LD projection from a page.
    /// </summary>
    public static SchemaOrgDataset BuildSchemaOrgDataset(ConsoleOpenDataPage page)
        => new()
        {
            Name = page.Title ?? page.ItemId,
            Description = page.Description,
            Keywords = page.Tags.Count > 0 ? page.Tags : null,
            License = page.License,
            Url = page.LandingPage,
            SpatialCoverage = page.SpatialExtent is { } e
                ? new SchemaOrgPlace
                {
                    Geo = new SchemaOrgGeoShape
                    {
                        // Schema.org box order is "minLat minLon maxLat maxLon".
                        Box = string.Create(CultureInfo.InvariantCulture, $"{e.South} {e.West} {e.North} {e.East}"),
                    },
                }
                : null,
            TemporalCoverage = FormatTemporalInterval(page.TemporalExtent),
            Publisher = string.IsNullOrWhiteSpace(page.PublisherName) ? null : new SchemaOrgOrganization { Name = page.PublisherName },
            Distribution = page.Distributions.Count == 0
                ? null
                : page.Distributions
                    .Select(d => new SchemaOrgDataDownload
                    {
                        ContentUrl = d.AccessUrl,
                        EncodingFormat = d.MediaType ?? d.Format,
                        Name = d.Title,
                    })
                    .ToArray(),
        };

    private static StacProjectionExtent BuildStacExtent(ConsoleOpenDataPage page)
    {
        var bbox = page.SpatialExtent is { } e
            ? new[] { new[] { e.West, e.South, e.East, e.North } }
            : new[] { new[] { -180.0, -90.0, 180.0, 90.0 } };

        var interval = new[]
        {
            new[]
            {
                page.TemporalExtent?.Start?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                page.TemporalExtent?.End?.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            },
        };

        return new StacProjectionExtent
        {
            Spatial = new StacProjectionSpatialExtent { Bbox = bbox },
            Temporal = new StacProjectionTemporalExtent { Interval = interval },
        };
    }

    private static Dictionary<string, StacProjectionAsset>? BuildStacAssets(ConsoleOpenDataPage page)
    {
        if (page.Distributions.Count == 0)
        {
            return null;
        }

        var assets = new Dictionary<string, StacProjectionAsset>(StringComparer.Ordinal);
        for (var i = 0; i < page.Distributions.Count; i++)
        {
            var d = page.Distributions[i];
            var key = string.IsNullOrWhiteSpace(d.Format)
                ? $"data-{i}"
                : SanitizeAssetKey(d.Format!, i);
            assets[key] = new StacProjectionAsset
            {
                Href = d.AccessUrl,
                Title = d.Title,
                Type = d.MediaType,
                Roles = new[] { "data" },
            };
        }

        return assets;
    }

    private static StacProjectionGeometry BuildBboxPolygon(ConsoleSpatialExtent e)
        => new()
        {
            Coordinates = new[]
            {
                new[]
                {
                    new[] { e.West, e.South },
                    new[] { e.East, e.South },
                    new[] { e.East, e.North },
                    new[] { e.West, e.North },
                    new[] { e.West, e.South },
                },
            },
        };

    private static string FormatBboxString(ConsoleSpatialExtent e)
        => string.Create(CultureInfo.InvariantCulture, $"{e.West},{e.South},{e.East},{e.North}");

    private static string? FormatTemporalInterval(ConsoleTemporalExtent? temporal)
    {
        if (temporal is null || (temporal.Start is null && temporal.End is null))
        {
            return null;
        }

        var start = temporal.Start?.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture) ?? string.Empty;
        var end = temporal.End?.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture) ?? string.Empty;
        return $"{start}/{end}";
    }

    private static string SanitizeAssetKey(string format, int index)
    {
        Span<char> buffer = stackalloc char[format.Length];
        var length = 0;
        foreach (var ch in format)
        {
            buffer[length++] = char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-';
        }

        var key = new string(buffer[..length]).Trim('-');
        return string.IsNullOrEmpty(key) ? $"data-{index}" : key;
    }

    private static bool IsValidExtent(ConsoleSpatialExtent e)
        => e.West is >= -180 and <= 180
            && e.East is >= -180 and <= 180
            && e.South is >= -90 and <= 90
            && e.North is >= -90 and <= 90
            && e.West <= e.East
            && e.South <= e.North;

    private static bool IsLikelyEmail(string value)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at < value.Length - 1 && value.IndexOf('.', at) > at;
    }

    private static bool IsLikelyHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static ConsoleOpenDataValidationIssue Error(string field, string message)
        => new() { Field = field, Severity = ConsoleOpenDataValidationSeverity.Error, Message = message };

    private static ConsoleOpenDataValidationIssue Warning(string field, string message)
        => new() { Field = field, Severity = ConsoleOpenDataValidationSeverity.Warning, Message = message };
}
