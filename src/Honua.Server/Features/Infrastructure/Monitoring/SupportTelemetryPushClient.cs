// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal sealed partial class SupportTelemetryPushClient(
    IHttpClientFactory httpClientFactory,
    SupportTelemetrySnapshotCollector collector,
    IOptionsMonitor<SupportTelemetryPushOptions> optionsMonitor,
    ILogger<SupportTelemetryPushClient> logger)
{
    public const string HttpClientName = "HonuaSupportTelemetry";

    public Task<bool> PushIngestAsync(CancellationToken cancellationToken) =>
        PushAsync(ticketId: null, cancellationToken);

    public async Task<bool> PushAsync(string? ticketId, CancellationToken cancellationToken)
    {
        var options = optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            return false;
        }

        SupportTelemetryPushOptions.ValidateEnabled(options);

        var snapshot = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);
        var baseUrl = options.EndpointUrl!.Trim().TrimEnd('/');
        var path = string.IsNullOrWhiteSpace(ticketId)
            ? "/api/v1/telemetry/ingest"
            : $"/api/v1/tickets/{Uri.EscapeDataString(ticketId.Trim())}/telemetry-snapshot";
        var targetUri = new Uri($"{baseUrl}{path}", UriKind.Absolute);

        var json = JsonSerializer.Serialize(
            snapshot,
            SupportTelemetryJsonContext.Default.SupportTelemetrySnapshotPayload);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(options.EffectiveRequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, targetUri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey!.Trim());

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, cts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            PushRejected(logger, targetUri.Host, (int)response.StatusCode);
            return false;
        }

        PushSucceeded(logger, targetUri.Host, snapshot.InstanceId ?? "unknown");
        return true;
    }

    [LoggerMessage(EventId = 8650, Level = LogLevel.Debug, Message = "Support telemetry snapshot delivered to {SupportHost} for instance {InstanceId}.")]
    private static partial void PushSucceeded(ILogger logger, string supportHost, string instanceId);

    [LoggerMessage(EventId = 8651, Level = LogLevel.Warning, Message = "Support telemetry snapshot rejected by {SupportHost} with HTTP {StatusCode}.")]
    private static partial void PushRejected(ILogger logger, string supportHost, int statusCode);
}
