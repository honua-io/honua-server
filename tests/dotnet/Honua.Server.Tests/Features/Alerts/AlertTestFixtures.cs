// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Honua.Core.Features.Alerts.Domain;
using Honua.Alerts;
using Honua.Alerts.Ops;

namespace Honua.Server.Tests.Features.Alerts;

internal static partial class AlertTestFixtures
{
    public const string RoutableWebhookBaseUrl = "https://8.8.8.8";

    public const string HostnameWebhookBaseUrl = "https://alerts.example.test";

    public static AlertDestinationGuard GuardWithUnavailableResolver()
        => new((_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.TryAgain)));

    public static AlertDestinationGuard GuardResolvingTo(params string[] addresses)
    {
        var resolved = addresses.Select(IPAddress.Parse).ToArray();
        return new AlertDestinationGuard((_, _) => Task.FromResult(resolved));
    }

    public static AlertEventEnvelope CreateOpsAlertEvent(
        AlertSeverity severity = AlertSeverity.Critical,
        string title = "Deploy prod-web failed",
        string body = "Deployment of prod-web to production failed during migration.",
        string source = "deploy-workflow",
        string operationId = "op-9f3c")
    {
        var payload = new OpsAlertPayload
        {
            Source = source,
            Severity = severity.ToString(),
            Title = title,
            Body = body,
            Attributes = new Dictionary<string, string>
            {
                ["operationId"] = operationId,
                ["status"] = "Failed"
            }
        };

        return new AlertEventEnvelope
        {
            DedupeKey = $"ops:{source}:{operationId}",
            RuleId = 0,
            ServiceId = source,
            LayerId = 0,
            ObjectId = 0,
            TriggerType = AlertTriggerType.Threshold,
            Generation = 0,
            Severity = severity,
            OccurredAt = DateTimeOffset.UtcNow,
            PayloadJson = JsonSerializer.Serialize(payload, OpsNotificationJsonContext.Default.OpsAlertPayload),
            IncidentStatus = AlertIncidentStatus.Started,
            Source = AlertEventSources.Ops
        };
    }
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;

    public FakeHttpMessageHandler(HttpStatusCode statusCode)
    {
        _statusCode = statusCode;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Ownership transfers to the HttpClient pipeline that invoked SendAsync;
        // it disposes the response after the caller finishes with it.
        return Task.FromResult<System.Net.Http.HttpResponseMessage>(new Honua.TestKit.CallerOwnedHttpResponseMessage(_statusCode));
    }
}
internal sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _statusCode;

    public CapturingHttpMessageHandler(HttpStatusCode statusCode)
    {
        _statusCode = statusCode;
    }

    public string? LastRequestBody { get; private set; }

    public Uri? LastRequestUri { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri;
        LastRequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);

        // Ownership transfers to the HttpClient pipeline that invoked SendAsync;
        // it disposes the response after the caller finishes with it.
        return new HttpResponseMessage(_statusCode);
    }
}
