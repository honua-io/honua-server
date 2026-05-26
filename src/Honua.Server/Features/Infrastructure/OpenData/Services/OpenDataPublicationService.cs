// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Text;
using Honua.Core.Features.Console.Abstractions;
using Honua.Core.Features.Console.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.OpenData.Abstractions;
using Honua.Core.Features.OpenData.Domain;
using Honua.Server.Features.Console;
using Honua.Server.Features.Infrastructure.OpenData;
using Honua.Server.Features.Infrastructure.OpenData.Models;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.OpenData.Services;

/// <summary>
/// Shared open-data, DCAT, Schema.org, and STAC publication application service.
/// </summary>
internal sealed class OpenDataPublicationService
{
    private const int DefaultLimit = 25;
    private const int MaxLimit = 200;
    private const string Wgs84 = "EPSG:4326";
    private readonly IConsoleContentStore _contentStore;
    private readonly IOpenDataStore _openDataStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpenDataPublicationService> _logger;
    private readonly OpenDataPublicationOptions _options;

    public OpenDataPublicationService(
        IConsoleContentStore contentStore,
        IOpenDataStore openDataStore,
        TimeProvider timeProvider,
        ILogger<OpenDataPublicationService> logger,
        IOptions<OpenDataPublicationOptions> options)
    {
        _contentStore = contentStore;
        _openDataStore = openDataStore;
        _timeProvider = timeProvider;
        _logger = logger;
        _options = options.Value;
    }

