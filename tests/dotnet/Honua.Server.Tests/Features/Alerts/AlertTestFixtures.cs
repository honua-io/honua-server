// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Honua.Core.Features.Alerts.Domain;
using Honua.Alerts;
using Honua.Alerts.Ops;

namespace Honua.Server.Tests.Features.Alerts;

/// <summary>
/// Shared test factory methods for alert delivery sink tests.
/// </summary>
internal static partial class AlertTestFixtures
{
    /// <summary>
    /// Base URL for delivery-sink tests whose expected outcome requires the outbound SSRF guard to
    /// admit the destination.
    /// <para>
    /// The IP literal is deliberate. Every webhook sink validates its destination through
    /// <c>OutboundHttpUrlValidator</c>, which resolves a <em>hostname</em> with a live
    /// <c>Dns.GetHostAddressesAsync</c> call and — by design — treats any <c>SocketException</c> as an
    /// unresolvable, therefore blocked, destination. A hostname here makes the test depend on CI DNS:
    /// a transient resolution failure turns a delivery that should succeed into a non-retryable
    /// failure and the test fails for reasons unrelated to the sink (#3056). An IP literal is vetted
    /// arithmetically with no DNS call, so the guard still runs but the test is hermetic. This matches
    /// the existing convention in <c>AlertOptionsValidatorTests</c> and <c>GeocodingOptionsValidatorTests</c>.
    /// </para>
    /// <para>
    /// Hostname resolution behaviour itself (private, unresolvable, empty, and public results) is
    /// covered directly by <c>OutboundHttpUrlValidatorTests</c> with an injected resolver, and the
    /// sink-level rejection path stays covered by
    /// <c>WebhookAlertDeliverySinkTests.DeliverAsync_WithUnsafeDestination_DoesNotSendRequest</c>,
    /// which uses a loopback host rejected before any DNS lookup.
    /// </para>
    /// </summary>
    public const string RoutableWebhookBaseUrl = "https://8.8.8.8";

    /// <summary>
    /// Host-name destination used by the destination-classification tests. It is never resolved by
    /// live DNS: those tests always supply an injected resolver through
    /// <see cref="GuardWithUnavailableResolver"/> or <see cref="GuardResolvingTo"/>.
    /// </summary>
    public const string HostnameWebhookBaseUrl = "https://alerts.example.test";

    /// <summary>
    /// Builds a destination guard whose resolver always fails the way a momentarily unavailable
    /// DNS resolver does, so a sink can be exercised against a transient resolution failure with
    /// no live DNS involved (#3057).
    /// </summary>
    public static AlertDestinationGuard GuardWithUnavailableResolver()
        => new((_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.TryAgain)));

    /// <summary>
    /// Builds a destination guard whose resolver deterministically returns the supplied addresses,
    /// so a sink can be exercised against a conclusively disallowed (or allowed) destination.
    /// </summary>
    public static AlertDestinationGuard GuardResolvingTo(params string[] addresses)
    {
        var resolved = addresses.Select(IPAddress.Parse).ToArray();
        return new AlertDestinationGuard((_, _) => Task.FromResult(resolved));
    }

    /// <summary>
    /// Builds an operations-notification envelope (source = ops) whose meaningful
    /// title/body/attributes live in the payload and whose rule/layer/object scalars
    /// are inert placeholders (#2427).
    /// </summary>
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

/// <summary>
/// Test HTTP handler that returns a fixed status code.
/// </summary>
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
