// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Import;

namespace Honua.Server.Features.Admin.Services;

internal sealed partial class ExternalServiceDiscoveryService(
    IHttpClientFactory httpClientFactory,
    IExternalServiceDiscoveryNetworkGuard networkGuard,
    ILogger<ExternalServiceDiscoveryService> logger) : IExternalServiceDiscoveryService
{
    internal const string HttpClientName = "external-service-discovery";
    private const int DefaultTimeoutSeconds = 30;
    private const int MaximumTimeoutSeconds = 120;

    public async Task<ExternalServiceDiscoveryResponse> DiscoverAsync(
        ExternalServiceDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceUrl = request.Url ?? request.ServiceUrl;
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            throw new ExternalServiceDiscoveryRequestException("Url is required.");
        }

        var normalizedUri = await NormalizeAndValidateAsync(sourceUrl, cancellationToken).ConfigureAwait(false);
        var serviceType = GetServiceType(normalizedUri);

        if (serviceType is null)
        {
            return await DiscoverOgcApiFeaturesAsync(
                    sourceUrl,
                    normalizedUri,
                    ClampTimeout(request.TimeoutSeconds),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await DiscoverArcGisAsync(
                sourceUrl,
                normalizedUri,
                serviceType,
                ClampTimeout(request.TimeoutSeconds),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ExternalServiceDiscoveryResponse> DiscoverArcGisAsync(
        string sourceUrl,
        Uri serviceUri,
        string serviceType,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var sourceKind = serviceType.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase)
            ? "arcgis-feature-server"
            : "arcgis-map-server";

        var serviceDocument = await GetJsonAsync(
                BuildServiceInfoUri(serviceUri),
                ExternalServiceDiscoveryJsonContext.Default.ArcGisServiceDocument,
                timeoutSeconds,
                cancellationToken)
            .ConfigureAwait(false);

        if (serviceDocument.Error is not null)
        {
            throw new ExternalServiceDiscoveryRemoteException("ArcGIS service returned an error during discovery.");
        }

        var serviceName = FirstNonWhiteSpace(
            serviceDocument.ServiceDescription,
            serviceDocument.MapName,
            serviceDocument.Name,
            ExtractServiceName(serviceUri));

        var warnings = new List<string>();
        var candidates = new List<ExternalServiceLayerCandidate>();
        var references = EnumerateLayerReferences(serviceDocument);

        foreach (var reference in references)
        {
            try
            {
                var layer = await GetJsonAsync(
                        BuildLayerInfoUri(serviceUri, reference.Id),
                        ExternalServiceDiscoveryJsonContext.Default.ArcGisLayerDocument,
                        timeoutSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (layer.Error is not null)
                {
                    warnings.Add($"Layer {reference.Id} metadata returned an ArcGIS error and was skipped.");
                    continue;
                }

                var featureCount = layer.FeatureCount ?? layer.Count;
                if (featureCount is null)
                {
                    featureCount = await TryGetFeatureCountAsync(serviceUri, reference.Id, timeoutSeconds, cancellationToken)
                        .ConfigureAwait(false);
                }

                candidates.Add(MapCandidate(
                    serviceUri,
                    sourceKind,
                    serviceName,
                    serviceDocument.SpatialReference,
                    reference,
                    layer,
                    featureCount));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                Log.LayerMetadataFailed(logger, reference.Id, ex);
                warnings.Add($"Layer {reference.Id} metadata could not be read.");
                candidates.Add(CreateReferenceOnlyCandidate(
                    serviceUri,
                    sourceKind,
                    serviceName,
                    serviceDocument.SpatialReference,
                    reference));
            }
        }

        var normalizedUrl = serviceUri.ToString();
        Log.ServiceDiscovered(logger, normalizedUrl, candidates.Count);

        return new ExternalServiceDiscoveryResponse
        {
            SourceUrl = sourceUrl,
            NormalizedUrl = normalizedUrl,
            SourceKind = sourceKind,
            ServiceType = serviceType,
            ServiceName = serviceName,
            Description = serviceDocument.Description,
            Srid = GetSrid(serviceDocument.SpatialReference),
            Candidates = candidates.ToArray(),
            Warnings = warnings.ToArray()
        };
    }

    private async Task<Uri> NormalizeAndValidateAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ExternalServiceDiscoveryRequestException("Url must be a valid HTTPS URL.");
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new ExternalServiceDiscoveryRequestException("Url must not include embedded credentials.");
        }

        if (await networkGuard.IsDisallowedAsync(uri, cancellationToken).ConfigureAwait(false))
        {
            throw new ExternalServiceDiscoveryRequestException(
                "Url resolves to a private, loopback, or unresolvable network address, which is not allowed.");
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        var normalized = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri(normalized, UriKind.Absolute);
    }

    private async Task<T> GetJsonAsync<T>(
        Uri uri,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var result = await client.GetFromJsonAsync(uri, jsonTypeInfo, timeoutCts.Token).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("External service returned an empty JSON response.");
    }

    private async Task<int?> TryGetFeatureCountAsync(
        Uri serviceUri,
        int layerId,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var count = await GetJsonAsync(
                    BuildLayerCountUri(serviceUri, layerId),
                    ExternalServiceDiscoveryJsonContext.Default.ArcGisCountDocument,
                    timeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);

            return count.Error is null ? count.Count : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            Log.FeatureCountFailed(logger, layerId, ex);
            return null;
        }
    }

    private static IEnumerable<ArcGisLayerReferenceDocument> EnumerateLayerReferences(ArcGisServiceDocument serviceDocument)
    {
        foreach (var layer in serviceDocument.Layers ?? [])
        {
            yield return layer;
        }

        foreach (var table in serviceDocument.Tables ?? [])
        {
            yield return table;
        }
    }

    private static ExternalServiceLayerCandidate MapCandidate(
        Uri serviceUri,
        string sourceKind,
        string serviceName,
        ArcGisSpatialReferenceDocument? serviceSpatialReference,
        ArcGisLayerReferenceDocument reference,
        ArcGisLayerDocument layer,
        int? featureCount)
    {
        var extent = MapExtent(layer.Extent);
        return new ExternalServiceLayerCandidate
        {
            SourceKind = sourceKind,
            ServiceName = serviceName,
            ServiceUrl = serviceUri.ToString(),
            ExternalId = layer.Id.ToString(CultureInfo.InvariantCulture),
            LayerId = layer.Id,
            Name = FirstNonWhiteSpace(layer.Name, reference.Name, $"Layer {reference.Id}"),
            Description = layer.Description,
            LayerType = layer.Type,
            GeometryType = layer.GeometryType,
            Srid = extent?.Srid ?? GetSrid(serviceSpatialReference),
            Extent = extent,
            Fields = MapFields(layer.Fields),
            FeatureCount = featureCount
        };
    }

    private static ExternalServiceLayerCandidate CreateReferenceOnlyCandidate(
        Uri serviceUri,
        string sourceKind,
        string serviceName,
        ArcGisSpatialReferenceDocument? serviceSpatialReference,
        ArcGisLayerReferenceDocument reference)
        => new()
        {
            SourceKind = sourceKind,
            ServiceName = serviceName,
            ServiceUrl = serviceUri.ToString(),
            ExternalId = reference.Id.ToString(CultureInfo.InvariantCulture),
            LayerId = reference.Id,
            Name = FirstNonWhiteSpace(reference.Name, $"Layer {reference.Id}"),
            Srid = GetSrid(serviceSpatialReference)
        };

    private async Task<ExternalServiceDiscoveryResponse> DiscoverOgcApiFeaturesAsync(
        string sourceUrl,
        Uri serviceUri,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var landingDocument = await TryGetOgcLandingAsync(serviceUri, timeoutSeconds, cancellationToken)
            .ConfigureAwait(false);
        var collectionsUri = ResolveOgcCollectionsUri(serviceUri, landingDocument);
        var collectionsDocument = await GetJsonAsync(
                collectionsUri,
                ExternalServiceDiscoveryJsonContext.Default.OgcCollectionsDocument,
                timeoutSeconds,
                cancellationToken)
            .ConfigureAwait(false);

        if (collectionsDocument.Collections is null)
        {
            throw new ExternalServiceDiscoveryRemoteException(
                "OGC API Features collections response did not include a collections array.");
        }

        var serviceName = FirstNonWhiteSpace(
            landingDocument?.Title,
            collectionsDocument.Title,
            ExtractOgcServiceName(serviceUri));

        var candidates = collectionsDocument.Collections
            .Where(collection => !string.IsNullOrWhiteSpace(collection.Id))
            .Select(collection => MapOgcCandidate(collection, serviceName, serviceUri, collectionsUri))
            .ToArray();

        string[] warnings = collectionsDocument.Collections.Length == candidates.Length
            ? []
            : new[] { "One or more OGC API Features collections without an id were skipped." };

        var normalizedUrl = collectionsUri.ToString();
        Log.ServiceDiscovered(logger, normalizedUrl, candidates.Length);

        return new ExternalServiceDiscoveryResponse
        {
            SourceUrl = sourceUrl,
            NormalizedUrl = normalizedUrl,
            SourceKind = "ogc-api-features",
            ServiceType = "OGC API Features",
            ServiceName = serviceName,
            Description = FirstNonWhiteSpaceOrNull(landingDocument?.Description, collectionsDocument.Description),
            Srid = 4326,
            Candidates = candidates,
            Warnings = warnings
        };
    }

    private async Task<OgcLandingDocument?> TryGetOgcLandingAsync(
        Uri serviceUri,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (IsOgcCollectionsUri(serviceUri))
        {
            return null;
        }

        try
        {
            return await GetJsonAsync(
                    serviceUri,
                    ExternalServiceDiscoveryJsonContext.Default.OgcLandingDocument,
                    timeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            Log.OgcLandingPageFailed(logger, serviceUri, ex);
            return null;
        }
    }

    private static Uri ResolveOgcCollectionsUri(Uri serviceUri, OgcLandingDocument? landingDocument)
    {
        if (IsOgcCollectionsUri(serviceUri))
        {
            return serviceUri;
        }

        var linkedCollections = landingDocument?.Links?
            .Where(static link =>
                !string.IsNullOrWhiteSpace(link.Href) &&
                (string.Equals(link.Rel, "data", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(link.Rel, "collections", StringComparison.OrdinalIgnoreCase)))
            .Select(link => TryResolveSameOriginUri(serviceUri, link.Href!))
            .FirstOrDefault(uri => uri is not null);

        return linkedCollections ?? AppendPathSegment(serviceUri, "collections");
    }

    private static ExternalServiceLayerCandidate MapOgcCandidate(
        OgcCollectionDocument collection,
        string serviceName,
        Uri serviceUri,
        Uri collectionsUri)
    {
        var sourceUrl = ResolveOgcCollectionUri(collection, serviceUri, collectionsUri).ToString();
        return new ExternalServiceLayerCandidate
        {
            SourceKind = "ogc-api-features",
            ServiceName = serviceName,
            ServiceUrl = sourceUrl,
            ExternalId = collection.Id,
            Name = FirstNonWhiteSpace(collection.Title, collection.Id, "Collection"),
            Description = collection.Description,
            LayerType = "collection",
            GeometryType = collection.ItemType,
            Srid = GetOgcSrid(collection),
            Extent = MapOgcExtent(collection.Extent),
            FeatureCount = collection.ItemCount
        };
    }

    private static Uri ResolveOgcCollectionUri(
        OgcCollectionDocument collection,
        Uri serviceUri,
        Uri collectionsUri)
    {
        var selfLink = collection.Links?
            .Where(static link =>
                !string.IsNullOrWhiteSpace(link.Href) &&
                string.Equals(link.Rel, "self", StringComparison.OrdinalIgnoreCase))
            .Select(link => TryResolveSameOriginUri(serviceUri, link.Href!))
            .FirstOrDefault(uri => uri is not null);

        if (selfLink is not null)
        {
            return selfLink;
        }

        return AppendPathSegment(collectionsUri, Uri.EscapeDataString(collection.Id ?? string.Empty));
    }

    private static ExternalServiceExtent? MapOgcExtent(OgcExtentDocument? extent)
    {
        var bbox = extent?.Spatial?.Bbox?.FirstOrDefault(static values => values.Length >= 4);
        if (bbox is null)
        {
            return null;
        }

        return new ExternalServiceExtent
        {
            XMin = bbox[0],
            YMin = bbox[1],
            XMax = bbox[2],
            YMax = bbox[3],
            Srid = ParseOgcCrsSrid(extent?.Spatial?.Crs) ?? 4326
        };
    }

    private static int? GetOgcSrid(OgcCollectionDocument collection)
    {
        if (ParseOgcCrsSrid(collection.StorageCrs) is { } storageSrid)
        {
            return storageSrid;
        }

        if (collection.Crs is not null)
        {
            foreach (var crs in collection.Crs)
            {
                if (ParseOgcCrsSrid(crs) is { } srid)
                {
                    return srid;
                }
            }
        }

        return ParseOgcCrsSrid(collection.Extent?.Spatial?.Crs) ?? 4326;
    }

    private static int? ParseOgcCrsSrid(string? crs)
    {
        if (string.IsNullOrWhiteSpace(crs))
        {
            return null;
        }

        if (crs.Contains("CRS84", StringComparison.OrdinalIgnoreCase))
        {
            return 4326;
        }

        var lastSegment = crs.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return int.TryParse(lastSegment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var srid) && srid > 0
            ? srid
            : null;
    }

    private static ExternalServiceExtent? MapExtent(ArcGisExtentDocument? extent)
        => extent is null
            ? null
            : new ExternalServiceExtent
            {
                XMin = extent.XMin,
                YMin = extent.YMin,
                XMax = extent.XMax,
                YMax = extent.YMax,
                Srid = GetSrid(extent.SpatialReference)
            };

    private static ExternalServiceField[] MapFields(ArcGisFieldDocument[]? fields)
        => fields is null
            ? []
            : fields
                .Where(field => !string.IsNullOrWhiteSpace(field.Name))
                .Select(field => new ExternalServiceField
                {
                    Name = field.Name!,
                    Type = field.Type,
                    Alias = field.Alias,
                    Length = field.Length,
                    Nullable = field.Nullable
                })
                .ToArray();

    private static Uri BuildServiceInfoUri(Uri serviceUri)
        => new($"{serviceUri}?f=json", UriKind.Absolute);

    private static Uri BuildLayerInfoUri(Uri serviceUri, int layerId)
        => new($"{serviceUri}/{layerId}?f=json", UriKind.Absolute);

    private static Uri BuildLayerCountUri(Uri serviceUri, int layerId)
        => new($"{serviceUri}/{layerId}/query?where=1%3D1&returnCountOnly=true&f=json", UriKind.Absolute);

    private static Uri AppendPathSegment(Uri baseUri, string segment)
    {
        var builder = new UriBuilder(baseUri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            Path = $"{baseUri.AbsolutePath.TrimEnd('/')}/{segment.TrimStart('/')}"
        };

        return builder.Uri;
    }

    private static bool IsOgcCollectionsUri(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[^1].Equals("collections", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri? TryResolveSameOriginUri(Uri baseUri, string href)
    {
        if (!Uri.TryCreate(baseUri, href, out var resolved))
        {
            return null;
        }

        if (!string.Equals(baseUri.Scheme, resolved.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(baseUri.Host, resolved.Host, StringComparison.OrdinalIgnoreCase) ||
            baseUri.Port != resolved.Port)
        {
            return null;
        }

        var builder = new UriBuilder(resolved)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }

    private static string? GetServiceType(Uri serviceUri)
    {
        var segments = serviceUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var lastSegment = segments[^1];
        if (lastSegment.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase))
        {
            return "FeatureServer";
        }

        return lastSegment.Equals("MapServer", StringComparison.OrdinalIgnoreCase)
            ? "MapServer"
            : null;
    }

    private static string ExtractServiceName(Uri serviceUri)
    {
        var segments = serviceUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            if (segments[i].Equals("FeatureServer", StringComparison.OrdinalIgnoreCase) ||
                segments[i].Equals("MapServer", StringComparison.OrdinalIgnoreCase))
            {
                return i > 0 ? Uri.UnescapeDataString(segments[i - 1]) : "External Service";
            }
        }

        return segments.Length > 0 ? Uri.UnescapeDataString(segments[^1]) : "External Service";
    }

    private static string ExtractOgcServiceName(Uri serviceUri)
    {
        var segments = serviceUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return serviceUri.Host;
        }

        if (segments[^1].Equals("collections", StringComparison.OrdinalIgnoreCase) && segments.Length > 1)
        {
            return Uri.UnescapeDataString(segments[^2]);
        }

        return Uri.UnescapeDataString(segments[^1]);
    }

    private static int? GetSrid(ArcGisSpatialReferenceDocument? spatialReference)
        => spatialReference?.LatestWkid ?? spatialReference?.Wkid;

    private static int ClampTimeout(int? timeoutSeconds)
        => Math.Clamp(timeoutSeconds ?? DefaultTimeoutSeconds, 1, MaximumTimeoutSeconds);

    private static string FirstNonWhiteSpace(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? "External Service";

    private static string? FirstNonWhiteSpaceOrNull(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 4180,
            Level = LogLevel.Information,
            Message = "Discovered external service {ServiceUrl} with {CandidateCount} candidates")]
        public static partial void ServiceDiscovered(ILogger logger, string serviceUrl, int candidateCount);

        [LoggerMessage(
            EventId = 4181,
            Level = LogLevel.Debug,
            Message = "Failed to read external service layer {LayerId} metadata")]
        public static partial void LayerMetadataFailed(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(
            EventId = 4182,
            Level = LogLevel.Debug,
            Message = "Failed to read external service layer {LayerId} feature count")]
        public static partial void FeatureCountFailed(ILogger logger, int layerId, Exception exception);

        [LoggerMessage(
            EventId = 4183,
            Level = LogLevel.Debug,
            Message = "Failed to read OGC API Features landing page {ServiceUrl}")]
        public static partial void OgcLandingPageFailed(ILogger logger, Uri serviceUrl, Exception exception);
    }
}

internal sealed class ExternalServiceDiscoveryRequestException(string message) : Exception(message)
{
}

internal sealed class ExternalServiceDiscoveryRemoteException(string message) : Exception(message)
{
}

internal sealed class ExternalServiceDiscoveryNetworkGuard : IExternalServiceDiscoveryNetworkGuard
{
    public Task<bool> IsDisallowedAsync(Uri uri, CancellationToken cancellationToken = default)
        => NetworkAddressValidator.IsDisallowedAddressAsync(uri, ResolveHostAddressesAsync, cancellationToken);

    private static Task<IPAddress[]> ResolveHostAddressesAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}
