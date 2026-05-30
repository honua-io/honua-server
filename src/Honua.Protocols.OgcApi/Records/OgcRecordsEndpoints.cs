// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Protocols.Ogc.Api.Records.Models;
using Honua.Server.Features.Protocols.Ogc.Common;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Protocols.Ogc.Api.Records;

/// <summary>
/// Read-only OGC API Records endpoints backed by Honua catalog metadata.
/// </summary>
internal static class OgcRecordsEndpoints
{
    internal const string CatalogCollectionId = "honua-catalog";
    private const int DefaultLimit = 10;
    private const int MaxLimit = 1_000;
    private const string FeatureServerProtocolName = "FeatureServer";
    private const string MapServerProtocolName = "MapServer";
    private const string OgcFeaturesProtocolName = "OgcFeatures";
    private const string StacProtocolName = "Stac";

    private static readonly HashSet<string> MetadataQueryParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "f"
    };

    private static readonly HashSet<string> ItemsQueryParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "f", "bbox", "datetime", "limit", "offset", "q", "ids", "type", "externalIds"
    };

    /// <summary>
    /// Logging category for OGC API Records endpoints.
    /// </summary>
    internal sealed class OgcRecordsEndpointsLog
    {
    }

    /// <summary>
    /// Maps the OGC API Records endpoint family.
    /// </summary>
    public static IEndpointRouteBuilder MapOgcRecordsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ogc/records", HandleGetLandingPage)
            .WithDisplayName("OGC API Records Landing Page")
            .WithName("OgcRecordsLandingPage")
            .WithSummary("Get OGC API Records landing page")
            .WithTags("OGC API Records")
            .Produces<LandingPage>(200, MediaTypes.Json)
            .Produces(400);

        endpoints.MapGet("/ogc/records/conformance", HandleGetConformance)
            .WithDisplayName("OGC API Records Conformance")
            .WithName("OgcRecordsConformance")
            .WithSummary("Get OGC API Records conformance declaration")
            .WithTags("OGC API Records")
            .Produces<ConformanceDeclaration>(200, MediaTypes.Json)
            .Produces(400);

        endpoints.MapGet("/ogc/records/collections", HandleGetCollections)
            .WithDisplayName("OGC API Records Collections")
            .WithName("OgcRecordsCollections")
            .WithSummary("List OGC API Records collections")
            .WithTags("OGC API Records")
            .Produces<OgcRecordCollectionsResponse>(200, MediaTypes.Json)
            .Produces(400);

        endpoints.MapGet("/ogc/records/collections/{collectionId}", HandleGetCollection)
            .WithDisplayName("OGC API Records Collection")
            .WithName("OgcRecordsCollection")
            .WithSummary("Get OGC API Records collection metadata")
            .WithTags("OGC API Records")
            .Produces<OgcRecordCollection>(200, MediaTypes.Json)
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/ogc/records/collections/{collectionId}/items", HandleGetItems)
            .WithDisplayName("OGC API Records Items")
            .WithName("OgcRecordsItems")
            .WithSummary("Search OGC API Records items")
            .WithTags("OGC API Records")
            .Produces<OgcRecordFeatureCollection>(200, MediaTypes.GeoJson)
            .Produces(400)
            .Produces(404);

        endpoints.MapGet("/ogc/records/collections/{collectionId}/items/{recordId}", HandleGetItem)
            .WithDisplayName("OGC API Records Item")
            .WithName("OgcRecordsItem")
            .WithSummary("Get an OGC API Records item by id")
            .WithTags("OGC API Records")
            .Produces<OgcRecordFeature>(200, MediaTypes.GeoJson)
            .Produces(400)
            .Produces(404);

        return endpoints;
    }

    private static IResult HandleGetLandingPage(HttpContext context, string? f)
    {
        if (!TryPrepareMetadataResponse(context, f, out var outputFormat, out var errorResult))
        {
            return errorResult!;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var basePath = $"{baseUrl}/ogc/records";
        var links = OgcCoreMetadataUtilities.BuildLandingPageLinks(context, basePath, outputFormat);
        links.Add(Link.Create($"{basePath}/conformance", RelationTypes.Conformance, MediaTypes.Json, "Conformance declaration"));
        links.Add(Link.Create($"{basePath}/collections", RelationTypes.Data, MediaTypes.Json, "Record collections"));

        var landingPage = new LandingPage
        {
            Title = "Honua OGC API Records",
            Description = "Read-only metadata record discovery for Honua catalog services and layers.",
            Supports3d = false,
            Links = links.ToImmutable()
        };

        return OgcCommonUtilities.FormatMetadataResponse(
            landingPage,
            OgcRecordsJsonContext.Default.LandingPage,
            outputFormat,
            landingPage.Title);
    }

    private static IResult HandleGetConformance(HttpContext context, string? f)
    {
        if (!TryPrepareMetadataResponse(context, f, out var outputFormat, out var errorResult))
        {
            return errorResult!;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var conformance = new ConformanceDeclaration
        {
            ConformsTo = ImmutableArray.Create(
                "http://www.opengis.net/spec/ogcapi-records-1/1.0/conf/core",
                "http://www.opengis.net/spec/ogcapi-records-1/1.0/conf/record-collection",
                "http://www.opengis.net/spec/ogcapi-records-1/1.0/conf/record-core",
                "http://www.opengis.net/spec/ogcapi-records-1/1.0/conf/record-geojson"),
            Links = OgcCoreMetadataUtilities.BuildConformanceLinks(
                context,
                $"{baseUrl}/ogc/records/conformance",
                outputFormat)
        };

        return OgcCommonUtilities.FormatMetadataResponse(
            conformance,
            OgcRecordsJsonContext.Default.ConformanceDeclaration,
            outputFormat,
            "Conformance");
    }

    private static IResult HandleGetCollections(HttpContext context, string? f)
    {
        if (!TryPrepareMetadataResponse(context, f, out var outputFormat, out var errorResult))
        {
            return errorResult!;
        }

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var response = new OgcRecordCollectionsResponse
        {
            Collections = ImmutableArray.Create(CreateCatalogCollection(baseUrl)),
            Links = ImmutableArray.Create(
                Link.Create($"{baseUrl}/ogc/records/collections", RelationTypes.Self, outputFormat, "Record collections"),
                Link.Create($"{baseUrl}/ogc/records", "parent", MediaTypes.Json, "Records landing page"))
        };

        return OgcCommonUtilities.FormatMetadataResponse(
            response,
            OgcRecordsJsonContext.Default.OgcRecordCollectionsResponse,
            outputFormat,
            "Record collections");
    }

    private static IResult HandleGetCollection(string collectionId, HttpContext context, string? f)
    {
        if (!IsCatalogCollection(collectionId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Record collection '{collectionId}' not found.");
        }

        if (!TryPrepareMetadataResponse(context, f, out var outputFormat, out var errorResult))
        {
            return errorResult!;
        }

        var collection = CreateCatalogCollection(BaseUrlResolver.GetBaseUrl(context));
        return OgcCommonUtilities.FormatMetadataResponse(
            collection,
            OgcRecordsJsonContext.Default.OgcRecordCollection,
            outputFormat,
            collection.Title ?? collection.Id);
    }

    private static async Task<IResult> HandleGetItems(
        string collectionId,
        HttpContext context,
        string? f,
        string? bbox,
        string? datetime,
        int? limit,
        int? offset,
        string? q,
        string? ids,
        string? type,
        string? externalIds,
        [FromServices] IMetadataV2GraphProvider graphProvider)
    {
        if (!IsCatalogCollection(collectionId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Record collection '{collectionId}' not found.");
        }

        var validationError = OgcCommonUtilities.ValidateQueryParameters(context.Request, ItemsQueryParameters);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!TryGetGeoJsonOutputFormat(context, f, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, formatError ?? "Invalid format.");
        }

        if (!TryParseLimitOffset(limit, offset, out var pageLimit, out var pageOffset, out var pageError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, pageError!);
        }

        if (!TryParseBbox(bbox, out var bboxFilter, out var bboxError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, bboxError!);
        }

        if (!TryParseDatetime(datetime, out var datetimeFilter, out var datetimeError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, datetimeError!);
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var records = await BuildVisibleRecordsAsync(context, graphProvider, cancellationToken).ConfigureAwait(false);
        var filtered = ApplyFilters(records, bboxFilter, datetimeFilter, q, ids, type, externalIds).ToArray();
        var page = filtered.Skip(pageOffset).Take(pageLimit).Select(record => record.Feature).ToImmutableArray();

        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var links = BuildItemsLinks(context, baseUrl, collectionId, filtered.Length, pageLimit, pageOffset);
        var response = new OgcRecordFeatureCollection
        {
            Features = page,
            NumberMatched = filtered.Length,
            NumberReturned = page.Length,
            Links = links
        };

        return Results.Json(response, OgcRecordsJsonContext.Default.OgcRecordFeatureCollection, MediaTypes.GeoJson);
    }

    private static async Task<IResult> HandleGetItem(
        string collectionId,
        string recordId,
        HttpContext context,
        string? f,
        [FromServices] IMetadataV2GraphProvider graphProvider)
    {
        if (!IsCatalogCollection(collectionId))
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Record collection '{collectionId}' not found.");
        }

        var validationError = OgcCommonUtilities.ValidateQueryParameters(context.Request, MetadataQueryParameters);
        if (validationError is not null)
        {
            return StandardErrorHelpers.CreateBadRequest(context, validationError.Value ?? "Invalid query parameters.");
        }

        if (!TryGetGeoJsonOutputFormat(context, f, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context, formatError ?? "Invalid format.");
        }

        var cancellationToken = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var records = await BuildVisibleRecordsAsync(context, graphProvider, cancellationToken).ConfigureAwait(false);
        var record = records.FirstOrDefault(candidate =>
            string.Equals(candidate.Feature.Id, recordId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return StandardErrorHelpers.CreateNotFound(context, $"Record '{recordId}' not found.");
        }

        return Results.Json(record.Feature, OgcRecordsJsonContext.Default.OgcRecordFeature, MediaTypes.GeoJson);
    }

    private static OgcRecordCollection CreateCatalogCollection(string baseUrl) => new()
    {
        Id = CatalogCollectionId,
        Title = "Honua Catalog Records",
        Description = "Read-only records projected from published Honua services and layers.",
        Links = ImmutableArray.Create(
            Link.Create($"{baseUrl}/ogc/records/collections/{CatalogCollectionId}", RelationTypes.Self, MediaTypes.Json, "Honua Catalog Records"),
            Link.Create($"{baseUrl}/ogc/records/collections/{CatalogCollectionId}/items", RelationTypes.Items, MediaTypes.GeoJson, "Records"),
            Link.Create($"{baseUrl}/ogc/records/collections", "parent", MediaTypes.Json, "Record collections"))
    };

    private static async Task<List<CatalogRecord>> BuildVisibleRecordsAsync(
        HttpContext context,
        IMetadataV2GraphProvider graphProvider,
        CancellationToken cancellationToken)
    {
        var baseUrl = BaseUrlResolver.GetBaseUrl(context);
        var snapshot = await graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var records = new List<CatalogRecord>();

        // Service-level records: one public record per service name. The V2 graph can
        // contain multiple protocol-specific service rows with the same public name
        // (for example FeatureServer/MapServer/STAC views), but Records should expose
        // the public service once with merged protocol links.
        foreach (var serviceGroup in snapshot.Graph.Services
                     .GroupBy(s => s.Metadata.Name, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var services = serviceGroup.ToArray();
            var visiblePublications = services
                .SelectMany(service => snapshot.Index.PublicationsByService[service.Metadata.Id]
                    .Select(p => (Service: service, Publication: p, Resource: snapshot.ResolveResource(p))))
                .Where(t => t.Resource is not null)
                .Where(t => AccessPolicyHelpers.IsResourceAccessible(context, t.Resource!, t.Service))
                .OrderBy(t => snapshot.ResolveStorageLayerId(t.Publication) ?? int.MaxValue)
                .ToArray();
            if (visiblePublications.Length == 0)
            {
                continue;
            }

            var representative = services[0] with
            {
                Protocols = services
                    .SelectMany(service => service.Protocols)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
            records.Add(CreateServiceRecord(
                representative,
                visiblePublications.Select(t => (t.Publication, t.Resource)).ToArray()!,
                snapshot,
                baseUrl));
        }

        // Resource-level records: one per resource, attributed to its primary publication's service.
        var resourceRecords = snapshot.Graph.Resources
            .Select(resource =>
            {
                var publications = snapshot.Index.PublicationsByResource[resource.Metadata.Id].ToArray();
                var primary = publications.FirstOrDefault(p => p.IsPrimary) ?? publications.FirstOrDefault();
                var storageLayerId = primary is not null ? snapshot.ResolveStorageLayerId(primary) : null;
                var layerSort = primary is not null
                    ? storageLayerId ?? primary.LayerIndex ?? int.MaxValue
                    : int.MaxValue;
                return (Resource: resource, Publications: publications, Primary: primary, LayerSort: layerSort, HasStorageLayer: storageLayerId.HasValue);
            })
            .OrderByDescending(item => item.HasStorageLayer)
            .ThenBy(item => item.LayerSort)
            .ThenBy(item => item.Resource.Metadata.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var item in resourceRecords)
        {
            // Pick the primary publication if any (prefer IsPrimary, else first).
            var resource = item.Resource;
            var primary = item.Primary;
            MetadataV2Service? primaryService = primary is not null && snapshot.Index.ServicesById.TryGetValue(primary.ServiceId, out var svc)
                ? svc
                : null;

            if (!AccessPolicyHelpers.IsResourceAccessible(context, resource, primaryService))
            {
                continue;
            }

            records.Add(CreateResourceRecord(resource, primary, primaryService, snapshot, baseUrl));
        }

        return records;
    }

    private static CatalogRecord CreateServiceRecord(
        MetadataV2Service service,
        (MetadataV2Publication Publication, MetadataV2Resource? Resource)[] visiblePublications,
        MetadataV2GraphSnapshot snapshot,
        string baseUrl)
    {
        var serviceMetadata = service.Metadata
            ?? throw new InvalidOperationException("Metadata v2 service metadata is required.");
        // Combine bboxes of visible resources; project into WGS84 only if the resource declares 4326.
        var bbox = CombineResourceBboxes(visiblePublications.Select(p => p.Resource!));
        var servicePath = $"{baseUrl}/rest/services/{Uri.EscapeDataString(serviceMetadata.Name)}";
        var links = ImmutableArray.CreateBuilder<Link>();
        links.Add(Link.Create($"{baseUrl}/ogc/records/collections/{CatalogCollectionId}/items/{Uri.EscapeDataString($"service:{serviceMetadata.Name}")}", RelationTypes.Self, MediaTypes.GeoJson, serviceMetadata.Name));
        if (service is not null && IsProtocolEnabled(service, FeatureServerProtocolName))
        {
            links.Add(Link.Create($"{servicePath}/FeatureServer", RelationTypes.Alternate, MediaTypes.Json, "FeatureServer"));
        }

        if (IsProtocolEnabled(service, MapServerProtocolName))
        {
            links.Add(Link.Create($"{servicePath}/MapServer", RelationTypes.Alternate, MediaTypes.Json, "MapServer"));
        }

        // External-facing layer ids are the storage layer ids (the v1 catalog's layer.Id).
        var layerIds = visiblePublications
            .Select(p => snapshot.ResolveStorageLayerId(p.Publication))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToImmutableArray();

        var properties = new OgcRecordProperties
        {
            Title = serviceMetadata.Title ?? serviceMetadata.Name,
            Description = serviceMetadata.Description,
            Type = "service",
            ResourceType = "Service",
            ExternalIds = ImmutableArray.Create(serviceMetadata.Name),
            LayerIds = layerIds
        };

        return new CatalogRecord(
            new OgcRecordFeature
            {
                Id = $"service:{serviceMetadata.Name}",
                Geometry = null,
                Bbox = bbox,
                Properties = properties,
                Links = links.ToImmutable()
            },
            bbox,
            Modified: null,
            ExternalIds: [serviceMetadata.Name],
            SearchText: $"{serviceMetadata.Name} {serviceMetadata.Description}");
    }

    private static CatalogRecord CreateResourceRecord(
        MetadataV2Resource resource,
        MetadataV2Publication? publication,
        MetadataV2Service? service,
        MetadataV2GraphSnapshot snapshot,
        string baseUrl)
    {
        var bbox = ToBbox(resource);
        // Use the storage layer id as the externally-facing v1-compatible identifier
        // when available; otherwise fall back to the resource id.
        var storageLayerId = publication is not null
            ? snapshot.ResolveStorageLayerId(publication)
            : snapshot.ResolveStorageLayerId(resource);
        var layerIdString = storageLayerId.HasValue
            ? storageLayerId.Value.ToString(CultureInfo.InvariantCulture)
            : resource.Metadata.Id;

        var links = ImmutableArray.CreateBuilder<Link>();
        links.Add(Link.Create($"{baseUrl}/ogc/records/collections/{CatalogCollectionId}/items/{Uri.EscapeDataString($"layer:{layerIdString}")}", RelationTypes.Self, MediaTypes.GeoJson, resource.Metadata.Name));

        if (IsProtocolEnabled(service, OgcFeaturesProtocolName))
        {
            links.Add(Link.Create($"{baseUrl}/ogc/features/collections/{layerIdString}", RelationTypes.Alternate, MediaTypes.Json, "OGC API Features collection"));
            links.Add(Link.Create($"{baseUrl}/ogc/features/collections/{layerIdString}/items", RelationTypes.Data, MediaTypes.GeoJson, "OGC API Features items"));
        }

        if (IsProtocolEnabled(service, StacProtocolName))
        {
            links.Add(Link.Create($"{baseUrl}/stac/collections/{layerIdString}", RelationTypes.Alternate, MediaTypes.Json, "STAC collection"));
        }

        if (service is { Metadata: { } featureServiceMetadata } featureServerService &&
            IsProtocolEnabled(featureServerService, FeatureServerProtocolName))
        {
            links.Add(Link.Create($"{baseUrl}/rest/services/{Uri.EscapeDataString(featureServiceMetadata.Name)}/FeatureServer/{layerIdString}", RelationTypes.Alternate, MediaTypes.Json, "FeatureServer layer"));
        }

        var properties = new OgcRecordProperties
        {
            Title = resource.Metadata.Title ?? resource.Metadata.Name,
            Description = resource.Metadata.Description,
            Type = "dataset",
            ResourceType = "Layer",
            GeometryType = resource.ReadGeometryType().ToString(),
            ExternalIds = ImmutableArray.Create(layerIdString, resource.Metadata.Name)
        };

        return new CatalogRecord(
            new OgcRecordFeature
            {
                Id = $"layer:{layerIdString}",
                Geometry = null,
                Bbox = bbox,
                Properties = properties,
                Links = links.ToImmutable()
            },
            bbox,
            Modified: null,
            ExternalIds: [layerIdString, resource.Metadata.Name],
            SearchText: $"{layerIdString} {resource.Metadata.Name} {resource.Metadata.Description}");
    }

    private static IEnumerable<CatalogRecord> ApplyFilters(
        IEnumerable<CatalogRecord> records,
        BboxFilter? bbox,
        DateTimeFilter? datetime,
        string? q,
        string? ids,
        string? type,
        string? externalIds)
    {
        var idSet = SplitCsv(ids);
        var typeSet = SplitCsv(type);
        var externalIdSet = SplitCsv(externalIds);
        var terms = string.IsNullOrWhiteSpace(q)
            ? Array.Empty<string>()
            : q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var record in records)
        {
            if (idSet.Count > 0 && !idSet.Contains(record.Feature.Id))
            {
                continue;
            }

            if (typeSet.Count > 0 && !typeSet.Contains(record.Feature.Properties.Type))
            {
                continue;
            }

            if (externalIdSet.Count > 0 && !record.ExternalIds.Any(externalIdSet.Contains))
            {
                continue;
            }

            if (terms.Length > 0 &&
                !terms.All(term => record.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (bbox is not null && (record.Bbox is null || !Intersects(record.Bbox.Value, bbox)))
            {
                continue;
            }

            if (datetime is not null && (record.Modified is null || !datetime.Contains(record.Modified.Value)))
            {
                continue;
            }

            yield return record;
        }
    }

    private static ImmutableArray<Link> BuildItemsLinks(
        HttpContext context,
        string baseUrl,
        string collectionId,
        int numberMatched,
        int limit,
        int offset)
    {
        var links = ImmutableArray.CreateBuilder<Link>();
        var currentPath = $"{baseUrl}/ogc/records/collections/{Uri.EscapeDataString(collectionId)}/items";
        links.Add(Link.Create($"{currentPath}{context.Request.QueryString}", RelationTypes.Self, MediaTypes.GeoJson, "Records"));
        links.Add(Link.Create($"{baseUrl}/ogc/records/collections/{Uri.EscapeDataString(collectionId)}", "collection", MediaTypes.Json, "Record collection"));

        var nextOffset = offset + limit;
        if (nextOffset < numberMatched)
        {
            links.Add(Link.Create(BuildPagedHref(context, currentPath, limit, nextOffset), "next", MediaTypes.GeoJson, "Next page"));
        }

        if (offset > 0)
        {
            var previousOffset = Math.Max(0, offset - limit);
            links.Add(Link.Create(BuildPagedHref(context, currentPath, limit, previousOffset), "prev", MediaTypes.GeoJson, "Previous page"));
        }

        return links.ToImmutable();
    }

    private static string BuildPagedHref(HttpContext context, string currentPath, int limit, int offset)
    {
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(context.Request.QueryString.Value);
        query["limit"] = limit.ToString(CultureInfo.InvariantCulture);
        query["offset"] = offset.ToString(CultureInfo.InvariantCulture);
        return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            currentPath,
            query.SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value))));
    }

    private static bool TryPrepareMetadataResponse(HttpContext context, string? f, out string outputFormat, out IResult? errorResult)
        => OgcCoreMetadataUtilities.TryPrepareMetadataResponse(
            context,
            f,
            MetadataQueryParameters,
            out outputFormat,
            out errorResult);

    private static bool TryGetGeoJsonOutputFormat(HttpContext context, string? f, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(f) || string.Equals(f, "json", StringComparison.OrdinalIgnoreCase) || string.Equals(f, "geojson", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        error = $"Unsupported format '{f}'. Supported formats: json, geojson.";
        return false;
    }

    private static bool TryParseLimitOffset(int? limit, int? offset, out int pageLimit, out int pageOffset, out string? error)
    {
        pageLimit = limit ?? DefaultLimit;
        pageOffset = offset ?? 0;
        error = null;

        if (pageLimit < 1 || pageLimit > MaxLimit)
        {
            error = $"limit must be between 1 and {MaxLimit}.";
            return false;
        }

        if (pageOffset < 0)
        {
            error = "offset must be greater than or equal to 0.";
            return false;
        }

        return true;
    }

    private static bool TryParseBbox(string? value, out BboxFilter? bbox, out string? error)
    {
        bbox = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minX) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minY) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxX) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maxY))
        {
            error = "bbox must contain four comma-separated numbers: minx,miny,maxx,maxy.";
            return false;
        }

        if (!double.IsFinite(minX) || !double.IsFinite(minY) || !double.IsFinite(maxX) || !double.IsFinite(maxY) || minX > maxX || minY > maxY)
        {
            error = "bbox values must be finite and ordered as minx,miny,maxx,maxy.";
            return false;
        }

        bbox = new BboxFilter(minX, minY, maxX, maxY);
        return true;
    }

    private static bool TryParseDatetime(string? value, out DateTimeFilter? filter, out string? error)
    {
        filter = null;
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var parts = value.Split('/');
        if (parts.Length > 2)
        {
            error = "datetime must be an RFC 3339 instant or interval.";
            return false;
        }

        if (!TryParseDatePart(parts[0], out var start))
        {
            error = "datetime start is not a valid RFC 3339 value.";
            return false;
        }

        DateTimeOffset? end = start;
        if (parts.Length == 2)
        {
            if (!TryParseDatePart(parts[1], out end))
            {
                error = "datetime end is not a valid RFC 3339 value.";
                return false;
            }
        }

        filter = new DateTimeFilter(start, end);
        if (filter.Start is not null && filter.End is not null && filter.Start.Value > filter.End.Value)
        {
            error = "datetime start must be less than or equal to datetime end.";
            filter = null;
            return false;
        }

        return true;
    }

    private static bool TryParseDatePart(string value, out DateTimeOffset? result)
    {
        result = null;
        if (string.Equals(value, "..", StringComparison.Ordinal))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            result = parsed;
            return true;
        }

        return false;
    }

    private static ImmutableArray<double>? ToBbox(MetadataV2Resource resource)
    {
        var bbox = resource.ReadBbox();
        return bbox is null
            ? null
            : ImmutableArray.Create(bbox.West, bbox.South, bbox.East, bbox.North);
    }

    private static ImmutableArray<double>? CombineResourceBboxes(IEnumerable<MetadataV2Resource> resources)
    {
        var bboxes = resources
            .Select(r => r.ReadBbox())
            .Where(b => b is not null)
            .Select(b => b!)
            .ToArray();
        if (bboxes.Length == 0)
        {
            return null;
        }

        return ImmutableArray.Create(
            bboxes.Min(b => b.West),
            bboxes.Min(b => b.South),
            bboxes.Max(b => b.East),
            bboxes.Max(b => b.North));
    }

    private static bool Intersects(ImmutableArray<double> recordBbox, BboxFilter filter)
        => recordBbox.Length == 4 &&
           recordBbox[0] <= filter.MaxX &&
           recordBbox[2] >= filter.MinX &&
           recordBbox[1] <= filter.MaxY &&
           recordBbox[3] >= filter.MinY;

    private static HashSet<string> SplitCsv(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsCatalogCollection(string collectionId)
        => string.Equals(collectionId, CatalogCollectionId, StringComparison.OrdinalIgnoreCase);

    private static bool IsProtocolEnabled(MetadataV2Service? service, string protocol)
        => service?.Protocols.Any(enabled => string.Equals(enabled, protocol, StringComparison.OrdinalIgnoreCase)) == true;

    private sealed record CatalogRecord(
        OgcRecordFeature Feature,
        ImmutableArray<double>? Bbox,
        DateTimeOffset? Modified,
        string[] ExternalIds,
        string SearchText);

    private sealed record BboxFilter(double MinX, double MinY, double MaxX, double MaxY);

    private sealed record DateTimeFilter(DateTimeOffset? Start, DateTimeOffset? End)
    {
        public bool Contains(DateTimeOffset value)
            => (Start is null || value >= Start.Value) && (End is null || value <= End.Value);
    }
}
