// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Logging;
using Polly;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// HTTP client for communicating with ArcGIS REST API services.
/// Handles service discovery, layer metadata retrieval, and paginated feature queries.
/// </summary>
internal sealed partial class ArcGisRestClient
{
    private const string DisallowedNetworkAddressMessage = "ArcGIS service URL resolves to a disallowed network address.";
    private const string InvalidServiceRootUrlMessage =
        "ArcGIS service URL must target a service root URL (FeatureServer or MapServer).";

    private readonly HttpClient _httpClient;
    private readonly ILogger<ArcGisRestClient> _logger;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _hostAddressResolver;

    public ArcGisRestClient(
        HttpClient httpClient,
        ILogger<ArcGisRestClient> logger,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hostAddressResolver = hostAddressResolver ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
    }

    internal static HttpMessageHandler CreatePinnedDnsHttpMessageHandler(
        Func<string, CancellationToken, Task<IPAddress[]>>? hostAddressResolver = null)
    {
        var resolver = hostAddressResolver ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // Bound per-host connections against slow ArcGIS REST endpoints so import
            // discovery cannot exhaust the local socket pool under pathological conditions.
            MaxConnectionsPerServer = 32,
            ConnectCallback = (context, cancellationToken) =>
                ConnectWithPinnedDnsAsync(context, resolver, cancellationToken)
        };
    }

    /// <summary>
    /// Discover service metadata from an ArcGIS Server URL.
    /// </summary>
    public async Task<GeoservicesServiceInfo> DiscoverServiceAsync(
        string serviceUrl,
        int timeoutSeconds,
        int maxRetries,
        CancellationToken cancellationToken,
        GeoservicesCredentialDescriptor? credentials = null)
    {
        var normalizedUrl = NormalizeServiceUrl(serviceUrl);
        Log.DiscoveringService(_logger, normalizedUrl);

        var serviceResponse = await GetJsonAsync(
            $"{normalizedUrl}?f=json",
            ArcGisJsonContext.Default.ArcGisServiceResponse,
            maxRetries,
            timeoutSeconds,
            credentials,
            cancellationToken);

        var layers = new List<GeoservicesLayerInfo>();

        if (serviceResponse.Layers != null)
        {
            foreach (var layer in serviceResponse.Layers)
            {
                try
                {
                    var layerInfo = await GetLayerInfoAsync(
                        normalizedUrl,
                        layer.Id,
                        timeoutSeconds,
                        maxRetries,
                        cancellationToken,
                        credentials);
                    layers.Add(layerInfo);
                }
                catch (Exception ex)
                {
                    Log.LayerDiscoveryFailed(_logger, layer.Id, layer.Name ?? "unknown", ex);
                }
            }
        }

        return new GeoservicesServiceInfo
        {
            ServiceUrl = normalizedUrl,
            ServiceName = serviceResponse.ServiceDescription ?? ExtractServiceName(normalizedUrl),
            Description = serviceResponse.Description,
            SpatialReferenceWkid = serviceResponse.SpatialReference?.Wkid,
            MaxRecordCount = serviceResponse.MaxRecordCount,
            Capabilities = ParseCapabilities(serviceResponse.Capabilities),
            Layers = layers.ToArray(),
            Version = FormatVersion(serviceResponse.CurrentVersion),
            SupportedQueryFormats = ParseQueryFormats(serviceResponse.SupportedQueryFormats)
        };
    }

    /// <summary>
    /// Get detailed metadata for a specific layer.
    /// </summary>
    public async Task<GeoservicesLayerInfo> GetLayerInfoAsync(
        string serviceUrl,
        int layerId,
        int timeoutSeconds,
        int maxRetries,
        CancellationToken cancellationToken,
        GeoservicesCredentialDescriptor? credentials = null)
    {
        var normalizedUrl = NormalizeServiceUrl(serviceUrl);
        var layerUrl = $"{normalizedUrl}/{layerId}?f=json";

        var layerResponse = await GetJsonAsync(
            layerUrl,
            ArcGisJsonContext.Default.ArcGisLayerResponse,
            maxRetries,
            timeoutSeconds,
            credentials,
            cancellationToken);

        // Try to get feature count
        int? featureCount = null;
        try
        {
            var countUrl = $"{normalizedUrl}/{layerId}/query?where=1=1&returnCountOnly=true&f=json";
            var countResponse = await GetJsonAsync(
                countUrl,
                ArcGisJsonContext.Default.ArcGisCountResponse,
                maxRetries,
                timeoutSeconds,
                credentials,
                cancellationToken);
            featureCount = countResponse.Count;
        }
        catch (Exception ex)
        {
            Log.FeatureCountFailed(_logger, layerId, ex);
        }

        return new GeoservicesLayerInfo
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
            FeatureCount = featureCount,
            DrawingInfoJson = layerResponse.DrawingInfo is { ValueKind: JsonValueKind.Object } drawingInfo
                ? drawingInfo.GetRawText()
                : null
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
        int maxRetries,
        CancellationToken cancellationToken,
        GeoservicesCredentialDescriptor? credentials = null)
    {
        var normalizedUrl = NormalizeServiceUrl(serviceUrl);
        var queryUrl = BuildQueryUrl(normalizedUrl, layerId, offset, batchSize, whereClause, outFields, outSrid);

        Log.QueryingFeatures(_logger, layerId, offset, batchSize);

        var response = await GetJsonAsync(
            queryUrl,
            ArcGisJsonContext.Default.ArcGisFeatureResponse,
            maxRetries,
            timeoutSeconds,
            credentials,
            cancellationToken);

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

    private static string BuildQueryUrl(
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
        int maxRetries,
        int timeoutSeconds,
        GeoservicesCredentialDescriptor? credentials,
        CancellationToken cancellationToken)
    {
        await EnsureSafeOutboundUriAsync(url, cancellationToken).ConfigureAwait(false);

        var options = BuildHttpOptions(maxRetries);
        var policy = CreateHttpPolicy(options, maxRetries, cancellationToken);
        using var response = await policy.ExecuteAsync(
            async ct =>
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using var request = CreateGetRequest(url, credentials);
                return await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            },
            cancellationToken);
        ThrowIfAuthenticationFailure(response, url, credentials);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync(jsonTypeInfo, cancellationToken);

        return result ?? throw new InvalidOperationException("Failed to deserialize response");
    }

    internal async Task<JsonDocument> GetJsonDocumentAsync(
        string url,
        int maxRetries,
        int timeoutSeconds,
        GeoservicesCredentialDescriptor? credentials,
        CancellationToken cancellationToken)
    {
        await EnsureSafeOutboundUriAsync(url, cancellationToken).ConfigureAwait(false);

        var options = BuildHttpOptions(maxRetries);
        var policy = CreateHttpPolicy(options, maxRetries, cancellationToken);
        using var response = await policy.ExecuteAsync(
            async ct =>
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                using var request = CreateGetRequest(url, credentials);
                return await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        ThrowIfAuthenticationFailure(response, url, credentials);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return JsonDocument.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse ArcGIS JSON from '{RedactArcGisTokenParameters(url)}'.", ex);
        }
    }

    private static HttpRequestMessage CreateGetRequest(
        string url,
        GeoservicesCredentialDescriptor? credentials)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildAuthenticatedUrl(url, credentials));
        ApplyAuthorizationHeader(request, credentials);
        return request;
    }

    private static string BuildAuthenticatedUrl(
        string url,
        GeoservicesCredentialDescriptor? credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials?.AccessToken))
        {
            return url;
        }

        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}token={Uri.EscapeDataString(credentials.AccessToken)}";
    }

    private static void ThrowIfAuthenticationFailure(
        HttpResponseMessage response,
        string url,
        GeoservicesCredentialDescriptor? credentials)
    {
        var statusCode = (int)response.StatusCode;
        if (statusCode is not (401 or 403 or 498 or 499))
        {
            return;
        }

        var credentialsSupplied = !string.IsNullOrWhiteSpace(credentials?.AccessToken)
            || (!string.IsNullOrWhiteSpace(credentials?.Username)
                && !string.IsNullOrWhiteSpace(credentials?.Password));

        var kind = statusCode switch
        {
            498 => ArcGisAuthenticationFailureKind.CredentialExpired,
            499 => ArcGisAuthenticationFailureKind.CredentialRequired,
            403 => ArcGisAuthenticationFailureKind.CredentialDenied,
            401 => credentialsSupplied
                ? ArcGisAuthenticationFailureKind.CredentialDenied
                : ArcGisAuthenticationFailureKind.CredentialRequired,
            _ => ArcGisAuthenticationFailureKind.CredentialDenied
        };

        var redactedUrl = RedactArcGisTokenParameters(url);
        var message = kind switch
        {
            ArcGisAuthenticationFailureKind.CredentialExpired =>
                $"ArcGIS credential expired (HTTP {statusCode}) for '{redactedUrl}'.",
            ArcGisAuthenticationFailureKind.CredentialRequired =>
                $"ArcGIS service requires authentication (HTTP {statusCode}) for '{redactedUrl}'.",
            _ =>
                $"ArcGIS credential denied (HTTP {statusCode}) for '{redactedUrl}'."
        };

        throw new ArcGisAuthenticationException(kind, statusCode, message);
    }

    private static void ApplyAuthorizationHeader(
        HttpRequestMessage request,
        GeoservicesCredentialDescriptor? credentials)
    {
        if (credentials?.GetNormalizedMode() != GeoservicesAuthenticationModes.Basic ||
            string.IsNullOrWhiteSpace(credentials.Username) ||
            string.IsNullOrWhiteSpace(credentials.Password))
        {
            return;
        }

        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{credentials.Username}:{credentials.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
    }

    private static ResiliencePolicyOptions BuildHttpOptions(int maxRetries)
    {
        return new ResiliencePolicyOptions
        {
            MaxRetryAttempts = Math.Max(0, maxRetries),
            BaseDelay = TimeSpan.FromSeconds(1),
            JitterPercentage = 0.2
        };
    }

    private IAsyncPolicy<HttpResponseMessage> CreateHttpPolicy(
        ResiliencePolicyOptions options,
        int maxRetries,
        CancellationToken cancellationToken)
    {
        var builder = Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .OrResult(response => (int)response.StatusCode >= 500)
            .Or<OperationCanceledException>(_ => !cancellationToken.IsCancellationRequested);

        return ResiliencePolicyFactory.CreateStandardPolicy(
            builder,
            options,
            onRetry: (result, delay, attempt) =>
            {
                Log.RetryingRequest(_logger, attempt, maxRetries, delay.TotalSeconds, GetFailureMessage(result));
                result.Result?.Dispose();
            });
    }

    private static string GetFailureMessage(DelegateResult<HttpResponseMessage> result)
    {
        if (result.Exception != null)
        {
            return RedactArcGisTokenParameters(result.Exception.Message);
        }

        return result.Result != null
            ? $"HTTP {(int)result.Result.StatusCode}"
            : "Unknown failure";
    }

    private static string RedactArcGisTokenParameters(string value)
    {
        const string TokenParameter = "token=";
        const string Redacted = "<redacted>";

        var searchStart = 0;
        while (true)
        {
            var tokenIndex = value.IndexOf(TokenParameter, searchStart, StringComparison.OrdinalIgnoreCase);
            if (tokenIndex < 0)
            {
                return value;
            }

            var valueStart = tokenIndex + TokenParameter.Length;
            var valueEnd = valueStart;
            while (valueEnd < value.Length && value[valueEnd] is not '&' and not ' ' and not '\'' and not '"')
            {
                valueEnd++;
            }

            value = string.Concat(value.AsSpan(0, valueStart), Redacted, value.AsSpan(valueEnd));
            searchStart = valueStart + Redacted.Length;
        }
    }

    internal static string NormalizeServiceUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var normalizedUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            if (!IsServiceRootUrl(normalizedUrl))
            {
                throw new HttpRequestException(InvalidServiceRootUrlMessage);
            }

            return normalizedUrl;
        }

        return url.TrimEnd('/');
    }

    private static bool IsServiceRootUrl(string normalizedUrl)
    {
        var segments = normalizedUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 &&
            (segments[^1].Equals("FeatureServer", StringComparison.OrdinalIgnoreCase)
             || segments[^1].Equals("MapServer", StringComparison.OrdinalIgnoreCase));
    }

    private async Task EnsureSafeOutboundUriAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new HttpRequestException("ArcGIS service URL must be a valid HTTPS URL.");
        }

        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            throw new HttpRequestException("ArcGIS service URL must not include embedded credentials.");
        }

        if (uri.IsLoopback || IsLocalhostHostName(uri.Host))
        {
            throw new HttpRequestException(DisallowedNetworkAddressMessage);
        }

        _ = await ResolveAllowedAddressesAsync(uri.DnsSafeHost, _hostAddressResolver, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IPAddress[]> ResolveAllowedAddressesAsync(
        string host,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new HttpRequestException(DisallowedNetworkAddressMessage);
        }

        if (IsLocalhostHostName(host))
        {
            throw new HttpRequestException(DisallowedNetworkAddressMessage);
        }

        if (IPAddress.TryParse(host, out var literalAddress))
        {
            if (IsPrivateOrReservedAddress(literalAddress))
            {
                throw new HttpRequestException(DisallowedNetworkAddressMessage);
            }

            return [literalAddress];
        }

        IPAddress[] addresses;
        try
        {
            addresses = await hostAddressResolver(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HttpRequestException(DisallowedNetworkAddressMessage, ex);
        }

        if (addresses.Length == 0)
        {
            throw new HttpRequestException(DisallowedNetworkAddressMessage);
        }

        foreach (var address in addresses)
        {
            if (IsPrivateOrReservedAddress(address))
            {
                throw new HttpRequestException(DisallowedNetworkAddressMessage);
            }
        }

        return addresses;
    }

    private static async ValueTask<Stream> ConnectWithPinnedDnsAsync(
        SocketsHttpConnectionContext context,
        Func<string, CancellationToken, Task<IPAddress[]>> hostAddressResolver,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveAllowedAddressesAsync(
                context.DnsEndPoint.Host,
                hostAddressResolver,
                cancellationToken)
            .ConfigureAwait(false);

        Exception? lastException = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var connected = false;

            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken).ConfigureAwait(false);
                connected = true;
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                lastException = ex;
            }
            finally
            {
                if (!connected)
                {
                    socket.Dispose();
                }
            }
        }

        throw new HttpRequestException("Unable to establish a secure connection to the ArcGIS service host.", lastException);
    }

    private static bool IsLocalhostHostName(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivateOrReservedAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            if (bytes[0] == 0)
            {
                return true;
            }

            if (bytes[0] == 10)
            {
                return true;
            }

            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
            {
                return true;
            }

            if (bytes[0] == 127)
            {
                return true;
            }

            if (bytes[0] == 169 && bytes[1] == 254)
            {
                return true;
            }

            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            {
                return true;
            }

            if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
            {
                return true;
            }

            if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2)
            {
                return true;
            }

            if (bytes[0] == 192 && bytes[1] == 168)
            {
                return true;
            }

            if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
            {
                return true;
            }

            if (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
            {
                return true;
            }

            if (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
            {
                return true;
            }

            if (bytes[0] >= 224)
            {
                return true;
            }
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();

            if (address.Equals(IPAddress.IPv6None))
            {
                return true;
            }

            if (address.Equals(IPAddress.IPv6Loopback))
            {
                return true;
            }

            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            {
                return true;
            }

            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0xc0)
            {
                return true;
            }

            if ((bytes[0] & 0xfe) == 0xfc)
            {
                return true;
            }

            if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            {
                return true;
            }

            if (bytes[0] == 0xff)
            {
                return true;
            }
        }

        return false;
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

    private static GeoservicesFieldInfo[] ParseFields(ArcGisField[]? fields)
    {
        if (fields == null || fields.Length == 0)
            return [];

        return fields.Select(f => new GeoservicesFieldInfo
        {
            Name = f.Name ?? "unknown",
            Type = f.Type ?? "esriFieldTypeString",
            Alias = f.Alias,
            Length = f.Length,
            Nullable = f.Nullable ?? true,
            Domain = MapFieldDomain(f.Domain)
        }).ToArray();
    }

    private static MetadataV2FieldDomain? MapFieldDomain(ArcGisFieldDomain? source)
    {
        if (source == null || string.IsNullOrWhiteSpace(source.Type))
        {
            return null;
        }

        var type = source.Type!.Trim();
        if (string.Equals(type, "codedValue", StringComparison.Ordinal))
        {
            var coded = (source.CodedValues ?? [])
                .Where(static cv => !string.IsNullOrWhiteSpace(cv.Name)
                    && cv.Code.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
                .Select(static cv => new MetadataV2CodedValue
                {
                    Code = cv.Code.Clone(),
                    Name = cv.Name!
                })
                .ToArray();

            if (coded.Length == 0)
            {
                return null;
            }

            return new MetadataV2FieldDomain
            {
                Type = type,
                Name = string.IsNullOrWhiteSpace(source.Name) ? null : source.Name,
                CodedValues = coded,
                MergePolicy = source.MergePolicy,
                SplitPolicy = source.SplitPolicy
            };
        }

        if (string.Equals(type, "range", StringComparison.Ordinal) &&
            source.Range is { Length: >= 2 } range)
        {
            return new MetadataV2FieldDomain
            {
                Type = type,
                Name = string.IsNullOrWhiteSpace(source.Name) ? null : source.Name,
                Range = [range[0].Clone(), range[1].Clone()],
                MergePolicy = source.MergePolicy,
                SplitPolicy = source.SplitPolicy
            };
        }

        return null;
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

    private static string? FormatVersion(JsonElement? version)
    {
        if (version is null)
        {
            return null;
        }

        var element = version.Value;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.String => element.GetString(),
            _ => element.ToString()
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

        [LoggerMessage(
            EventId = 7504,
            Level = LogLevel.Warning,
            Message = "ArcGIS request attempt {Attempt}/{MaxRetries} failed, retrying in {DelaySeconds}s: {ErrorMessage}")]
        public static partial void RetryingRequest(
            ILogger logger, int attempt, int maxRetries, double delaySeconds, string errorMessage);
    }
}

/// <summary>
/// Categorises authentication-related ArcGIS REST failures so callers can surface
/// structured diagnostics (credential expired vs. denied vs. required) without
/// reflecting raw token material into logs or job state.
/// </summary>
internal enum ArcGisAuthenticationFailureKind
{
    CredentialRequired,
    CredentialExpired,
    CredentialDenied
}

/// <summary>
/// Thrown by <see cref="ArcGisRestClient"/> when an ArcGIS REST request fails with
/// an authentication-related HTTP status (401/403/498/499). The exception carries the
/// upstream HTTP status code and a categorised failure kind so import job handlers can
/// emit deterministic credential diagnostics instead of generic transport errors.
/// </summary>
internal sealed class ArcGisAuthenticationException : HttpRequestException
{
    public ArcGisAuthenticationException(
        ArcGisAuthenticationFailureKind kind,
        int upstreamStatusCode,
        string message)
        : base(message)
    {
        Kind = kind;
        UpstreamStatusCode = upstreamStatusCode;
    }

    public ArcGisAuthenticationFailureKind Kind { get; }

    public int UpstreamStatusCode { get; }
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
    public JsonElement? CurrentVersion { get; init; }

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

    [JsonPropertyName("drawingInfo")]
    public JsonElement? DrawingInfo { get; init; }
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
    public bool? Nullable { get; init; }

    [JsonPropertyName("domain")]
    public ArcGisFieldDomain? Domain { get; init; }
}

internal sealed record ArcGisFieldDomain
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("codedValues")]
    public ArcGisCodedValue[]? CodedValues { get; init; }

    [JsonPropertyName("range")]
    public JsonElement[]? Range { get; init; }

    [JsonPropertyName("mergePolicy")]
    public string? MergePolicy { get; init; }

    [JsonPropertyName("splitPolicy")]
    public string? SplitPolicy { get; init; }
}

internal sealed record ArcGisCodedValue
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("code")]
    public JsonElement Code { get; init; }
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
