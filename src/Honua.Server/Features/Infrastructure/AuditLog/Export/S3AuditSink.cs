// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net.Http.Headers;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.AuditLog.Export;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

internal sealed class S3AuditSink : IAuditSink
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _httpClientName;
    private readonly S3SinkOptions _options;

    public S3AuditSink(IHttpClientFactory httpClientFactory, string httpClientName, S3SinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        ArgumentNullException.ThrowIfNull(options);
        _httpClientFactory = httpClientFactory;
        _httpClientName = httpClientName;
        _options = options;
    }

    public string SinkType => "s3";

    public string? Region => _options.Region;

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

            using var response = await _httpClientFactory.CreateClient(_httpClientName).SendAsync(request, ct).ConfigureAwait(false);
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
