// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// HTTP client for communicating with ArcGIS REST API services.
/// Handles service discovery, layer metadata retrieval, and paginated feature queries.
/// </summary>
internal sealed partial class ArcGisRestClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ArcGisRestClient> _logger;

    public ArcGisRestClient(HttpClient httpClient, ILogger<ArcGisRestClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Discover service metadata from an ArcGIS Server URL.
    /// </summary>
    public async Task<EsriServiceInfo> DiscoverServiceAsync(
        string serviceUrl,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeServiceUrl(serviceUrl);
        Log.DiscoveringService(_logger, normalizedUrl);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var serviceResponse = await GetJsonAsync(
            $"{normalizedUrl}?f=json",
            ArcGisJsonContext.Default.ArcGisServiceResponse,
            cts.Token);

        var layers = new List<EsriLayerInfo>();

        if (serviceResponse.Layers != null)
        {
            foreach (var layer in serviceResponse.Layers)
            {
                try
                {
                    var layerInfo = await GetLayerInfoAsync(normalizedUrl, layer.Id, timeoutSeconds, cancellationToken);
                    layers.Add(layerInfo);
                }
                catch (Exception ex)
                {
                    Log.LayerDiscoveryFailed(_logger, layer.Id, layer.Name ?? "unknown", ex);
                }
            }
        }

        return new EsriServiceInfo
        {
            ServiceUrl = normalizedUrl,
            ServiceName = serviceResponse.ServiceDescription ?? ExtractServiceName(normalizedUrl),
            Description = serviceResponse.Description,
            SpatialReferenceWkid = serviceResponse.SpatialReference?.Wkid,
            MaxRecordCount = serviceResponse.MaxRecordCount,
            Capabilities = ParseCapabilities(serviceResponse.Capabilities),
            Layers = layers.ToArray(),
            Version = serviceResponse.CurrentVersion,
            SupportedQueryFormats = ParseQueryFormats(serviceResponse.SupportedQueryFormats)
        };
    }

    /// <summary>
    /// Get detailed metadata for a specific layer.
    /// </summary>
    public async Task<EsriLayerInfo> GetLayerInfoAsync(
        string serviceUrl,
        int layerId,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeServiceUrl(serviceUrl);
        var layerUrl = $"{normalizedUrl}/{layerId}?f=json";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var layerResponse = await GetJsonAsync(
            layerUrl,
            ArcGisJsonContext.Default.ArcGisLayerResponse,
            cts.Token);

        // Try to get feature count
        int? featureCount = null;
        try
        {
            var countUrl = $"{normalizedUrl}/{layerId}/query?where=1=1&returnCountOnly=true&f=json";
            var countResponse = await GetJsonAsync(
                countUrl,
                ArcGisJsonContext.Default.ArcGisCountResponse,
                cts.Token);
            featureCount = countResponse.Count;
        }
        catch (Exception ex)
        {
            Log.FeatureCountFailed(_logger, layerId, ex);
        }

        return new EsriLayerInfo
        {
            Id = layerResponse.Id,
            Name = layerResponse.Name ?? $"Layer {layerResponse.Id}",
            Description = layerResponse.Description,
            GeometryType = layerResponse.GeometryType,
            SpatialReferenceWkid = layerResponse.Extent?.SpatialReference?.Wkid,
            MaxRecordCount = layerResponse.MaxRecordCount,
            Fields = ParseFields(layerResponse.Fields),
            Type = layerResponse.Type,
            HasAttachments = layerResponse.HasAttachments,
            MinScale = layerResponse.MinScale,
            MaxScale = layerResponse.MaxScale,
            Extent = ParseExtent(layerResponse.Extent),
            FeatureCount = featureCount
        };
    }

    /// <summary>
    /// Query features from a layer with pagination support.
    /// </summary>
    public async Task<ArcGisQueryResult> QueryFeaturesAsync(
        string serviceUrl,
        int layerId,
        int offset,
        int batchSize,
        string? whereClause,
        string[]? outFields,
        int? outSrid,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var normalizedUrl = NormalizeServiceUrl(serviceUrl);
        var queryUrl = BuildQueryUrl(normalizedUrl, layerId, offset, batchSize, whereClause, outFields, outSrid);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        Log.QueryingFeatures(_logger, layerId, offset, batchSize);

        var response = await GetJsonAsync(
            queryUrl,
            ArcGisJsonContext.Default.ArcGisFeatureResponse,
            cts.Token);

        if (response.Error != null)
        {
            throw new InvalidOperationException(
                $"ArcGIS query error {response.Error.Code}: {response.Error.Message}");
        }

        return new ArcGisQueryResult
        {
            Features = response.Features ?? [],
            ExceededTransferLimit = response.ExceededTransferLimit,
            SpatialReferenceWkid = response.SpatialReference?.Wkid
        };
    }

    private string BuildQueryUrl(
        string serviceUrl,
        int layerId,
        int offset,
        int batchSize,
        string? whereClause,
        string[]? outFields,
        int? outSrid)
    {
        var query = new List<string>
        {
            "f=json",
            $"where={Uri.EscapeDataString(whereClause ?? "1=1")}",
            $"outFields={Uri.EscapeDataString(outFields != null ? string.Join(",", outFields) : "*")}",
            "returnGeometry=true",
            $"resultOffset={offset}",
            $"resultRecordCount={batchSize}"
        };

        if (outSrid.HasValue)
        {
            query.Add($"outSR={outSrid.Value}");
        }

        return $"{serviceUrl}/{layerId}/query?{string.Join("&", query)}";
    }

    private async Task<T> GetJsonAsync<T>(
        string url,
        JsonTypeInfo<T> jsonTypeInfo,
        CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync(jsonTypeInfo, cancellationToken);

        return result ?? throw new InvalidOperationException("Failed to deserialize response");
    }

    private static string NormalizeServiceUrl(string url)
    {
        url = url.TrimEnd('/');

        // Remove query string if present
        var queryIndex = url.IndexOf('?');
        if (queryIndex > 0)
        {
            url = url[..queryIndex];
        }

        return url;
    }

    private static string ExtractServiceName(string url)
    {
        var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Find FeatureServer or MapServer and get the previous segment
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            if (segments[i].Equals("FeatureServer", StringComparison.OrdinalIgnoreCase) ||
                segments[i].Equals("MapServer", StringComparison.OrdinalIgnoreCase))
            {
                return i > 0 ? segments[i - 1] : "Unknown Service";
            }
        }

        return segments.Length > 0 ? segments[^1] : "Unknown Service";
    }

    private static string[] ParseCapabilities(string? capabilities)
    {
        if (string.IsNullOrWhiteSpace(capabilities))
            return [];

        return capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string[] ParseQueryFormats(string? formats)
    {
        if (string.IsNullOrWhiteSpace(formats))
            return ["JSON"];

        return formats.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static EsriFieldInfo[] ParseFields(ArcGisField[]? fields)
    {
        if (fields == null || fields.Length == 0)
            return [];

        return fields.Select(f => new EsriFieldInfo
        {
            Name = f.Name ?? "unknown",
            Type = f.Type ?? "esriFieldTypeString",
            Alias = f.Alias,
            Length = f.Length,
            Nullable = f.Nullable
        }).ToArray();
    }

    private static EsriExtent? ParseExtent(ArcGisExtent? extent)
    {
        if (extent == null)
            return null;

        return new EsriExtent
        {
            Xmin = extent.Xmin,
            Ymin = extent.Ymin,
            Xmax = extent.Xmax,
            Ymax = extent.Ymax,
            SpatialReferenceWkid = extent.SpatialReference?.Wkid
        };
    }

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 7500,
            Level = LogLevel.Information,
            Message = "Discovering ArcGIS service at {ServiceUrl}")]
        public static partial void DiscoveringService(ILogger logger, string serviceUrl);

        [LoggerMessage(
            EventId = 7501,
            Level = LogLevel.Warning,
            Message = "Failed to discover layer {LayerId} ({LayerName})")]
        public static partial void LayerDiscoveryFailed(
            ILogger logger, int layerId, string layerName, Exception exception);

        [LoggerMessage(
            EventId = 7502,
            Level = LogLevel.Debug,
            Message = "Querying features from layer {LayerId}, offset={Offset}, batchSize={BatchSize}")]
        public static partial void QueryingFeatures(
            ILogger logger, int layerId, int offset, int batchSize);

        [LoggerMessage(
            EventId = 7503,
            Level = LogLevel.Debug,
            Message = "Failed to get feature count for layer {LayerId}")]
        public static partial void FeatureCountFailed(ILogger logger, int layerId, Exception exception);
    }
}

