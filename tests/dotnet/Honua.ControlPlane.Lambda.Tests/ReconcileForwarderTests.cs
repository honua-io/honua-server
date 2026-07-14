// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using FluentAssertions;
using Honua.ControlPlane.Lambda;
using Xunit;

namespace Honua.ControlPlane.Lambda.Tests;

/// <summary>
/// Tests that the forwarder targets the in-process host's internal reconcile routes with the provider
/// id and shared-secret header, and no-ops when there is nothing to reconcile.
/// </summary>
public sealed class ReconcileForwarderTests
{
    [Fact]
    public async Task ReconcileBatchEventAsync_PostsProviderIdAndTokenHeader()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var forwarder = new ReconcileForwarder(
            httpClient,
            new ReconcileForwarderOptions { BaseAddress = "http://127.0.0.1:8080", EventToken = "secret-token" });

        await forwarder.ReconcileBatchEventAsync("batch-job-abc", CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.AbsolutePath.Should().Be("/internal/control-plane/events/batch-job-state-change");
        handler.LastRequest.RequestUri.Query.Should().Contain("providerOperationId=batch-job-abc");
        handler.LastRequest.Headers.GetValues("X-Honua-ControlPlane-Token").Should().ContainSingle()
            .Which.Should().Be("secret-token");
    }

    [Fact]
    public async Task ReconcileBatchEventAsync_BlankProviderId_DoesNotPost()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var forwarder = new ReconcileForwarder(
            httpClient,
            new ReconcileForwarderOptions { BaseAddress = "http://127.0.0.1:8080" });

        await forwarder.ReconcileBatchEventAsync(" ", CancellationToken.None);

        handler.LastRequest.Should().BeNull("a blank provider id resolves to no active job");
    }

    [Fact]
    public async Task RunBackstopAsync_PostsToBackstopRoute()
    {
        var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var forwarder = new ReconcileForwarder(
            httpClient,
            new ReconcileForwarderOptions { BaseAddress = "http://127.0.0.1:8080" });

        await forwarder.RunBackstopAsync(CancellationToken.None);

        handler.LastRequest!.RequestUri!.AbsolutePath.Should().Be("/internal/control-plane/backstop/sweep");
        handler.LastRequest.Headers.Contains("X-Honua-ControlPlane-Token").Should().BeFalse();
    }

    [Fact]
    public void FromEnvironment_UsesLwaPortAndToken()
    {
        var options = ReconcileForwarderOptions.FromEnvironment(new Dictionary<string, string?>
        {
            ["AWS_LWA_PORT"] = "9000",
            ["HONUA_CONTROL_PLANE_EVENT_TOKEN"] = "tok",
        });

        options.BaseAddress.Should().Be("http://127.0.0.1:9000");
        options.EventToken.Should().Be("tok");
    }

    [Fact]
    public void FromEnvironment_DefaultsPortTo8080()
    {
        var options = ReconcileForwarderOptions.FromEnvironment(new Dictionary<string, string?>());

        options.BaseAddress.Should().Be("http://127.0.0.1:8080");
        options.EventToken.Should().BeNull();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            // Ownership transfers to the HttpClient pipeline/caller, which disposes it after reading the response.
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
