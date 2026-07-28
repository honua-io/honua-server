// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Security.Abstractions;

namespace Honua.Geoprocessing.Inference;

/// <summary>
/// Generic REST provider adapter for the delegated imagery/ML inference lane
/// (#2241) — the end-to-end-supported backend family. Targets any hosted
/// inference HTTP endpoint that speaks a simple JSON contract: OpenAI-compatible
/// inference gateways, hosted-ONNX model servers, and Azure ML online-endpoint
/// style invocation URLs (bearer-key auth). SageMaker/Vertex SDK-signed
/// invocation is intentionally NOT implemented here; those provider ids fail
/// clearly in the executor until dedicated adapters land.
/// </summary>
/// <remarks>
/// Request contract (JSON): <c>{ "model", "task", "image" (base64 GeoTIFF),
/// "imageMediaType", "confidenceThreshold"? }</c>.
/// Response contract (JSON): <c>{ "outputType": "raster"|"features",
/// "raster": base64 GeoTIFF | "features": GeoJSON FeatureCollection }</c>.
/// The backend must preserve the source georeferencing: raster outputs carry the
/// source grid/CRS in the returned GeoTIFF, feature outputs use source-CRS
/// coordinates. The adapter passes raster bytes through byte-for-byte, so
/// whatever georeferencing the backend emits is exactly what lands in the
/// artifact.
/// All thrown <see cref="ImageryInferenceException"/> messages are safe for job
/// status: no endpoint URL, credentials, or raw response bodies.
/// </remarks>
internal sealed partial class HttpImageryInferenceClient : IImageryInferenceClient
{
    /// <summary>Named <see cref="HttpClient"/> registration used by this adapter.</summary>
    internal const string HttpClientName = "honua-imagery-inference";

    /// <summary>Provider id served by this adapter.</summary>
    internal const string ProviderId = "http";

    private const string GeoTiffMediaType = "image/tiff; application=geotiff";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<HttpImageryInferenceClient> _logger;
    private readonly ISecretProvider? _secretProvider;

    public HttpImageryInferenceClient(
        IHttpClientFactory httpClientFactory,
        ILogger<HttpImageryInferenceClient> logger,
        ISecretProvider? secretProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _secretProvider = secretProvider;
    }

    public string Provider => ProviderId;

    public async Task<ImageryInferenceOutcome> InferAsync(
        ImageryInferenceOptions options,
        ImageryInferenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            throw new ImageryInferenceException(
                "the configured 'http' inference backend has a missing or invalid endpoint; " +
                $"set {ImageryInferenceOptions.SectionName}:Endpoint to the backend's absolute https invocation URL.");
        }

        // Both the API key and the full source scene leave the process on this
        // request, so plaintext http:// is refused outright — a deployment typo or
        // a non-TLS remote endpoint would otherwise expose credentials and
        // potentially sensitive imagery on the wire. The only exception is a
        // loopback host, where the traffic never leaves the machine and local
        // model-server development is a legitimate workflow.
        if (endpoint.Scheme == Uri.UriSchemeHttp && !endpoint.IsLoopback)
        {
            throw new ImageryInferenceException(
                "the configured inference endpoint uses plaintext http:// to a non-loopback host; " +
                "the API key and the full source raster would travel unencrypted. Use an https:// endpoint " +
                "(plain http is permitted only for loopback development backends).");
        }