/// <summary>
/// Result of a paginated feature query.
/// </summary>
internal sealed record ArcGisQueryResult
{
    public required ArcGisFeature[] Features { get; init; }
    public bool ExceededTransferLimit { get; init; }
    public int? SpatialReferenceWkid { get; init; }
}

// JSON response models for ArcGIS REST API
#pragma warning disable CA1812 // Internal class is never instantiated (used for JSON deserialization)

internal sealed record ArcGisServiceResponse
{
    [JsonPropertyName("serviceDescription")]
    public string? ServiceDescription { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("currentVersion")]
    public string? CurrentVersion { get; init; }

    [JsonPropertyName("maxRecordCount")]
    public int? MaxRecordCount { get; init; }

    [JsonPropertyName("capabilities")]
    public string? Capabilities { get; init; }

    [JsonPropertyName("supportedQueryFormats")]
    public string? SupportedQueryFormats { get; init; }

    [JsonPropertyName("spatialReference")]
    public ArcGisSpatialReference? SpatialReference { get; init; }

    [JsonPropertyName("layers")]
    public ArcGisLayerRef[]? Layers { get; init; }
}

internal sealed record ArcGisLayerRef
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed record ArcGisLayerResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("geometryType")]
    public string? GeometryType { get; init; }

    [JsonPropertyName("maxRecordCount")]
    public int? MaxRecordCount { get; init; }

    [JsonPropertyName("hasAttachments")]
    public bool HasAttachments { get; init; }

    [JsonPropertyName("minScale")]
    public double? MinScale { get; init; }

    [JsonPropertyName("maxScale")]
    public double? MaxScale { get; init; }

    [JsonPropertyName("extent")]
    public ArcGisExtent? Extent { get; init; }

    [JsonPropertyName("fields")]
    public ArcGisField[]? Fields { get; init; }
}