    public async Task<OpenDataEligibility> EvaluateEligibilityAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var item = await _contentStore.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return new OpenDataEligibility
            {
                ItemId = itemId,
                IsEligible = false,
                Reasons =
                [
                    Issue("ItemNotFound", "The requested Console content item was not found.")
                ]
            };
        }

        var page = await _openDataStore.GetPageRecordAsync(itemId, cancellationToken).ConfigureAwait(false);
        return EvaluateEligibility(item, page);
    }

    public async Task<OpenDataPageAdminResponse?> GetAdminPageAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        var item = await _contentStore.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var stored = await _openDataStore.GetPageRecordAsync(itemId, cancellationToken).ConfigureAwait(false);
        var page = stored ?? BuildDefaultPage(item);
        return new OpenDataPageAdminResponse
        {
            ItemId = itemId,
            Eligibility = EvaluateEligibility(item, page),
            Page = page
        };
    }

    public async Task<OpenDataPageAdminResponse?> UpdatePageAsync(
        string itemId,
        OpenDataPageUpdateRequest request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default)
    {
        var item = await _contentStore.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var existing = await _openDataStore.GetPageRecordAsync(itemId, cancellationToken).ConfigureAwait(false)
                       ?? BuildDefaultPage(item);
        var now = _timeProvider.GetUtcNow();
        var updated = existing with
        {
            Title = request.Title is null ? existing.Title : NormalizeOptional(request.Title),
            Description = request.Description is null ? existing.Description : NormalizeOptional(request.Description),
            Publisher = request.Publisher is null ? existing.Publisher : NormalizeOrganization(request.Publisher),
            ContactPoint = request.ContactPoint is null ? existing.ContactPoint : NormalizeContact(request.ContactPoint),
            License = request.License is null ? existing.License : NormalizeOptional(request.License),
            Tags = request.Tags is null ? existing.Tags : NormalizeStrings(request.Tags),
            LandingPage = request.LandingPage is null ? existing.LandingPage : NormalizeOptional(request.LandingPage),
            Distributions = request.Distributions is null ? existing.Distributions : NormalizeDistributions(request.Distributions),
            SpatialCoverage = request.SpatialCoverage ?? existing.SpatialCoverage,
            TemporalCoverage = request.TemporalCoverage ?? existing.TemporalCoverage,
            ProvenanceReferences = request.ProvenanceReferences is null ? existing.ProvenanceReferences : request.ProvenanceReferences.ToArray(),
            IsPublished = request.IsPublished ?? existing.IsPublished,
            UpdatedAt = now,
            UpdatedBy = ConsolePrincipal.ResolveActorId(principal)
        };

        var stored = await _openDataStore.SetPageRecordAsync(updated, cancellationToken).ConfigureAwait(false);
        OpenDataLog.PageUpdated(_logger, itemId, stored.IsPublished);
        return new OpenDataPageAdminResponse
        {
            ItemId = itemId,
            Eligibility = EvaluateEligibility(item, stored),
            Page = stored
        };
    }

    public async Task<OpenDataItemResponse?> GetPublicItemAsync(
        string itemId,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var item = await _contentStore.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var page = await _openDataStore.GetPageRecordAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (!IsAnonymousReadable(item, page))
        {
            return null;
        }

        var validation = ValidateDcat(item, page!);
        if (!validation.IsValid)
        {
            return null;
        }

        return MapPublicItem(item, page!, baseUrl);
    }

    public async Task<OpenDataListResponse> ListPublicItemsAsync(
        string baseUrl,
        string? cursor,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var effectiveLimit = limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);
        var records = await _openDataStore.ListPageRecordsAsync(cancellationToken).ConfigureAwait(false);
        var visible = new List<OpenDataItemResponse>();
        foreach (var page in records.Where(static page => page.IsPublished))
        {
            var item = await _contentStore.GetAsync(page.ItemId, cancellationToken).ConfigureAwait(false);
            if (item is null || !IsAnonymousReadable(item, page) || !ValidateDcat(item, page).IsValid)
            {
                continue;
            }

            visible.Add(MapPublicItem(item, page, baseUrl));
        }

        visible.Sort(static (left, right) => string.CompareOrdinal(left.ItemId, right.ItemId));
        var startIndex = ResolveCursorIndex(cursor, visible.Count);
        var pageItems = visible.Skip(startIndex).Take(effectiveLimit).ToArray();
        var nextCursor = startIndex + pageItems.Length < visible.Count
            ? EncodeCursor(startIndex + pageItems.Length)
            : null;
        return new OpenDataListResponse
        {
            Items = pageItems,
            Total = visible.Count,
            NextCursor = nextCursor
        };
    }

    public async Task<DcatCatalogResponse> BuildDcatCatalogAsync(
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        var list = await ListPublicItemsAsync(baseUrl, cursor: null, limit: MaxLimit, cancellationToken)
            .ConfigureAwait(false);
        var datasets = list.Items
            .Select(item => MapDcatDataset(item, baseUrl))
            .ToArray();

        OpenDataLog.DcatCatalogGenerated(_logger, datasets.Length);
        return new DcatCatalogResponse
        {
            Dataset = datasets,
            GeneratedAt = _timeProvider.GetUtcNow()
        };
    }

    public async Task<OpenDataValidationSummary> ValidateDcatAsync(
        string? itemId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<OpenDataValidationResult>();
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            var item = await _contentStore.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                results.Add(new OpenDataValidationResult
                {
                    ItemId = itemId,
                    IsValid = false,
                    Issues =
                    [
                        Issue("ItemNotFound", "The requested Console content item was not found.")
                    ]
                });
            }
            else
            {
                var page = await _openDataStore.GetPageRecordAsync(itemId, cancellationToken).ConfigureAwait(false)
                           ?? BuildDefaultPage(item);
                results.Add(ValidateDcat(item, page));
            }
        }
        else
        {
            var records = await _openDataStore.ListPageRecordsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var page in records.Where(static record => record.IsPublished))
            {
                var item = await _contentStore.GetAsync(page.ItemId, cancellationToken).ConfigureAwait(false);
                if (item is not null)
                {
                    results.Add(ValidateDcat(item, page));
                }
            }
        }

        var exceptionCount = results.Sum(static result => result.Issues.Count(issue => issue.DocumentedException));
        return new OpenDataValidationSummary
        {
            IsValid = results.All(static result => result.IsValid),
            ItemCount = results.Count,
            ValidationExceptionCount = exceptionCount,
            Items = results
        };
    }

    public async Task<StacPublicationOperationResult> PublishStacAsync(
        StacPublicationPublishRequest request,
        string baseUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ItemId))
        {
            return StacPublicationOperationResult.NotFound();
        }

        var item = await _contentStore.GetAsync(request.ItemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return StacPublicationOperationResult.NotFound();
        }

        var page = await _openDataStore.GetPageRecordAsync(request.ItemId, cancellationToken).ConfigureAwait(false);
        var eligibility = EvaluateEligibility(item, page);
        if (!eligibility.IsEligible)
        {
            return StacPublicationOperationResult.Ineligible(eligibility);
        }

        var collectionId = CreateCollectionId(request.CollectionId, item);
        var existing = await _openDataStore.GetStacPublicationAsync(collectionId, cancellationToken).ConfigureAwait(false);
        if (existing is { Status: OpenDataStacPublicationStatus.Published })
        {
            return StacPublicationOperationResult.Conflict(existing, eligibility);
        }

        var now = _timeProvider.GetUtcNow();
        var record = new OpenDataStacPublicationRecord
        {
            CollectionId = collectionId,
            ItemId = item.Id,
            Status = OpenDataStacPublicationStatus.Published,
            PublicStacCollectionUrl = $"{baseUrl}/stac/collections/{Uri.EscapeDataString(collectionId)}",
            Title = NormalizeOptional(request.Title) ?? ResolveTitle(item, page),
            Description = NormalizeOptional(request.Description) ?? ResolveDescription(item, page),
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        };

        var stored = await _openDataStore.SetStacPublicationAsync(record, cancellationToken).ConfigureAwait(false);
        OpenDataLog.StacPublicationChanged(_logger, "publish", stored.CollectionId, stored.Status);
        return StacPublicationOperationResult.Success(stored, eligibility);
    }

    public async Task<IReadOnlyList<OpenDataStacPublicationProjection>> ListPublicStacPublicationsAsync(
        CancellationToken cancellationToken = default)
    {
        var records = await _openDataStore.ListStacPublicationsAsync(cancellationToken).ConfigureAwait(false);
        var visible = new List<OpenDataStacPublicationProjection>();
        foreach (var record in records)
        {
            var projection = await ResolvePublicStacPublicationAsync(record, cancellationToken).ConfigureAwait(false);
            if (projection is not null)
            {
                visible.Add(projection.Value);
            }
        }

        visible.Sort(static (left, right) =>
            string.CompareOrdinal(left.Record.CollectionId, right.Record.CollectionId));
        return visible;
    }

    public async Task<OpenDataStacPublicationProjection?> GetPublicStacPublicationAsync(
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        var record = await _openDataStore.GetStacPublicationAsync(collectionId, cancellationToken).ConfigureAwait(false);
        return record is null
            ? null
            : await ResolvePublicStacPublicationAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StacPublicationOperationResult> UpdateStacAsync(
        string collectionId,
        StacPublicationUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await _openDataStore.GetStacPublicationAsync(collectionId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return StacPublicationOperationResult.NotFound();
        }

        var item = await _contentStore.GetAsync(existing.ItemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return StacPublicationOperationResult.NotFound();
        }

        var page = await _openDataStore.GetPageRecordAsync(existing.ItemId, cancellationToken).ConfigureAwait(false);
        var eligibility = EvaluateEligibility(item, page);
        var updated = existing with
        {
            Title = request.Title is null ? existing.Title : NormalizeOptional(request.Title),
            Description = request.Description is null ? existing.Description : NormalizeOptional(request.Description),
            UpdatedAt = _timeProvider.GetUtcNow()
        };
        var stored = await _openDataStore.SetStacPublicationAsync(updated, cancellationToken).ConfigureAwait(false);
        OpenDataLog.StacPublicationChanged(_logger, "update", stored.CollectionId, stored.Status);
        return StacPublicationOperationResult.Success(stored, eligibility);
    }

    public async Task<StacPublicationOperationResult> UnpublishStacAsync(
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        var existing = await _openDataStore.GetStacPublicationAsync(collectionId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return StacPublicationOperationResult.NotFound();
        }

        var updated = existing with
        {
            Status = OpenDataStacPublicationStatus.Unpublished,
            UpdatedAt = _timeProvider.GetUtcNow()
        };
        var stored = await _openDataStore.SetStacPublicationAsync(updated, cancellationToken).ConfigureAwait(false);
        OpenDataLog.StacPublicationChanged(_logger, "unpublish", stored.CollectionId, stored.Status);
        return StacPublicationOperationResult.Success(stored, null);
    }

    public async Task<StacPublicationStatusResponse?> GetStacStatusAsync(
        string collectionId,
        CancellationToken cancellationToken = default)
    {
        var record = await _openDataStore.GetStacPublicationAsync(collectionId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        OpenDataEligibility? eligibility = null;
        var item = await _contentStore.GetAsync(record.ItemId, cancellationToken).ConfigureAwait(false);
        if (item is not null)
        {
            var page = await _openDataStore.GetPageRecordAsync(record.ItemId, cancellationToken).ConfigureAwait(false);
            eligibility = EvaluateEligibility(item, page);
        }

        return MapStacStatus(record, eligibility);
    }

    private async Task<OpenDataStacPublicationProjection?> ResolvePublicStacPublicationAsync(
        OpenDataStacPublicationRecord record,
        CancellationToken cancellationToken)
    {
        if (record.Status != OpenDataStacPublicationStatus.Published)
        {
            return null;
        }

        var item = await _contentStore.GetAsync(record.ItemId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var page = await _openDataStore.GetPageRecordAsync(record.ItemId, cancellationToken).ConfigureAwait(false);
        return EvaluateEligibility(item, page).IsEligible
            ? new OpenDataStacPublicationProjection(record, page)
            : null;
    }

    internal static StacPublicationStatusResponse MapStacStatus(
        OpenDataStacPublicationRecord record,
        OpenDataEligibility? eligibility)
    {
        return new StacPublicationStatusResponse
        {
            CollectionId = record.CollectionId,
            ItemId = record.ItemId,
            Status = record.Status,
            PublicStacCollectionUrl = record.PublicStacCollectionUrl,
            Title = record.Title,
            Description = record.Description,
            Eligibility = eligibility,
            UpdatedAt = record.UpdatedAt
        };
    }

    private OpenDataEligibility EvaluateEligibility(ConsoleContentItem item, OpenDataPageRecord? page)
    {
        var reasons = new List<OpenDataIssue>();
        var warnings = new List<OpenDataIssue>();

        if (!_options.Enabled)
        {
            reasons.Add(Issue(
                "OpenDataDisabled",
                "Open-data publication is disabled for this deployment.",
                "openData.enabled"));
        }

        var title = ResolveTitle(item, page);
        if (string.IsNullOrWhiteSpace(title))
        {
            reasons.Add(Issue("MissingTitle", "Open-data publication requires a title.", "title"));
        }

        if (item.Lifecycle is MetadataV2LifecycleStatus.Archived or MetadataV2LifecycleStatus.Retired)
        {
            reasons.Add(Issue("LifecycleBlocked", "Archived or retired items cannot be published.", "lifecycle"));
        }

        if (item.Visibility != ConsoleVisibility.Public)
        {
            reasons.Add(Issue("PolicyBlocked", "Anonymous reads require public Console visibility.", "visibility"));
        }

        if (HasBlockingComplianceFlag(item))
        {
            reasons.Add(Issue("ComplianceBlocked", "An active compliance or legal-hold flag blocks open-data publication.", "labels"));
        }

        if (string.IsNullOrWhiteSpace(ResolveDescription(item, page)))
        {
            warnings.Add(Issue("MissingDescription", "No description is available for DCAT publication.", "description", documentedException: true));
        }

        if (string.IsNullOrWhiteSpace(page?.License))
        {
            warnings.Add(Issue("MissingLicense", "No license is configured for DCAT publication.", "license", documentedException: true));
        }

        return new OpenDataEligibility
        {
            ItemId = item.Id,
            IsEligible = reasons.Count == 0,
            Reasons = reasons,
            Warnings = warnings
        };
    }

    private static OpenDataValidationResult ValidateDcat(ConsoleContentItem item, OpenDataPageRecord page)
    {
        var issues = new List<OpenDataIssue>();
        if (string.IsNullOrWhiteSpace(ResolveTitle(item, page)))
        {
            issues.Add(Issue("RequiredFieldMissing", "DCAT title is required.", "title"));
        }

        if (string.IsNullOrWhiteSpace(ResolveDescription(item, page)))
        {
            issues.Add(Issue("RequiredFieldMissing", "DCAT description is missing; Console treats this as a documented exception.", "description", documentedException: true));
        }

        if (page.Tags.Count == 0 && item.Tags.Count == 0)
        {
            issues.Add(Issue("RequiredFieldMissing", "DCAT keyword is missing; Console treats this as a documented exception.", "keyword", documentedException: true));
        }

        if (page.Publisher is null || string.IsNullOrWhiteSpace(page.Publisher.Name))
        {
            issues.Add(Issue("RequiredFieldMissing", "DCAT publisher is missing; Console treats this as a documented exception.", "publisher", documentedException: true));
        }

        if (page.ContactPoint is null || string.IsNullOrWhiteSpace(page.ContactPoint.Email))
        {
            issues.Add(Issue("RequiredFieldMissing", "DCAT contactPoint email is missing; Console treats this as a documented exception.", "contactPoint", documentedException: true));
        }

        if (string.IsNullOrWhiteSpace(page.License))
        {
            issues.Add(Issue("RequiredFieldMissing", "DCAT license is missing; Console treats this as a documented exception.", "license", documentedException: true));
        }

        if (page.Distributions.Count == 0)
        {
            issues.Add(Issue("RequiredFieldMissing", "No distribution links are configured; Console treats this as a documented exception.", "distribution", documentedException: true));
        }

        if (page.SpatialCoverage is { } spatial)
        {
            ValidateSpatial(spatial, issues);
        }

        if (page.TemporalCoverage is { Start: { } start, End: { } end } && start > end)
        {
            issues.Add(Issue("InvalidTemporalExtent", "Temporal coverage start must be earlier than or equal to end.", "temporalCoverage"));
        }

        return new OpenDataValidationResult
        {
            ItemId = item.Id,
            IsValid = issues.All(static issue => issue.DocumentedException),
            Issues = issues
        };
    }

    private static void ValidateSpatial(OpenDataSpatialExtent spatial, List<OpenDataIssue> issues)
    {
        if (!string.Equals(spatial.Crs, Wgs84, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(Issue("UnsupportedCrs", "Open-data spatial coverage must be expressed in EPSG:4326.", "spatialCoverage.crs"));
        }

        if (spatial.MinX > spatial.MaxX || spatial.MinY > spatial.MaxY)
        {
            issues.Add(Issue("InvalidBoundingBox", "Spatial coverage min values must be less than or equal to max values.", "spatialCoverage"));
        }

        if (spatial.MinX < -180 || spatial.MaxX > 180 || spatial.MinY < -90 || spatial.MaxY > 90)
        {
            issues.Add(Issue("InvalidCoordinateRange", "EPSG:4326 spatial coverage must be within longitude/latitude bounds.", "spatialCoverage"));
        }
    }

    private bool IsAnonymousReadable(ConsoleContentItem item, OpenDataPageRecord? page)
    {
        return page is { IsPublished: true } && EvaluateEligibility(item, page).IsEligible;
    }

    private static OpenDataPageRecord BuildDefaultPage(ConsoleContentItem item)
    {
        return new OpenDataPageRecord
        {
            ItemId = item.Id,
            Title = item.Title,
            Description = item.Description,
            Tags = item.Tags,
            ProvenanceReferences = item.Provenance,
            IsPublished = false,
            UpdatedAt = item.UpdatedAt ?? item.CreatedAt ?? DateTimeOffset.UnixEpoch,
            UpdatedBy = item.UpdatedById
        };
    }

    private static OpenDataItemResponse MapPublicItem(
        ConsoleContentItem item,
        OpenDataPageRecord page,
        string baseUrl)
    {
        var title = ResolveTitle(item, page);
        var description = ResolveDescription(item, page);
        var landingPage = page.LandingPage ?? $"{baseUrl}/open-data/{Uri.EscapeDataString(item.Id)}";
        var distributions = page.Distributions;
        return new OpenDataItemResponse
        {
            ItemId = item.Id,
            ItemType = item.ItemType,
            Title = title,
            Description = description,
            Publisher = page.Publisher,
            ContactPoint = page.ContactPoint,
            License = page.License,
            Tags = page.Tags.Count > 0 ? page.Tags : item.Tags,
            LandingPage = landingPage,
            Distributions = distributions,
            SpatialCoverage = page.SpatialCoverage,
            TemporalCoverage = page.TemporalCoverage,
            ProvenanceReferences = page.ProvenanceReferences,
            UpdatedAt = page.UpdatedAt,
            SchemaOrg = new SchemaOrgDatasetResponse
            {
                Name = title,
                Description = description,
                Url = landingPage,
                License = page.License,
                Keywords = page.Tags.Count > 0 ? page.Tags : item.Tags,
                Publisher = page.Publisher,
                Distribution = distributions.Select(MapSchemaOrgDistribution).ToArray()
            }
        };
    }

    private static SchemaOrgDistributionResponse MapSchemaOrgDistribution(OpenDataDistribution distribution)
    {
        return new SchemaOrgDistributionResponse
        {
            Name = distribution.Title,
            ContentUrl = FirstNonEmpty(distribution.DownloadUrl, distribution.AccessUrl, distribution.ServiceUrl),
            EncodingFormat = FirstNonEmpty(distribution.MediaType, distribution.Format)
        };
    }

    private static DcatDatasetResponse MapDcatDataset(OpenDataItemResponse item, string baseUrl)
    {
        return new DcatDatasetResponse
        {
            Identifier = $"{baseUrl}/open-data/{Uri.EscapeDataString(item.ItemId)}",
            Title = item.Title,
            Description = item.Description,
            Publisher = item.Publisher,
            ContactPoint = item.ContactPoint,
            License = item.License,
            Keyword = item.Tags,
            LandingPage = item.LandingPage,
            Modified = item.UpdatedAt,
            Spatial = item.SpatialCoverage is null ? null : MapDcatSpatial(item.SpatialCoverage),
            Temporal = item.TemporalCoverage,
            Distribution = item.Distributions.Select(MapDcatDistribution).ToArray()
        };
    }

    private static DcatSpatialResponse MapDcatSpatial(OpenDataSpatialExtent spatial)
    {
        return new DcatSpatialResponse
        {
            Coordinates =
            [
                [
                    [
                        [spatial.MinX, spatial.MinY],
                        [spatial.MinX, spatial.MaxY],
                        [spatial.MaxX, spatial.MaxY],
                        [spatial.MaxX, spatial.MinY],
                        [spatial.MinX, spatial.MinY]
                    ]
                ]
            ]
        };
    }

    private static DcatDistributionResponse MapDcatDistribution(OpenDataDistribution distribution)
    {
        return new DcatDistributionResponse
        {
            Title = distribution.Title,
            Description = distribution.Description,
            MediaType = distribution.MediaType,
            Format = distribution.Format,
            AccessUrl = FirstNonEmpty(distribution.AccessUrl, distribution.ServiceUrl, distribution.DownloadUrl),
            DownloadUrl = distribution.DownloadUrl
        };
    }

    private static string CreateCollectionId(string? requestedCollectionId, ConsoleContentItem item)
    {
        var source = NormalizeOptional(requestedCollectionId)
                     ?? NormalizeOptional(item.Name)
                     ?? item.Id;
        var builder = new StringBuilder(source.Length);
        var previousWasDash = false;
        foreach (var ch in source)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousWasDash = false;
            }
            else if (!previousWasDash)
            {
                builder.Append('-');
                previousWasDash = true;
            }
        }

        var candidate = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = item.Id;
        }

        return candidate.Length <= 120 ? candidate : candidate[..120].Trim('-');
    }

    private static OpenDataIssue Issue(
        string code,
        string message,
        string? field = null,
        bool documentedException = false)
    {
        return new OpenDataIssue
        {
            Code = code,
            Message = message,
            Field = field,
            DocumentedException = documentedException
        };
    }

    private static string? ResolveTitle(ConsoleContentItem item, OpenDataPageRecord? page)
        => FirstNonEmpty(page?.Title, item.Title);

    private static string? ResolveDescription(ConsoleContentItem item, OpenDataPageRecord? page)
        => FirstNonEmpty(page?.Description, item.Description);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static OpenDataOrganization NormalizeOrganization(OpenDataOrganization source)
    {
        return source with
        {
            Name = NormalizeOptional(source.Name),
            Url = NormalizeOptional(source.Url)
        };
    }

    private static OpenDataContact NormalizeContact(OpenDataContact source)
    {
        return source with
        {
            Name = NormalizeOptional(source.Name),
            Email = NormalizeOptional(source.Email)
        };
    }

    private static string[] NormalizeStrings(IReadOnlyList<string> values)
    {
        return values
            .Select(NormalizeOptional)
            .Where(static value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static OpenDataDistribution[] NormalizeDistributions(IReadOnlyList<OpenDataDistribution> values)
    {
        return values
            .Select(static distribution => distribution with
            {
                Title = NormalizeOptional(distribution.Title),
                Description = NormalizeOptional(distribution.Description),
                MediaType = NormalizeOptional(distribution.MediaType),
                Format = NormalizeOptional(distribution.Format),
                DownloadUrl = NormalizeOptional(distribution.DownloadUrl),
                AccessUrl = NormalizeOptional(distribution.AccessUrl),
                ServiceUrl = NormalizeOptional(distribution.ServiceUrl)
            })
            .Where(static distribution => FirstNonEmpty(distribution.DownloadUrl, distribution.AccessUrl, distribution.ServiceUrl) is not null)
            .ToArray();
    }

    private static bool HasBlockingComplianceFlag(ConsoleContentItem item)
    {
        return item.Labels.Any(static pair =>
            (string.Equals(pair.Key, "legalHold", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(pair.Key, "complianceBlocked", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(pair.Value, "true", StringComparison.OrdinalIgnoreCase));
    }

    private static int ResolveCursorIndex(string? cursor, int count)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            return int.TryParse(decoded, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                ? Math.Clamp(index, 0, count)
                : 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static string EncodeCursor(int index)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(index.ToString(CultureInfo.InvariantCulture)));
    }
}

internal enum StacPublicationOperationStatus
{
    Success,
    NotFound,
    Ineligible,
    Conflict
}

internal readonly record struct StacPublicationOperationResult(
    StacPublicationOperationStatus Status,
    OpenDataStacPublicationRecord? Record,
    OpenDataEligibility? Eligibility)
{
    public static StacPublicationOperationResult Success(
        OpenDataStacPublicationRecord record,
        OpenDataEligibility? eligibility)
        => new(StacPublicationOperationStatus.Success, record, eligibility);

    public static StacPublicationOperationResult NotFound()
        => new(StacPublicationOperationStatus.NotFound, null, null);

    public static StacPublicationOperationResult Ineligible(OpenDataEligibility eligibility)
        => new(StacPublicationOperationStatus.Ineligible, null, eligibility);

    public static StacPublicationOperationResult Conflict(
        OpenDataStacPublicationRecord record,
        OpenDataEligibility eligibility)
        => new(StacPublicationOperationStatus.Conflict, record, eligibility);
}

internal readonly record struct OpenDataStacPublicationProjection(
    OpenDataStacPublicationRecord Record,
    OpenDataPageRecord? Page);
