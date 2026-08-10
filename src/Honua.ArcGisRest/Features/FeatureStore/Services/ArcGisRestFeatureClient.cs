// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ArcGisRest.Features.FeatureStore.Models;

namespace Honua.ArcGisRest.Features.FeatureStore.Services;

/// <summary>
/// Outbound HTTP client used by the federated ArcGIS REST provider. Each method
/// issues a single <c>/query</c> request and returns the strongly typed wire model
/// without applying any business logic — translation between Honua's canonical
/// query/feature model and the Esri wire format happens in
/// <see cref="ArcGisRestFeatureStore"/>.
/// </summary>
/// <remarks>
/// The HttpClient itself is registered through <see cref="ArcGisRestServiceClientName"/>
/// in the DI composition root and managed by <see cref="System.Net.Http.IHttpClientFactory"/>.
/// <para>The optional ArcGIS API token is passed as an <c>X-Esri-Authorization: Bearer …</c>
/// request header rather than a query parameter to prevent it from appearing in
/// HTTP logs, OpenTelemetry traces, or upstream server access logs.</para>
/// </remarks>
internal interface IArcGisRestFeatureClient
{
    /// <summary>Fetches a page of features from the remote layer.</summary>
    Task<ArcGisRestQueryResponse> QueryAsync(string url, string? authorizationHeader, CancellationToken cancellationToken);

    /// <summary>Fetches a record-count from the remote layer.</summary>
    Task<ArcGisRestCountResponse> QueryCountAsync(string url, string? authorizationHeader, CancellationToken cancellationToken);

    /// <summary>Fetches the extent envelope from the remote layer.</summary>
    Task<ArcGisRestExtentResponse> QueryExtentAsync(string url, string? authorizationHeader, CancellationToken cancellationToken);

    /// <summary>Fetches an object-id list from the remote layer.</summary>
    Task<ArcGisRestObjectIdsResponse> QueryObjectIdsAsync(string url, string? authorizationHeader, CancellationToken cancellationToken);

    /// <summary>Fetches the layer metadata document for the remote layer.</summary>
    Task<ArcGisRestLayerResponse> GetLayerMetadataAsync(string url, string? authorizationHeader, CancellationToken cancellationToken);
}

/// <summary>
/// HttpClient-backed implementation of <see cref="IArcGisRestFeatureClient"/>.
/// </summary>
internal sealed class ArcGisRestFeatureClient : IArcGisRestFeatureClient
{
    /// <summary>
    /// ArcGIS Server and ArcGIS Online header for bearer-token authentication.
    /// Using a request header rather than the <c>token</c> query parameter prevents
    /// the credential from appearing in HTTP access logs and telemetry traces.
    /// </summary>
    private const string EsriAuthorizationHeader = "X-Esri-Authorization";

    /// <summary>
    /// Maximum number of bytes the federated client will read from an upstream
    /// ArcGIS response body (64 MiB). The module's threat model treats the remote
    /// service as potentially hostile, so the body is read through a
    /// length-limited stream that fails fast once this bound is crossed. This
    /// keeps a malicious or compromised endpoint from driving unbounded memory
    /// allocation during deserialization (<see cref="HttpClient.MaxResponseContentBufferSize"/>
    /// does not apply to streamed reads via <see cref="HttpCompletionOption.ResponseHeadersRead"/>
    /// + <c>JsonSerializer.DeserializeAsync</c>), and it is the real cap that bounds
    /// the per-buffer WKB limits in <see cref="EsriJsonWkbWriter"/>.
    /// </summary>
    internal const long MaxResponseContentBytes = 64L * 1024 * 1024;

    private readonly HttpClient _httpClient;

    public ArcGisRestFeatureClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public Task<ArcGisRestQueryResponse> QueryAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
        => GetAsync(url, authorizationHeader, ArcGisRestJsonContext.Default.ArcGisRestQueryResponse, cancellationToken);

    public Task<ArcGisRestCountResponse> QueryCountAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
        => GetAsync(url, authorizationHeader, ArcGisRestJsonContext.Default.ArcGisRestCountResponse, cancellationToken);

    public Task<ArcGisRestExtentResponse> QueryExtentAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
        => GetAsync(url, authorizationHeader, ArcGisRestJsonContext.Default.ArcGisRestExtentResponse, cancellationToken);

    public Task<ArcGisRestObjectIdsResponse> QueryObjectIdsAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
        => GetAsync(url, authorizationHeader, ArcGisRestJsonContext.Default.ArcGisRestObjectIdsResponse, cancellationToken);

    public Task<ArcGisRestLayerResponse> GetLayerMetadataAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
        => GetAsync(url, authorizationHeader, ArcGisRestJsonContext.Default.ArcGisRestLayerResponse, cancellationToken);

    private async Task<T> GetAsync<T>(
        string url,
        string? authorizationHeader,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (authorizationHeader is not null)
        {
            request.Headers.TryAddWithoutValidation(EsriAuthorizationHeader, authorizationHeader);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        // Defense against a hostile/compromised upstream: reject any body whose
        // advertised Content-Length already exceeds the cap before reading a byte,
        // then read through a length-limited stream so a missing or lying
        // Content-Length still cannot drive unbounded allocation during
        // deserialization. ResponseHeadersRead + streamed deserialization means
        // HttpClient.MaxResponseContentBufferSize does not apply here.
        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength is > MaxResponseContentBytes)
        {
            throw new InvalidOperationException(
                $"ArcGIS REST service response of {declaredLength} bytes exceeds the {MaxResponseContentBytes}-byte limit.");
        }

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var boundedStream = new LengthLimitedStream(contentStream, MaxResponseContentBytes);

        var result = await System.Text.Json.JsonSerializer.DeserializeAsync(boundedStream, typeInfo, cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException(
            "ArcGIS REST service returned an empty response body.");
    }

    /// <summary>
    /// Read-only wrapper that fails fast once more than <c>maxBytes</c> have been
    /// read from the underlying stream, bounding the memory a hostile upstream can
    /// force the JSON deserializer to allocate from a streamed response body.
    /// </summary>
    private sealed class LengthLimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _bytesRead;

        public LengthLimitedStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            Track(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            Track(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Track(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            Track(read);
            return read;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private void Track(int read)
        {
            if (read <= 0)
            {
                return;
            }

            _bytesRead += read;
            if (_bytesRead > _maxBytes)
            {
                throw new InvalidOperationException(
                    $"ArcGIS REST service response exceeds the {_maxBytes}-byte limit.");
            }
        }
    }
}

/// <summary>
/// Well-known DI client name registered through <c>AddHttpClient</c>. Lets host
/// applications attach handlers (resilience, retry, telemetry) without taking a
/// hard dependency on this provider's internals.
/// </summary>
internal static class ArcGisRestServiceClientName
{
    /// <summary>Logical HttpClient name for federated ArcGIS REST outbound calls.</summary>
    public const string Default = "Honua.ArcGisRest.Federation";
}
