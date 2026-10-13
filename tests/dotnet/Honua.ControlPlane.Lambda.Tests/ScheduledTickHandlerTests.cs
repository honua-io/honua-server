// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using FluentAssertions;
using Honua.ControlPlane.Lambda;
using Xunit;

namespace Honua.ControlPlane.Lambda.Tests;

/// <summary>
/// honua-iac sets <c>HONUA_CONTROL_PLANE_LAMBDA_HANDLER=scheduled-tick</c> on the tick function. The
/// bootstrap used to treat that value as <c>batch-event</c>, so no tick ever ran. These tests pin the
/// selector and the forwarded scheduled-tick request.
/// </summary>
public sealed class ScheduledTickHandlerTests
{
    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(null, Function.BatchEventHandler)]
    [InlineData("", Function.BatchEventHandler)]
    [InlineData("batch-event", Function.BatchEventHandler)]
    [InlineData("reconcile", Function.BatchEventHandler)]
    [InlineData("backstop", Function.BackstopHandler)]
    [InlineData("scheduled-tick", Function.ScheduledTickHandler)]
    [InlineData(" Scheduled-Tick ", Function.ScheduledTickHandler)]
    public void ResolveHandler_MapsEverySelectorIacSets(string? configured, string expected)
        => Function.ResolveHandler(configured).Should().Be(expected);

    [Trait("Tier", "Fast")]
    [Fact]
    public void ResolveHandler_UnknownSelector_IsRejectedRatherThanRunAsBatchEvent()
        => Function.ResolveHandler("sweep-everything").Should().BeNull();

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task RunScheduledTickAsync_PostsKindToScheduledTickRoute_WithTheServersTickToken()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = ReconcileForwarderOptions.FromEnvironment(new Dictionary<string, string?>
        {
            ["ControlPlane__ScheduledTickToken"] = "tick-token",
            ["HONUA_CONTROL_PLANE_EVENT_TOKEN"] = "event-token",
        });
        var forwarder = new ReconcileForwarder(httpClient, options);

        await Function.RunScheduledTickAsync(
            forwarder,
            new ScheduledTickEvent { Kind = "JobReconciliation" },
            CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString()
            .Should().Be("http://127.0.0.1:8080/internal/control-plane/scheduled-tick");
        handler.LastRequest.Headers.GetValues("X-Honua-Control-Plane-Token").Should().ContainSingle()
            .Which.Should().Be("tick-token");
        handler.LastRequest.Headers.Contains("X-Honua-ControlPlane-Token").Should().BeFalse();
        handler.LastBody.Should().Be("""{"kind":"JobReconciliation"}""");
        handler.LastContentType.Should().Be("application/json");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task RunScheduledTickAsync_WithoutKind_FailsTheInvocation_AndPostsNothing(string? kind)
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var forwarder = new ReconcileForwarder(
            httpClient,
            new ReconcileForwarderOptions { BaseAddress = "http://127.0.0.1:8080" });

        var act = () => Function.RunScheduledTickAsync(forwarder, new ScheduledTickEvent { Kind = kind }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.LastRequest.Should().BeNull();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task RunScheduledTickAsync_ServerRejection_FailsTheInvocation()
    {
        var handler = new RecordingHandler { StatusCode = HttpStatusCode.BadRequest };
        using var httpClient = new HttpClient(handler);
        var forwarder = new ReconcileForwarder(
            httpClient,
            new ReconcileForwarderOptions { BaseAddress = "http://127.0.0.1:8080" });

        var act = () => Function.RunScheduledTickAsync(forwarder, new ScheduledTickEvent { Kind = "NoSuchTick" }, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void FromEnvironment_EventToken_PrefersTheServersConfigurationKey()
    {
        var options = ReconcileForwarderOptions.FromEnvironment(new Dictionary<string, string?>
        {
            ["ControlPlane__EventToken"] = "config-token",
            ["HONUA_CONTROL_PLANE_EVENT_TOKEN"] = "env-token",
        });

        options.EventToken.Should().Be("config-token");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        public string? LastContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
                LastContentType = request.Content.Headers.ContentType?.MediaType;
            }

            return new HttpResponseMessage(StatusCode);
        }
    }
}
