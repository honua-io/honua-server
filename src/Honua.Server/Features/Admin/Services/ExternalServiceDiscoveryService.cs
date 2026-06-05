// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Honua.Server.Features.Admin.Models;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;

namespace Honua.Server.Features.Admin.Services;

internal sealed partial class ExternalServiceDiscoveryService(
    IHttpClientFactory httpClientFactory,
    IExternalServiceDiscoveryNetworkGuard networkGuard,
    ILogger<ExternalServiceDiscoveryService> logger) : IExternalServiceDiscoveryService
{
    internal const string HttpClientName = "external-service-discovery";
    private const int DefaultTimeoutSeconds = 30;
    private const int MaximumTimeoutSeconds = 120;
    private const int MaxCatalogServices = 250;
    private const int CatalogConcurrency = 6;

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

        var timeoutSeconds = ClampTimeout(request.TimeoutSeconds);
        var normalizedUri = await NormalizeAndValidateAsync(sourceUrl, cancellationToken).ConfigureAwait(false);
        var auth = await ResolveAuthAsync(normalizedUri, request.Credentials, timeoutSeconds, cancellationToken)
            .ConfigureAwait(false);
        var serviceType = GetServiceType(normalizedUri);

        if (serviceType is not null)
        {
            return await DiscoverArcGisAsync(
                    sourceUrl,
                    normalizedUri,
                    serviceType,
                    auth,
                    timeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (TryGetArcGisCatalogRemainder(normalizedUri, out var catalogFolder))
        {
            return await DiscoverArcGisCatalogAsync(
                    sourceUrl,
                    normalizedUri,
                    catalogFolder,
                    auth,
                    timeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (IsWfsDiscoveryRequest(sourceUrl, normalizedUri))
        {
            return await DiscoverWfsAsync(
                    sourceUrl,
                    normalizedUri,
                    auth,
                    timeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await DiscoverOgcApiFeaturesAsync(
                sourceUrl,
                normalizedUri,
                auth,
                timeoutSeconds,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ExternalServiceDiscoveryResponse> DiscoverArcGisAsync(
        string sourceUrl,
        Uri serviceUri,
        string serviceType,
        ResolvedAuth auth,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var summary = await DiscoverArcGisServiceCoreAsync(
                serviceUri,
                serviceType,
                folderPath: null,
                auth,
                includeFeatureCount: true,
                timeoutSeconds,
                warnings,
                cancellationToken)
            .ConfigureAwait(false);

        var normalizedUrl = serviceUri.ToString();
        Log.ServiceDiscovered(logger, normalizedUrl, summary.Candidates.Length);

        return new ExternalServiceDiscoveryResponse
        {
            SourceUrl = sourceUrl,
            NormalizedUrl = normalizedUrl,
            SourceKind = summary.SourceKind,
            ServiceType = serviceType,
            ServiceName = summary.ServiceName,
            Srid = summary.Srid,
            Candidates = summary.Candidates,
            Services = [summary],
            IsCatalog = false,
            Warnings = warnings.ToArray()
        };
    }

    /// <summary>
    /// Reads a single ArcGIS service document and its layers/tables into a service summary. Shared by
    /// single-service discovery and catalog enumeration. In catalog mode feature counts are skipped to keep
    /// the per-service cost low; the cheap counts the layer document already exposes are still surfaced.
    /// </summary>
    private async Task<ExternalServiceSummary> DiscoverArcGisServiceCoreAsync(
        Uri serviceUri,
        string serviceType,
        string? folderPath,
        ResolvedAuth auth,
        bool includeFeatureCount,
        int timeoutSeconds,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var sourceKind = serviceType.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase)
            ? "arcgis-feature-server"
            : "arcgis-map-server";

        var serviceDocument = await GetJsonAsync(
                BuildServiceInfoUri(serviceUri),
                ExternalServiceDiscoveryJsonContext.Default.ArcGisServiceDocument,
                auth,
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

        var candidates = new List<ExternalServiceLayerCandidate>();
        foreach (var reference in EnumerateLayerReferences(serviceDocument))
        {
            try
            {
                var layer = await GetJsonAsync(
                        BuildLayerInfoUri(serviceUri, reference.Id),
                        ExternalServiceDiscoveryJsonContext.Default.ArcGisLayerDocument,
                        auth,
                        timeoutSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (layer.Error is not null)
                {
                    warnings.Add($"Layer {reference.Id} metadata returned an ArcGIS error and was skipped.");
                    continue;
                }

                var featureCount = layer.FeatureCount ?? layer.Count;
                if (featureCount is null && includeFeatureCount)
                {
                    featureCount = await TryGetFeatureCountAsync(serviceUri, reference.Id, auth, timeoutSeconds, cancellationToken)
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
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
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

        return new ExternalServiceSummary
        {
            SourceKind = sourceKind,
            ServiceName = serviceName,
            ServiceType = serviceType,
            ServiceUrl = serviceUri.ToString(),
            FolderPath = folderPath,
            Srid = GetSrid(serviceDocument.SpatialReference),
            Candidates = candidates.ToArray()
        };
    }

    private async Task<ExternalServiceDiscoveryResponse> DiscoverArcGisCatalogAsync(
        string sourceUrl,
        Uri catalogUri,
        string? requestedFolder,
        ResolvedAuth auth,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        // The catalog listing lives at the supplied URL; the services base is the URL with any folder removed,
        // because ArcGIS service names in a folder listing are folder-qualified (e.g. "Census/Population").
        var servicesBaseUri = requestedFolder is null ? catalogUri : RemoveLastSegment(catalogUri);
        var warnings = new List<string>();

        var rootCatalog = await GetJsonAsync(
                BuildServiceInfoUri(catalogUri),
                ExternalServiceDiscoveryJsonContext.Default.ArcGisCatalogDocument,
                auth,
                timeoutSeconds,
                cancellationToken)
            .ConfigureAwait(false);

        if (rootCatalog.Error is not null)
        {
            throw new ExternalServiceDiscoveryRemoteException("ArcGIS catalog returned an error during discovery.");
        }

        // (folder, service) pairs to enumerate. The requested folder's services come from the root listing;
        // when at the catalog root we also walk each child folder.
        var serviceTargets = new List<(string? Folder, ArcGisCatalogServiceDocument Service)>();
        foreach (var service in rootCatalog.Services ?? [])
        {
            serviceTargets.Add((requestedFolder, service));
        }

        if (requestedFolder is null)
        {
            foreach (var folder in rootCatalog.Folders ?? [])
            {
                if (string.IsNullOrWhiteSpace(folder))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var folderCatalog = await GetJsonAsync(
                            BuildServiceInfoUri(AppendPathSegment(servicesBaseUri, folder)),
                            ExternalServiceDiscoveryJsonContext.Default.ArcGisCatalogDocument,
                            auth,
                            timeoutSeconds,
                            cancellationToken)
                        .ConfigureAwait(false);

                    foreach (var service in folderCatalog.Services ?? [])
                    {
                        serviceTargets.Add((folder, service));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
                {
                    Log.CatalogFolderFailed(logger, folder, ex);
                    warnings.Add($"Folder '{folder}' could not be enumerated and was skipped.");
                }
            }
        }

        // Only feature/map services are importable; filter and de-duplicate before enumerating.
        var importable = serviceTargets
            .Where(target => GetCatalogServiceType(target.Service.Type) is not null &&
                             !string.IsNullOrWhiteSpace(target.Service.Name))
            .ToList();

        if (importable.Count > MaxCatalogServices)
        {
            warnings.Add(
                $"Catalog exposes {importable.Count} services; only the first {MaxCatalogServices} were enumerated.");
            importable = importable.Take(MaxCatalogServices).ToList();
        }

        var summaries = await EnumerateCatalogServicesAsync(
                servicesBaseUri,
                importable,
                auth,
                timeoutSeconds,
                warnings,
                cancellationToken)
            .ConfigureAwait(false);

        var candidates = summaries.SelectMany(summary => summary.Candidates).ToArray();
        var normalizedUrl = catalogUri.ToString();
        Log.CatalogDiscovered(logger, normalizedUrl, summaries.Length, candidates.Length);

        return new ExternalServiceDiscoveryResponse
        {
            SourceUrl = sourceUrl,
            NormalizedUrl = normalizedUrl,
            SourceKind = "arcgis-catalog",
            ServiceType = "Catalog",
            ServiceName = requestedFolder ?? ExtractCatalogName(catalogUri),
            Srid = null,
            Candidates = candidates,
            Services = summaries,
            IsCatalog = true,
            Warnings = warnings.ToArray()
        };
    }

    private async Task<ExternalServiceSummary[]> EnumerateCatalogServicesAsync(
        Uri servicesBaseUri,
        IReadOnlyList<(string? Folder, ArcGisCatalogServiceDocument Service)> targets,
        ResolvedAuth auth,
        int timeoutSeconds,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(CatalogConcurrency);
        var warningsLock = new object();

        var tasks = targets.Select(async target =>
        {
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var serviceType = GetCatalogServiceType(target.Service.Type)!;
                var serviceUri = BuildCatalogServiceUri(servicesBaseUri, target.Service.Name!, serviceType);
                var serviceWarnings = new List<string>();
                var summary = await DiscoverArcGisServiceCoreAsync(
                        serviceUri,
                        serviceType,
                        target.Folder,
                        auth,
                        includeFeatureCount: false,
                        timeoutSeconds,
                        serviceWarnings,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (serviceWarnings.Count > 0)
                {
                    lock (warningsLock)
                    {
                        warnings.AddRange(serviceWarnings);
                    }
                }

                return summary;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                           or JsonException or ExternalServiceDiscoveryRemoteException)
            {
                Log.CatalogServiceFailed(logger, target.Service.Name ?? "(unknown)", ex);
                lock (warningsLock)
                {
                    warnings.Add($"Service '{target.Service.Name}' could not be read and was skipped.");
                }

                return null;
            }
            finally
            {
                throttle.Release();
            }
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(static summary => summary is not null).Cast<ExternalServiceSummary>().ToArray();
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
        ResolvedAuth auth,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var requestMessage = BuildAuthenticatedRequest(HttpMethod.Get, uri, auth);
        using var response = await client
            .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"External service returned status {(int)response.StatusCode} during discovery.");
        }

        var result = await response.Content
            .ReadFromJsonAsync(jsonTypeInfo, timeoutCts.Token)
            .ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("External service returned an empty JSON response.");
    }

    private async Task<int?> TryGetFeatureCountAsync(
        Uri serviceUri,
        int layerId,
        ResolvedAuth auth,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var count = await GetJsonAsync(
                    BuildLayerCountUri(serviceUri, layerId),
                    ExternalServiceDiscoveryJsonContext.Default.ArcGisCountDocument,
                    auth,
                    timeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);

            return count.Error is null ? count.Count : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
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
        ResolvedAuth auth,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var landingDocument = await TryGetOgcLandingAsync(serviceUri, auth, timeoutSeconds, cancellationToken)
            .ConfigureAwait(false);
        var collectionsUri = ResolveOgcCollectionsUri(serviceUri, landingDocument);
        var collectionsDocument = await GetJsonAsync(
                collectionsUri,
                ExternalServiceDiscoveryJsonContext.Default.OgcCollectionsDocument,
                auth,
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
            Services =
            [
                new ExternalServiceSummary
                {
                    SourceKind = "ogc-api-features",
                    ServiceName = serviceName,
                    ServiceType = "OGC API Features",
                    ServiceUrl = normalizedUrl,
                    Srid = 4326,
                    Candidates = candidates
                }
            ],
            IsCatalog = false,
            Warnings = warnings
        };
    }

    private async Task<ExternalServiceDiscoveryResponse> DiscoverWfsAsync(
        string sourceUrl,
        Uri serviceUri,
        ResolvedAuth auth,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var capabilitiesUri = BuildWfsGetCapabilitiesUri(serviceUri);
        XDocument capabilitiesDocument;
        try
        {
            capabilitiesDocument = await GetXmlAsync(capabilitiesUri, auth, timeoutSeconds, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (XmlException)
        {
            throw new ExternalServiceDiscoveryRemoteException("WFS GetCapabilities response was not valid XML.");
        }

        var root = capabilitiesDocument.Root;
        if (root is null)
        {
            throw new ExternalServiceDiscoveryRemoteException("WFS GetCapabilities response was empty.");
        }

        if (root.Name.LocalName.Equals("ExceptionReport", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExternalServiceDiscoveryRemoteException("WFS service returned an error during discovery.");
        }

        if (!root.Name.LocalName.Equals("WFS_Capabilities", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExternalServiceDiscoveryRemoteException("WFS GetCapabilities response was not recognized.");
        }

        var featureTypeList = DescendantsByLocalName(root, "FeatureTypeList").FirstOrDefault();
        if (featureTypeList is null)
        {
            throw new ExternalServiceDiscoveryRemoteException(
                "WFS GetCapabilities response did not include feature type metadata.");
        }

        var warnings = new List<string>();
        var serviceName = FirstNonWhiteSpace(
            GetWfsServiceMetadataValue(root, "Title"),
            ExtractWfsServiceName(serviceUri));
        var candidates = featureTypeList.Elements()
            .Where(static element => element.Name.LocalName.Equals("FeatureType", StringComparison.OrdinalIgnoreCase))
            .Select(featureType => MapWfsCandidate(featureType, serviceName, serviceUri, warnings))
            .Where(static candidate => candidate is not null)
            .Cast<ExternalServiceLayerCandidate>()
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new ExternalServiceDiscoveryRemoteException(
                "WFS GetCapabilities response did not include feature type candidates.");
        }

        var responseSrid = GetSharedSrid(candidates);
        var normalizedUrl = capabilitiesUri.ToString();
        Log.ServiceDiscovered(logger, normalizedUrl, candidates.Length);

        var wfsServiceType = BuildWfsServiceType(root);
        return new ExternalServiceDiscoveryResponse
        {
            SourceUrl = sourceUrl,
            NormalizedUrl = normalizedUrl,
            SourceKind = "wfs",
            ServiceType = wfsServiceType,
            ServiceName = serviceName,
            Description = GetWfsServiceMetadataValue(root, "Abstract"),
            Srid = responseSrid,
            Candidates = candidates,
            Services =
            [
                new ExternalServiceSummary
                {
                    SourceKind = "wfs",
                    ServiceName = serviceName,
                    ServiceType = wfsServiceType,
                    ServiceUrl = normalizedUrl,
                    Srid = responseSrid,
                    Candidates = candidates
                }
            ],
            IsCatalog = false,
            Warnings = warnings.ToArray()
        };
    }

    private async Task<XDocument> GetXmlAsync(
        Uri uri,
        ResolvedAuth auth,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var requestMessage = BuildAuthenticatedRequest(HttpMethod.Get, uri, auth);
        using var response = await client
            .SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException("External service returned a non-success response during discovery.");
        }

        using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        using var reader = XmlReader.Create(stream, CreateSecureXmlReaderSettings());
        return await XDocument.LoadAsync(reader, LoadOptions.None, timeoutCts.Token).ConfigureAwait(false);
    }

    private static XmlReaderSettings CreateSecureXmlReaderSettings()
        => new()
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 2_000_000,
            MaxCharactersFromEntities = 0
        };

    private static ExternalServiceLayerCandidate? MapWfsCandidate(
        XElement featureType,
        string serviceName,
        Uri serviceUri,
        List<string> warnings)
    {
        var featureName = FirstChildValue(featureType, "Name");
        if (string.IsNullOrWhiteSpace(featureName))
        {
            warnings.Add("A WFS feature type without a name was skipped.");
            return null;
        }

        var extent = MapWfsExtent(featureType);
        return new ExternalServiceLayerCandidate
        {
            SourceKind = "wfs",
            ServiceName = serviceName,
            ServiceUrl = serviceUri.ToString(),
            ExternalId = featureName,
            Name = featureName,
            Title = FirstNonWhiteSpaceOrNull(FirstChildValue(featureType, "Title")),
            Description = FirstChildValue(featureType, "Abstract"),
            LayerType = "feature-type",
            Srid = GetWfsSrid(featureType) ?? extent?.Srid,
            Extent = extent,
            Fields = []
        };
    }

    private static ExternalServiceExtent? MapWfsExtent(XElement featureType)
    {
        var wgs84BoundingBox = DescendantsByLocalName(featureType, "WGS84BoundingBox").FirstOrDefault();
        if (wgs84BoundingBox is not null &&
            TryParseCorner(FirstChildValue(wgs84BoundingBox, "LowerCorner"), out var lower) &&
            TryParseCorner(FirstChildValue(wgs84BoundingBox, "UpperCorner"), out var upper))
        {
            return CreateExtent(lower, upper, 4326);
        }

        var latLongBoundingBox = DescendantsByLocalName(featureType, "LatLongBoundingBox").FirstOrDefault();
        if (latLongBoundingBox is not null &&
            TryGetDoubleAttribute(latLongBoundingBox, "minx", out var minX) &&
            TryGetDoubleAttribute(latLongBoundingBox, "miny", out var minY) &&
            TryGetDoubleAttribute(latLongBoundingBox, "maxx", out var maxX) &&
            TryGetDoubleAttribute(latLongBoundingBox, "maxy", out var maxY))
        {
            return new ExternalServiceExtent
            {
                XMin = minX,
                YMin = minY,
                XMax = maxX,
                YMax = maxY,
                Srid = 4326
            };
        }

        var boundingBox = DescendantsByLocalName(featureType, "BoundingBox").FirstOrDefault();
        var bboxSrid = ParseOgcCrsSrid(
            GetAttributeValue(boundingBox, "crs") ??
            GetAttributeValue(boundingBox, "srsName") ??
            GetAttributeValue(boundingBox, "SRS"));
        if (boundingBox is not null &&
            bboxSrid is not null &&
            TryParseCorner(FirstChildValue(boundingBox, "LowerCorner"), out lower) &&
            TryParseCorner(FirstChildValue(boundingBox, "UpperCorner"), out upper))
        {
            return CreateExtent(lower, upper, bboxSrid);
        }

        return null;
    }

    private static ExternalServiceExtent CreateExtent(double[] lower, double[] upper, int? srid)
        => new()
        {
            XMin = Math.Min(lower[0], upper[0]),
            YMin = Math.Min(lower[1], upper[1]),
            XMax = Math.Max(lower[0], upper[0]),
            YMax = Math.Max(lower[1], upper[1]),
            Srid = srid
        };

    private static int? GetWfsSrid(XElement featureType)
    {
        foreach (var localName in new[] { "DefaultCRS", "DefaultSRS", "SRS", "OtherCRS", "OtherSRS" })
        {
            foreach (var value in ChildValues(featureType, localName))
            {
                if (ParseOgcCrsSrid(value) is { } srid)
                {
                    return srid;
                }
            }
        }

        return null;
    }

    private async Task<OgcLandingDocument?> TryGetOgcLandingAsync(
        Uri serviceUri,
        ResolvedAuth auth,
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
                    auth,
                    timeoutSeconds,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException)
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

        var lastSegment = crs.Split(['/', ':'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
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

    // ---- Authentication -------------------------------------------------------------------------------

    /// <summary>
    /// Resolved, transient authentication material applied to discovery requests: a token appended as a query
    /// parameter (ArcGIS) and/or an Authorization header (Basic), plus an optional Referer.
    /// </summary>
    private readonly record struct ResolvedAuth(string? Token, string? AuthorizationHeader, string? Referer)
    {
        public static ResolvedAuth None => default;
    }

    private async Task<ResolvedAuth> ResolveAuthAsync(
        Uri serviceUri,
        ExternalServiceCredentials? credentials,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (credentials is null || string.IsNullOrWhiteSpace(credentials.Mode))
        {
            return ResolvedAuth.None;
        }

        switch (credentials.Mode.Trim().ToLowerInvariant())
        {
            case "anonymous":
            case "none":
                return ResolvedAuth.None;

            case "token":
                if (string.IsNullOrWhiteSpace(credentials.Token))
                {
                    throw new ExternalServiceDiscoveryRequestException("A token is required for token authentication.");
                }

                return new ResolvedAuth(credentials.Token.Trim(), null, credentials.Referer);

            case "basic":
                if (string.IsNullOrWhiteSpace(credentials.Username))
                {
                    throw new ExternalServiceDiscoveryRequestException(
                        "A username and password are required for basic authentication.");
                }

                var encoded = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{credentials.Username}:{credentials.Password}"));
                return new ResolvedAuth(null, $"Basic {encoded}", null);

            case "arcgis-token":
                return await MintArcGisTokenAsync(serviceUri, credentials, timeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);

            case "oauth":
                return await MintOAuthTokenAsync(credentials, timeoutSeconds, cancellationToken)
                    .ConfigureAwait(false);

            default:
                throw new ExternalServiceDiscoveryRequestException(
                    $"Unsupported authentication mode '{credentials.Mode}'.");
        }
    }

    private async Task<ResolvedAuth> MintArcGisTokenAsync(
        Uri serviceUri,
        ExternalServiceCredentials credentials,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.Username) || string.IsNullOrWhiteSpace(credentials.Password))
        {
            throw new ExternalServiceDiscoveryRequestException(
                "A username and password are required for ArcGIS token authentication.");
        }

        var tokenSource = string.IsNullOrWhiteSpace(credentials.TokenUrl)
            ? DeriveArcGisTokenUrl(serviceUri)
            : credentials.TokenUrl!;
        var tokenUri = await NormalizeAndValidateAsync(tokenSource, cancellationToken).ConfigureAwait(false);

        var form = new List<KeyValuePair<string, string>>
        {
            new("username", credentials.Username!),
            new("password", credentials.Password ?? string.Empty),
            new("f", "json"),
            new("expiration", "60")
        };
        if (!string.IsNullOrWhiteSpace(credentials.Referer))
        {
            form.Add(new("referer", credentials.Referer!));
            form.Add(new("client", "referer"));
        }
        else
        {
            form.Add(new("client", "requestip"));
        }

        var token = await PostFormAsync(
                tokenUri,
                form,
                ExternalServiceDiscoveryJsonContext.Default.ArcGisTokenDocument,
                timeoutSeconds,
                cancellationToken)
            .ConfigureAwait(false);

        if (token.Error is not null || string.IsNullOrWhiteSpace(token.Token))
        {
            throw new ExternalServiceDiscoveryRequestException(
                "ArcGIS token request was rejected. Verify the username, password, and token URL.");
        }

        return new ResolvedAuth(token.Token, null, credentials.Referer);
    }

    private async Task<ResolvedAuth> MintOAuthTokenAsync(
        ExternalServiceCredentials credentials,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(credentials.TokenUrl))
        {
            throw new ExternalServiceDiscoveryRequestException(
                "A token endpoint URL is required for OAuth authentication.");
        }

        if (string.IsNullOrWhiteSpace(credentials.ClientId) || string.IsNullOrWhiteSpace(credentials.ClientSecret))
        {
            throw new ExternalServiceDiscoveryRequestException(
                "A client id and client secret are required for OAuth authentication.");
        }

        var tokenUri = await NormalizeAndValidateAsync(credentials.TokenUrl!, cancellationToken).ConfigureAwait(false);
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "client_credentials"),
            new("client_id", credentials.ClientId!),
            new("client_secret", credentials.ClientSecret!),
            new("f", "json")
        };

        var token = await PostFormAsync(
                tokenUri,
                form,
                ExternalServiceDiscoveryJsonContext.Default.OAuthTokenDocument,
                timeoutSeconds,
                cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(token.Error) || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            var detail = string.IsNullOrWhiteSpace(token.ErrorDescription) ? "." : $": {token.ErrorDescription}";
            throw new ExternalServiceDiscoveryRequestException($"OAuth token request was rejected{detail}");
        }

        return new ResolvedAuth(token.AccessToken, null, null);
    }

    private async Task<T> PostFormAsync<T>(
        Uri uri,
        IEnumerable<KeyValuePair<string, string>> form,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var content = new FormUrlEncodedContent(form);
        using var response = await client.PostAsync(uri, content, timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ExternalServiceDiscoveryRequestException(
                $"Authentication token endpoint returned status {(int)response.StatusCode}.");
        }

        var result = await response.Content.ReadFromJsonAsync(jsonTypeInfo, timeoutCts.Token).ConfigureAwait(false);
        return result ?? throw new ExternalServiceDiscoveryRequestException(
            "Authentication token endpoint returned an empty response.");
    }

    private static string DeriveArcGisTokenUrl(Uri serviceUri)
    {
        // ArcGIS Server co-locates a token endpoint at /{instance}/tokens/generateToken, where {instance} is the
        // first path segment (commonly "arcgis"). Callers can override via Credentials.TokenUrl for portal/AGOL.
        var segments = serviceUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var instance = segments.Length > 0 ? segments[0] : "arcgis";
        return $"{serviceUri.GetLeftPart(UriPartial.Authority)}/{instance}/tokens/generateToken";
    }

    private static HttpRequestMessage BuildAuthenticatedRequest(HttpMethod method, Uri uri, ResolvedAuth auth)
    {
        var request = new HttpRequestMessage(method, ApplyTokenQuery(uri, auth));
        if (!string.IsNullOrEmpty(auth.AuthorizationHeader))
        {
            request.Headers.TryAddWithoutValidation("Authorization", auth.AuthorizationHeader);
        }

        if (!string.IsNullOrEmpty(auth.Referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", auth.Referer);
        }

        return request;
    }

    private static Uri ApplyTokenQuery(Uri uri, ResolvedAuth auth)
    {
        if (string.IsNullOrEmpty(auth.Token))
        {
            return uri;
        }

        var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
        return new Uri($"{uri.AbsoluteUri}{separator}token={Uri.EscapeDataString(auth.Token)}", UriKind.Absolute);
    }

    // ---- ArcGIS catalog enumeration -------------------------------------------------------------------

    /// <summary>
    /// Detects an ArcGIS catalog root (<c>.../rest/services</c>) or single folder (<c>.../rest/services/{folder}</c>)
    /// and returns the folder name (null at the root). Returns false for specific service/layer URLs.
    /// </summary>
    private static bool TryGetArcGisCatalogRemainder(Uri uri, out string? folderPath)
    {
        folderPath = null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var servicesIndex = -1;
        for (var i = 1; i < segments.Length; i++)
        {
            if (segments[i].Equals("services", StringComparison.OrdinalIgnoreCase) &&
                segments[i - 1].Equals("rest", StringComparison.OrdinalIgnoreCase))
            {
                servicesIndex = i;
                break;
            }
        }

        if (servicesIndex < 0)
        {
            return false;
        }

        var remainder = segments.Length - servicesIndex - 1;
        if (remainder == 0)
        {
            return true;
        }

        if (remainder == 1)
        {
            var last = segments[^1];
            if (last.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase) ||
                last.Equals("MapServer", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            folderPath = Uri.UnescapeDataString(last);
            return true;
        }

        return false;
    }

    private static string? GetCatalogServiceType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return null;
        }

        if (type.Equals("FeatureServer", StringComparison.OrdinalIgnoreCase))
        {
            return "FeatureServer";
        }

        return type.Equals("MapServer", StringComparison.OrdinalIgnoreCase) ? "MapServer" : null;
    }

    private static Uri BuildCatalogServiceUri(Uri servicesBaseUri, string serviceName, string serviceType)
        => AppendPathSegment(AppendPathSegment(servicesBaseUri, serviceName.Trim('/')), serviceType);

    private static Uri RemoveLastSegment(Uri uri)
    {
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var trimmed = segments.Length > 0 ? string.Join('/', segments[..^1]) : string.Empty;
        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            Path = "/" + trimmed
        };

        return builder.Uri;
    }

    private static string ExtractCatalogName(Uri uri) => uri.Host;

    private static Uri BuildServiceInfoUri(Uri serviceUri)
        => new($"{serviceUri}?f=json", UriKind.Absolute);

    private static Uri BuildLayerInfoUri(Uri serviceUri, int layerId)
        => new($"{serviceUri}/{layerId}?f=json", UriKind.Absolute);

    private static Uri BuildLayerCountUri(Uri serviceUri, int layerId)
        => new($"{serviceUri}/{layerId}/query?where=1%3D1&returnCountOnly=true&f=json", UriKind.Absolute);

    private static Uri BuildWfsGetCapabilitiesUri(Uri serviceUri)
    {
        var builder = new UriBuilder(serviceUri)
        {
            Query = "service=WFS&request=GetCapabilities"
        };

        return builder.Uri;
    }

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

    private static bool IsWfsDiscoveryRequest(string sourceUrl, Uri serviceUri)
    {
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var originalUri) &&
            QueryParameterEquals(originalUri, "service", "WFS"))
        {
            return true;
        }

        var segments = serviceUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[^1].Equals("wfs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool QueryParameterEquals(Uri uri, string parameterName, string expectedValue)
    {
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0)
        {
            return false;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = pair.IndexOf('=');
            var name = equalsIndex >= 0 ? pair[..equalsIndex] : pair;
            var value = equalsIndex >= 0 ? pair[(equalsIndex + 1)..] : string.Empty;
            if (DecodeQueryComponent(name).Equals(parameterName, StringComparison.OrdinalIgnoreCase) &&
                DecodeQueryComponent(value).Equals(expectedValue, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string DecodeQueryComponent(string value)
        => Uri.UnescapeDataString(value.Replace('+', ' '));

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

    private static string ExtractWfsServiceName(Uri serviceUri)
    {
        var segments = serviceUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return serviceUri.Host;
        }

        return Uri.UnescapeDataString(segments[^1]);
    }

    private static string BuildWfsServiceType(XElement root)
    {
        var version = root.Attribute("version")?.Value;
        return string.IsNullOrWhiteSpace(version) ? "WFS" : $"WFS {version}";
    }

    private static int? GetSharedSrid(ExternalServiceLayerCandidate[] candidates)
    {
        int? sharedSrid = null;
        foreach (var candidate in candidates)
        {
            if (candidate.Srid is null)
            {
                continue;
            }

            if (sharedSrid is null)
            {
                sharedSrid = candidate.Srid;
                continue;
            }

            if (sharedSrid != candidate.Srid)
            {
                return null;
            }
        }

        return sharedSrid;
    }

    private static string? GetWfsServiceMetadataValue(XElement root, string localName)
    {
        var serviceMetadata = root.Elements()
            .FirstOrDefault(static element =>
                element.Name.LocalName.Equals("ServiceIdentification", StringComparison.OrdinalIgnoreCase) ||
                element.Name.LocalName.Equals("Service", StringComparison.OrdinalIgnoreCase));

        return serviceMetadata is null ? null : FirstChildValue(serviceMetadata, localName);
    }

    private static IEnumerable<XElement> DescendantsByLocalName(XContainer container, string localName)
        => container.Descendants()
            .Where(element => element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ChildValues(XElement element, string localName)
        => element.Elements()
            .Where(child => child.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            .Select(child => child.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value));

    private static string? FirstChildValue(XElement element, params string[] localNames)
    {
        foreach (var localName in localNames)
        {
            var value = element.Elements()
                .FirstOrDefault(child => child.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
                ?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? GetAttributeValue(XElement? element, string localName)
        => element?.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;

    private static bool TryGetDoubleAttribute(XElement element, string localName, out double value)
        => double.TryParse(
            GetAttributeValue(element, localName),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value);

    private static bool TryParseCorner(string? value, out double[] coordinates)
    {
        coordinates = [];
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(
            [' ', '\t', '\r', '\n', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            return false;
        }

        coordinates = [x, y];
        return true;
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

        [LoggerMessage(
            EventId = 4184,
            Level = LogLevel.Information,
            Message = "Discovered ArcGIS catalog {CatalogUrl} with {ServiceCount} services and {CandidateCount} candidates")]
        public static partial void CatalogDiscovered(ILogger logger, string catalogUrl, int serviceCount, int candidateCount);

        [LoggerMessage(
            EventId = 4185,
            Level = LogLevel.Debug,
            Message = "Failed to enumerate ArcGIS catalog folder {Folder}")]
        public static partial void CatalogFolderFailed(ILogger logger, string folder, Exception exception);

        [LoggerMessage(
            EventId = 4186,
            Level = LogLevel.Debug,
            Message = "Failed to read ArcGIS catalog service {Service}")]
        public static partial void CatalogServiceFailed(ILogger logger, string service, Exception exception);
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
