// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Realtime;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Admin;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.Streaming)]
public sealed class AdminLiveEndpointTests
{
    private const string AdminPassword = "admin-live-test-password";

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/live")]
    public async Task GetLive_Anonymous_ReturnsUnauthorized()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/v1/admin/live");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/live")]
    public async Task GetLive_UnknownTopic_ReturnsBadRequest()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        using var response = await client.GetAsync("/api/v1/admin/live?topics=secrets");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTheory]
    [InlineData("features:1")]
    [InlineData("admin:-1")]
    [InlineData("admin:9223372036854775808")]
    [InlineData("admin:abc")]
    [Endpoint("GET /api/v1/admin/live")]
    public async Task GetLive_InvalidCursor_ReturnsBadRequest(string cursor)
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        client.DefaultRequestHeaders.Add("Last-Event-ID", cursor);
        using var response = await client.GetAsync("/api/v1/admin/live?topics=proposals");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/live")]
    public async Task GetLive_FullSubscriptionCloses_EmitsSlowConsumerReset()
    {
        var provider = Substitute.For<IRealtimeFanoutProvider>();
        var subscription = Substitute.For<IRealtimeSubscription>();
        subscription.Position.Returns(256L);
        provider.GetWindowAsync(RealtimeFamily.Admin, Arg.Any<CancellationToken>())
            .Returns(new RealtimeLogWindow(RealtimeFamily.Admin, 0, 0));
        provider.Subscribe(Arg.Any<RealtimeSubscriptionRequest>()).Returns(call =>
        {
            var writer = call.Arg<RealtimeSubscriptionRequest>().Writer;
            var queued = 0;
            while (writer.TryWrite(RealtimeDelivery.Heartbeat.Instance)) { queued++; }
            queued.Should().Be(256);
            writer.TryComplete(new RealtimeSubscriptionClosedException(RealtimeResetReasons.SlowConsumer));
            return subscription;
        });
        using var factory = CreateFactory(provider);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);

        using var response = await client.GetAsync("/api/v1/admin/live?topics=proposals");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("event: status\n");
        body.Should().EndWith("event: reset\ndata: {\"reason\":\"slow-consumer\",\"head\":256}\n\n");
    }

    private static WebApplicationFactory<Program> CreateFactory(IRealtimeFanoutProvider? provider = null)
        => ConfiguredWebApplicationFactory.Create(builder =>
        {
            TestWebApplicationFactory.ConfigureForTests(builder);
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["HONUA_ADMIN_PASSWORD"] = AdminPassword }));
            if (provider is not null)
            {
                builder.ConfigureTestServices(services =>
                    services.Replace(ServiceDescriptor.Singleton(provider)));
            }
        });
}