internal sealed record ArcGisSpatialReference
{
    [JsonPropertyName("wkid")]
    public int? Wkid { get; init; }

    [JsonPropertyName("latestWkid")]
    public int? LatestWkid { get; init; }
}

internal sealed record ArcGisExtent
{
    [JsonPropertyName("xmin")]
    public double Xmin { get; init; }

    [JsonPropertyName("ymin")]
    public double Ymin { get; init; }

    [JsonPropertyName("xmax")]
    public double Xmax { get; init; }

    [JsonPropertyName("ymax")]
    public double Ymax { get; init; }

    [JsonPropertyName("spatialReference")]
    public ArcGisSpatialReference? SpatialReference { get; init; }
}

internal sealed record ArcGisField
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    [JsonPropertyName("length")]
    public int? Length { get; init; }

    [JsonPropertyName("nullable")]
    public bool Nullable { get; init; } = true;
}

internal sealed record ArcGisCountResponse
{
    [JsonPropertyName("count")]
    public int Count { get; init; }
}

internal sealed record ArcGisFeatureResponse
{
    [JsonPropertyName("features")]
    public ArcGisFeature[]? Features { get; init; }

    [JsonPropertyName("exceededTransferLimit")]
    public bool ExceededTransferLimit { get; init; }

    [JsonPropertyName("spatialReference")]
    public ArcGisSpatialReference? SpatialReference { get; init; }

    [JsonPropertyName("error")]
    public ArcGisError? Error { get; init; }
}

internal sealed record ArcGisFeature
{
    [JsonPropertyName("attributes")]
    public Dictionary<string, JsonElement>? Attributes { get; init; }

    [JsonPropertyName("geometry")]
    public JsonElement? Geometry { get; init; }
}

internal sealed record ArcGisError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("details")]
    public string[]? Details { get; init; }
}

#pragma warning restore CA1812

/// <summary>
/// JSON serialization context for ArcGIS REST API responses.
/// </summary>
[JsonSerializable(typeof(ArcGisServiceResponse))]
[JsonSerializable(typeof(ArcGisLayerResponse))]
[JsonSerializable(typeof(ArcGisCountResponse))]
[JsonSerializable(typeof(ArcGisFeatureResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ArcGisJsonContext : JsonSerializerContext
{
}
