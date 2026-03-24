// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Alerts;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class StreamingOperationsEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/streaming/subscribers")]
    public async Task ListSubscribers_WhenNone_ReturnsEmptyList()
    {
        var response = await _client.GetAsync("/api/v1/admin/operations/streaming/subscribers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        var data = root.GetProperty("data");
        data.GetProperty("subscriberCount").GetInt32().Should().Be(0);
        data.GetProperty("subscribers").GetArrayLength().Should().Be(0);
        data.TryGetProperty("generatedAt", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/streaming/alerts")]
    public async Task StreamAlerts_WithoutWebSocketUpgrade_Returns400()
    {
        var response = await _client.GetAsync("/api/v1/admin/operations/streaming/alerts");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/operations/streaming/subscribers")]
    public async Task ListSubscribers_AfterSubscribe_ReturnsSubscriberInfo()
    {
        var broadcaster = _fixture.GetService<IAlertNotificationBroadcaster>();
        using var sub = broadcaster.Subscribe(
            (_, _) => Task.CompletedTask,
            new SubscriptionOptions("test-client"));

        var response = await _client.GetAsync("/api/v1/admin/operations/streaming/subscribers");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("subscriberCount").GetInt32().Should().BeGreaterOrEqualTo(1);

        var subscribers = data.GetProperty("subscribers");
        subscribers.GetArrayLength().Should().BeGreaterOrEqualTo(1);

        var subscriber = subscribers[0];
        subscriber.TryGetProperty("subscriberId", out _).Should().BeTrue();
        subscriber.TryGetProperty("connectedAt", out _).Should().BeTrue();
        subscriber.GetProperty("clientLabel").GetString().Should().Be("test-client");
        subscriber.GetProperty("durationSeconds").GetDouble().Should().BeGreaterOrEqualTo(0);
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/operations/streaming/subscribers/{subscriberId}")]
    public async Task DisconnectSubscriber_ExistingId_ReturnsSuccess()
    {
        var broadcaster = _fixture.GetService<IAlertNotificationBroadcaster>();
        using var sub = broadcaster.Subscribe(
            (_, _) => Task.CompletedTask,
            new SubscriptionOptions("disconnect-test"));

        // Get the subscriber ID
        var listResponse = await _client.GetAsync("/api/v1/admin/operations/streaming/subscribers");
        using var listDoc = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var subscribers = listDoc.RootElement.GetProperty("data").GetProperty("subscribers");
        var subscriberId = subscribers.EnumerateArray()
            .First(s => s.GetProperty("clientLabel").GetString() == "disconnect-test")
            .GetProperty("subscriberId").GetString();

        // Disconnect
        var response = await _client.DeleteAsync(
            $"/api/v1/admin/operations/streaming/subscribers/{subscriberId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        // Verify removed
        var verifyResponse = await _client.GetAsync("/api/v1/admin/operations/streaming/subscribers");
        using var verifyDoc = JsonDocument.Parse(await verifyResponse.Content.ReadAsStringAsync());
        var remaining = verifyDoc.RootElement.GetProperty("data").GetProperty("subscribers");
        remaining.EnumerateArray()
            .Any(s => s.GetProperty("subscriberId").GetString() == subscriberId)
            .Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("DELETE /api/v1/admin/operations/streaming/subscribers/{subscriberId}")]
    public async Task DisconnectSubscriber_UnknownId_Returns404()
    {
        var unknownId = Guid.NewGuid();
        var response = await _client.DeleteAsync(
            $"/api/v1/admin/operations/streaming/subscribers/{unknownId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
