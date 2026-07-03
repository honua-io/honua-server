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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _httpClientName;
    private readonly SentinelSinkOptions _options;

    /// <summary>
    /// Initializes a new Sentinel sink.
    /// </summary>
    /// <param name="httpClientFactory">
    /// Factory used to resolve a fresh named <see cref="HttpClient"/> per send — see
    /// <see cref="SplunkHecAuditSink"/> for why capturing a single client at construction
    /// would bypass <see cref="IHttpClientFactory"/> handler rotation.
    /// </param>
    /// <param name="httpClientName">The named client to resolve on each send.</param>
    /// <param name="options">Sentinel configuration.</param>
    public SentinelAuditSink(IHttpClientFactory httpClientFactory, string httpClientName, SentinelSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        ArgumentNullException.ThrowIfNull(options);
        _httpClientFactory = httpClientFactory;
        _httpClientName = httpClientName;
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
}
