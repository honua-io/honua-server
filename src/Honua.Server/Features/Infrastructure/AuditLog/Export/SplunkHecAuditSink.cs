// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Net.Http.Headers;
using System.Text.Json;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.AuditLog.Export;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

/// <summary>
/// Audit sink that forwards events to a Splunk HTTP Event Collector (HEC). Each
/// event is POSTed as a HEC envelope to <c>{Endpoint}/services/collector/event</c>
/// with an <c>Authorization: Splunk &lt;token&gt;</c> header (#2157).
/// </summary>
internal sealed class SplunkHecAuditSink : IAuditSink
{
    private const string Path = "/services/collector/event";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _httpClientName;
    private readonly SplunkHecSinkOptions _options;

    /// <summary>
    /// Initializes a new Splunk HEC sink.
    /// </summary>
    /// <param name="httpClientFactory">
    /// Factory used to resolve a fresh named <see cref="HttpClient"/> per send. A sink that
    /// captured a single <see cref="HttpClient"/> at construction (a singleton) would never
    /// observe <see cref="IHttpClientFactory"/>'s primary-handler rotation, so a DNS change
    /// or a broken pooled connection would wedge this sink for the life of the process.
    /// </param>
    /// <param name="httpClientName">The named client to resolve on each send.</param>
    /// <param name="options">Splunk HEC configuration.</param>
    public SplunkHecAuditSink(IHttpClientFactory httpClientFactory, string httpClientName, SplunkHecSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(httpClientName);
        ArgumentNullException.ThrowIfNull(options);
        _httpClientFactory = httpClientFactory;
        _httpClientName = httpClientName;
        _options = options;
    }

    /// <inheritdoc />
    public string SinkType => "splunk-hec";

    /// <inheritdoc />
    public string? Region => _options.Region;

    /// <inheritdoc />
    public async Task<AuditSinkResult> SendAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);

        var requestUri = BuildRequestUri();
        var httpClient = _httpClientFactory.CreateClient(_httpClientName);
        // Not a pure map: each iteration sends an HTTP request and can return early on the
        // first transport/HTTP failure, so this doesn't reduce cleanly to a '.Select(...)'.
        foreach (var evt in events)
        {
            var payload = BuildEnvelope(evt);
            try
            {
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = content };
                request.Headers.TryAddWithoutValidation("Authorization", $"Splunk {_options.Token}");

                using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
                var result = AuditHttpResultMapper.FromStatus(SinkType, response.StatusCode);
                if (!result.Succeeded)
                {
                    return result;
                }
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
                // Timeout (not caller cancellation) surfaces as TaskCanceledException.
                return AuditHttpResultMapper.FromTransportException(SinkType, ex);
            }
        }

        return AuditSinkResult.Success();
    }

    private Uri BuildRequestUri()
    {
        var baseAddress = _options.Endpoint.TrimEnd('/');
        return new Uri(baseAddress + Path, UriKind.Absolute);
    }

    private byte[] BuildEnvelope(AuditEvent evt)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sourcetype", _options.SourceType);
            if (!string.IsNullOrWhiteSpace(_options.Index))
            {
                writer.WriteString("index", _options.Index);
            }

            writer.WritePropertyName("event");
            AuditEventJsonSerializer.Write(writer, evt);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }
}
