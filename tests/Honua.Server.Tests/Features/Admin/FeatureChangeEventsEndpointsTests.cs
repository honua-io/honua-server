// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Events;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.OperationsProgress)]
public sealed class FeatureChangeEventsEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;
    private IFeatureChangeEventStore _store = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
        _store = _fixture.GetService<IFeatureChangeEventStore>();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/feature-events/replay")]
    public async Task Replay_WithCursorLimit_ReturnsOrderedEventsAndCursor()
    {
        var first = await _store.AppendAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-a",
            LayerId = 7,
            ObjectId = 1001,
            Operation = "create",
            Protocol = "FeatureServer",
            RequestId = "req-1"
        });

        var second = await _store.AppendAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-a",
            LayerId = 7,
            ObjectId = 1002,
            Operation = "update",
            Protocol = "FeatureServer",
            RequestId = "req-2"
        });

        var third = await _store.AppendAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-a",
            LayerId = 7,
            ObjectId = 1003,
            Operation = "delete",
            Protocol = "FeatureServer",
            RequestId = "req-3"
        });

        var response = await _client.GetAsync($"/api/v1/admin/feature-events/replay?cursor={first.Cursor}&limit=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var events = GetPropertyCaseInsensitive(json.RootElement, "events");
        events.GetArrayLength().Should().Be(2);

        var firstReturned = events[0];
        var secondReturned = events[1];
        GetPropertyCaseInsensitive(firstReturned, "cursor").GetInt64().Should().Be(second.Cursor);
        GetPropertyCaseInsensitive(secondReturned, "cursor").GetInt64().Should().Be(third.Cursor);

        var eventIdOne = GetPropertyCaseInsensitive(firstReturned, "eventId").GetString();
        var eventIdTwo = GetPropertyCaseInsensitive(secondReturned, "eventId").GetString();
        eventIdOne.Should().NotBeNullOrWhiteSpace();
        eventIdTwo.Should().NotBeNullOrWhiteSpace();
        eventIdOne.Should().NotBe(eventIdTwo, "eventId is used by consumers as idempotency key");

        GetPropertyCaseInsensitive(json.RootElement, "nextCursor").GetInt64().Should().Be(third.Cursor);
    }

    [IntegrationTest]
    public async Task Replay_WithTimeWindow_FiltersEvents()
    {
        var now = DateTimeOffset.UtcNow;
        _ = await _store.AppendAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-window",
            LayerId = 2,
            ObjectId = 1,
            Operation = "create",
            Protocol = "OData",
            RequestId = "window-old",
            Timestamp = now.AddHours(-2)
        });

        var inWindow = await _store.AppendAsync(new FeatureChangeEventRequest
        {
            ServiceId = "svc-window",
            LayerId = 2,
            ObjectId = 2,
            Operation = "update",
            Protocol = "OData",
            RequestId = "window-new",
            Timestamp = now
        });

        var from = Uri.EscapeDataString(now.AddMinutes(-30).ToString("O"));
        var to = Uri.EscapeDataString(now.AddMinutes(30).ToString("O"));
        var response = await _client.GetAsync($"/api/v1/admin/feature-events/replay?from={from}&to={to}&limit=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var events = GetPropertyCaseInsensitive(json.RootElement, "events");
        events.EnumerateArray()
            .Select(e => GetPropertyCaseInsensitive(e, "cursor").GetInt64())
            .Should()
            .Contain(inWindow.Cursor);
    }

    private static JsonElement GetPropertyCaseInsensitive(JsonElement element, string propertyName)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new KeyNotFoundException($"Property '{propertyName}' was not found.");
    }
}