        var apiKey = await ResolveApiKeyAsync(options, cancellationToken).ConfigureAwait(false);

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(BuildRequestBody(request))
        };
        message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (!string.IsNullOrEmpty(apiKey))
        {
            if (string.IsNullOrWhiteSpace(options.ApiKeyHeader))
            {
                message.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            }
            else
            {
                message.Headers.TryAddWithoutValidation(options.ApiKeyHeader, apiKey);
            }
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 1, 3600));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        HttpResponseMessage response;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            response = await client
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.RequestTimedOut(_logger, endpoint.Host, (int)timeout.TotalSeconds);
            throw new ImageryInferenceException(
                $"the inference request timed out after {(int)timeout.TotalSeconds}s; " +
                "increase the configured TimeoutSeconds or reduce the scene size.");
        }
        catch (HttpRequestException ex)
        {
            // Log the transport detail server-side; the job-status message stays
            // endpoint-free and detail-free.
            Log.RequestFailed(_logger, endpoint.Host, ex);
            throw new ImageryInferenceException(
                "the configured inference endpoint could not be reached.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                Log.BackendErrorStatus(_logger, endpoint.Host, (int)response.StatusCode);
                throw new ImageryInferenceException(
                    $"the inference request failed with HTTP {(int)response.StatusCode}.");
            }

            byte[] body;
            try
            {
                body = await ReadBoundedBodyAsync(response, request.MaxArtifactBytes, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The endpoint answered with headers inside the budget but then stalled
                // mid-body. Without this translation the OperationCanceledException
                // escapes and the job service misreports the failure as
                // "Cancelled by operator" even though the caller never cancelled.
                Log.RequestTimedOut(_logger, endpoint.Host, (int)timeout.TotalSeconds);
                throw new ImageryInferenceException(
                    $"the inference response body stalled and timed out after {(int)timeout.TotalSeconds}s; " +
                    "increase the configured TimeoutSeconds or reduce the scene size.");
            }
            catch (HttpRequestException ex)
            {
                Log.RequestFailed(_logger, endpoint.Host, ex);
                throw new ImageryInferenceException(
                    "the inference response body could not be read from the endpoint.", ex);
            }

            return ParseResponse(body);
        }
    }

    /// <summary>
    /// Resolves the backend API key: secret reference via the registered
    /// <see cref="ISecretProvider"/> first, then the literal configured value,
    /// then the <see cref="ImageryInferenceOptions.ApiKeyEnvironmentVariable"/>
    /// fallback. Mirrors the WorkflowGeneration key-resolution order.
    /// </summary>
    private async Task<string> ResolveApiKeyAsync(
        ImageryInferenceOptions options,
        CancellationToken cancellationToken)
    {
        var configured = options.ApiKey;

        if (!string.IsNullOrWhiteSpace(configured)
            && _secretProvider is not null
            && _secretProvider.IsSecretReference(configured))
        {
            var resolved = await _secretProvider
                .GetSecretOrDefaultAsync(configured, defaultValue: null, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }

            throw new ImageryInferenceException(
                "the configured inference API key secret reference could not be resolved.");
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var fromEnv = Environment.GetEnvironmentVariable(ImageryInferenceOptions.ApiKeyEnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnv) ? string.Empty : fromEnv;
    }

    private static byte[] BuildRequestBody(ImageryInferenceRequest request)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", request.Model);
            writer.WriteString("task", request.Task);
            writer.WriteBase64String("image", request.ImageBytes);
            writer.WriteString("imageMediaType", GeoTiffMediaType);
            if (request.ConfidenceThreshold is { } threshold)
            {
                writer.WriteNumber("confidenceThreshold", threshold);
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Buffers the response body with a hard cap derived from the artifact size
    /// ceiling (base64 inflation + JSON envelope headroom), so a runaway backend
    /// cannot exhaust worker memory before the executor's own artifact guard runs.
    /// </summary>
    private static async Task<byte[]> ReadBoundedBodyAsync(
        HttpResponseMessage response,
        long maxArtifactBytes,
        CancellationToken cancellationToken)
    {
        // base64 inflates 4/3x; leave envelope headroom on top.
        var cap = Math.Max(1024L * 1024L, maxArtifactBytes * 2);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                throw new ImageryInferenceException(
                    $"the inference response exceeded the {cap} byte response ceiling; " +
                    "reduce the scene size or raise Geoprocessing:Executors:MaxArtifactBytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static ImageryInferenceOutcome ParseResponse(byte[] body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new ImageryInferenceException("the inference backend returned a non-JSON response.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("outputType", out var outputTypeElement)
                || outputTypeElement.ValueKind != JsonValueKind.String)
            {
                throw new ImageryInferenceException(
                    "the inference backend response is missing the required 'outputType' " +
                    "('raster' or 'features') discriminator.");
            }

            var outputType = outputTypeElement.GetString();
            if (string.Equals(outputType, "raster", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("raster", out var rasterElement)
                    || rasterElement.ValueKind != JsonValueKind.String
                    || !rasterElement.TryGetBytesFromBase64(out var rasterBytes)
                    || rasterBytes is null
                    || rasterBytes.Length == 0)
                {
                    throw new ImageryInferenceException(
                        "the inference backend declared a raster output but supplied no valid " +
                        "base64 'raster' payload.");
                }

                return new ImageryInferenceOutcome
                {
                    OutputType = ImageryInferenceOutputType.Raster,
                    RasterBytes = rasterBytes
                };
            }

            if (string.Equals(outputType, "features", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("features", out var featuresElement)
                    || featuresElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ImageryInferenceException(
                        "the inference backend declared a features output but supplied no " +
                        "'features' GeoJSON object.");
                }

                using var featureBuffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(featureBuffer))
                {
                    featuresElement.WriteTo(writer);
                }

                return new ImageryInferenceOutcome
                {
                    OutputType = ImageryInferenceOutputType.Features,
                    FeatureCollectionJson = featureBuffer.ToArray()
                };
            }

            throw new ImageryInferenceException(
                $"the inference backend returned an unrecognized outputType '{outputType}'; " +
                "expected 'raster' or 'features'.");
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9310, LogLevel.Warning,
            "Imagery inference request to backend host {Host} timed out after {TimeoutSeconds}s")]
        public static partial void RequestTimedOut(ILogger logger, string host, int timeoutSeconds);

        [LoggerMessage(9311, LogLevel.Warning,
            "Imagery inference request to backend host {Host} failed at transport level")]
        public static partial void RequestFailed(ILogger logger, string host, Exception exception);

        [LoggerMessage(9312, LogLevel.Warning,
            "Imagery inference backend host {Host} returned HTTP {StatusCode}")]
        public static partial void BackendErrorStatus(ILogger logger, string host, int statusCode);
    }
}
