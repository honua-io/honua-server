// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
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
            // TODO honua-server#977: add OGC API Features landing/collections discovery here.
            throw new ExternalServiceDiscoveryRequestException(
                "Only ArcGIS REST FeatureServer and MapServer service root URLs are supported. OGC API Features discovery is tracked by honua-server#977.");
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
            LayerId = reference.Id,
            Name = FirstNonWhiteSpace(reference.Name, $"Layer {reference.Id}"),
            Srid = GetSrid(serviceSpatialReference)
        };

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

    private static int? GetSrid(ArcGisSpatialReferenceDocument? spatialReference)
        => spatialReference?.LatestWkid ?? spatialReference?.Wkid;

    private static int ClampTimeout(int? timeoutSeconds)
        => Math.Clamp(timeoutSeconds ?? DefaultTimeoutSeconds, 1, MaximumTimeoutSeconds);

    private static string FirstNonWhiteSpace(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? "External Service";

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
