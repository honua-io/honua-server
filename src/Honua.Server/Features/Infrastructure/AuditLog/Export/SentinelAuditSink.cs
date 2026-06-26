// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Headers;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.AuditLog.Export;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

/// <summary>
/// Audit sink that forwards events to a Microsoft Sentinel / Log Analytics Data
/// Collector (or Logs Ingestion) endpoint as a JSON array (#2157). The batch is
/// POSTed with the configured <c>Authorization</c> header value and a
/// <c>Log-Type</c> header naming the custom table.
/// </summary>
internal sealed class SentinelAuditSink : IAuditSink
{
    private readonly HttpClient _httpClient;
    private readonly SentinelSinkOptions _options;

    /// <summary>
    /// Initializes a new Sentinel sink.
    /// </summary>
    /// <param name="httpClient">The HTTP client (typically named, from <see cref="IHttpClientFactory"/>).</param>
    /// <param name="options">Sentinel configuration.</param>
    public SentinelAuditSink(HttpClient httpClient, SentinelSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        _httpClient = httpClient;
        _options = options;
    }

    /// <inheritdoc />
    public string SinkType => "sentinel";

    /// <inheritdoc />
    public string? Region => _options.Region;

    /// <inheritdoc />
    public async Task<AuditSinkResult> SendAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);

        var payload = AuditEventJsonSerializer.SerializeArray(events);
        try
        {
            using var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint) { Content = content };
            request.Headers.TryAddWithoutValidation("Authorization", _options.AuthHeaderValue);
            request.Headers.TryAddWithoutValidation("Log-Type", _options.LogType);

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
}
