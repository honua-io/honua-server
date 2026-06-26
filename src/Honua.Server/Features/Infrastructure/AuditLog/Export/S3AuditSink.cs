// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net.Http.Headers;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.AuditLog.Export;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

/// <summary>
/// Audit sink that PUTs a newline-delimited JSON object to an S3-compatible REST
/// endpoint at <c>{Endpoint}/{Bucket}/{KeyPrefix}{yyyy/MM/dd}/{guid}.jsonl</c>
/// (#2157).
/// </summary>
/// <remarks>
/// Request signing (AWS SigV4) is intentionally not implemented to keep the sink
/// dependency-free. When <see cref="S3SinkOptions.AuthHeaderValue"/> is supplied
/// it is sent verbatim as the <c>Authorization</c> header; otherwise signing is
/// expected to be delegated to a sidecar/proxy or to a pre-signed endpoint.
/// </remarks>
internal sealed class S3AuditSink : IAuditSink
{
    private readonly HttpClient _httpClient;
    private readonly S3SinkOptions _options;

    /// <summary>
    /// Initializes a new S3 sink.
    /// </summary>
    /// <param name="httpClient">The HTTP client (typically named, from <see cref="IHttpClientFactory"/>).</param>
    /// <param name="options">S3 configuration.</param>
    public S3AuditSink(HttpClient httpClient, S3SinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _options = options;
    }

    /// <inheritdoc />
    public string SinkType => "s3";

    /// <inheritdoc />
    public string? Region => _options.Region;

    /// <inheritdoc />
    public async Task<AuditSinkResult> SendAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);

        var requestUri = BuildObjectUri(DateTimeOffset.UtcNow);
        var payload = AuditEventJsonSerializer.SerializeNdjson(events);
        try
        {
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            using var request = new HttpRequestMessage(HttpMethod.Put, requestUri) { Content = content };
            if (!string.IsNullOrWhiteSpace(_options.AuthHeaderValue))
            {
                request.Headers.TryAddWithoutValidation("Authorization", _options.AuthHeaderValue);
            }

            using var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            return AuditHttpResultMapper.FromStatus(SinkType, response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            return AuditHttpResultMapper.FromTransportException(SinkType, ex);
        }
        catch (TaskCanceledException ex)
        {
            return AuditHttpResultMapper.FromTransportException(SinkType, ex);
        }
    }

    private Uri BuildObjectUri(DateTimeOffset now)
    {
        var endpoint = _options.Endpoint.TrimEnd('/');
        var bucket = _options.Bucket.Trim('/');
        var prefix = _options.KeyPrefix.TrimStart('/');
        var datePart = now.UtcDateTime.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        var key = $"{prefix}{datePart}/{Guid.NewGuid():N}.jsonl";
        return new Uri($"{endpoint}/{bucket}/{key}", UriKind.Absolute);
    }
}
