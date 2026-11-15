// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Temporal.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Temporal;

public sealed partial class TemporalHistorySliceEndpointsTests
{
    private const string TimelineEndpoint = "GET /api/v1/temporal/services/{serviceId}/layers/{layerId}/features/{featureId}/timeline";

    [IntegrationTest]
    [Endpoint(TimelineEndpoint)]
    public async Task Timeline_ChangeBeyondCapturedGeneration_IsDeferred()
    {
        _changeTracker.CurrentGeneration = 5;
        SetTimelineRevisions(2, 4, 6);

        var response = await _client.GetAsync(TimelineUrl());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = (await ReadJsonAsync(response)).RootElement;
        root.GetProperty("currentGeneration").GetInt64().Should().Be(5);
        root.GetProperty("revisions").EnumerateArray()
            .Select(row => row.GetProperty("generation").GetInt64()).Should().Equal(2, 4);
        root.TryGetProperty("nextCursor", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint(TimelineEndpoint)]
    public async Task Timeline_CommitBetweenPages_PreservesOriginalBoundary()
    {
        _changeTracker.CurrentGeneration = 5;
        SetTimelineRevisions(2, 4);
        var firstResponse = await _client.GetAsync(TimelineUrl() + "?limit=1");
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var first = (await ReadJsonAsync(firstResponse)).RootElement;
        var cursor = first.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrEmpty();

        _changeTracker.CurrentGeneration = 8;
        SetTimelineRevisions(2, 4, 6, 8);
        var secondResponse = await _client.GetAsync(TimelineUrl() + "?limit=1&cursor=" + Uri.EscapeDataString(cursor!));

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var second = (await ReadJsonAsync(secondResponse)).RootElement;
        second.GetProperty("currentGeneration").GetInt64().Should().Be(5);
        second.GetProperty("revisions").EnumerateArray()
            .Select(row => row.GetProperty("generation").GetInt64()).Should().Equal(4);
        second.TryGetProperty("nextCursor", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint(TimelineEndpoint)]
    public async Task Timeline_LegacyCursor_CapturesCurrentBoundary()
    {
        _changeTracker.CurrentGeneration = 5;
        SetTimelineRevisions(2, 4, 6);

        var response = await _client.GetAsync(TimelineUrl() + "?limit=1&cursor=2");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = (await ReadJsonAsync(response)).RootElement;
        root.GetProperty("currentGeneration").GetInt64().Should().Be(5);
        root.GetProperty("revisions")[0].GetProperty("generation").GetInt64().Should().Be(4);
        root.TryGetProperty("nextCursor", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint(TimelineEndpoint)]
    public async Task Timeline_NewTraversal_IncludesNewlyCommittedRevisions()
    {
        _changeTracker.CurrentGeneration = 8;
        SetTimelineRevisions(2, 4, 6, 8);

        var response = await _client.GetAsync(TimelineUrl());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var root = (await ReadJsonAsync(response)).RootElement;
        root.GetProperty("currentGeneration").GetInt64().Should().Be(8);
        root.GetProperty("revisions").EnumerateArray()
            .Select(row => row.GetProperty("generation").GetInt64()).Should().Equal(2, 4, 6, 8);
    }

    [IntegrationTheory]
    [InlineData("v1:2:6")]
    [InlineData("v1:6:5")]
    [InlineData("v1:-1:5")]
    [InlineData("v1:2:-1")]
    [InlineData("v1:2:5:7")]
    [InlineData("v2:2:5")]
    [InlineData("not-a-cursor")]
    [Endpoint(TimelineEndpoint)]
    public async Task Timeline_InvalidCursor_ReturnsBadRequest(string cursor)
    {
        _changeTracker.CurrentGeneration = 5;

        var response = await _client.GetAsync(TimelineUrl() + "?cursor=" + Uri.EscapeDataString(cursor));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static string TimelineUrl()
        => $"/api/v1/temporal/services/{ServiceId}/layers/{TemporalLayerId}/features/100/timeline";

    private void SetTimelineRevisions(params long[] generations)
        => _store.FeatureRevisions = generations.Select(generation => new TemporalChangeRecord(
            generation, 100, TemporalChangeKind.Update, DateTimeOffset.UnixEpoch, null)).ToArray();
}
