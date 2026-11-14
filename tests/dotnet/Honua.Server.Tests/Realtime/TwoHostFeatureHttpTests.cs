// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Realtime;
using Honua.Server.Features.Streaming;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

// Actual loopback HTTP connections to two independent application hosts on one database.
// This covers the feature HTTP path in specification section 12; it is not the separate
// process-replacement, all-family, scale or deployed-CDN qualification.
[Collection("Database")]
[Protocol(TestProtocols.Streaming)]
[Operation(Operations.Streaming)]
public sealed class TwoHostFeatureHttpTests : IAsyncLifetime
{
    private const int Edits = 200;
    private const string EditPath = "/rest/services/test/FeatureServer/0/applyEdits";
    private readonly string _marker = $"twohost-{Guid.NewGuid():N}";
    private readonly WebAppFixture _taskA = NewHost();
    private WebAppFixture _taskB = null!;
    private bool _taskBStopped;

    public async Task InitializeAsync()
    {
        await _taskA.InitializeAsync();
        _taskA.EnableV2ServiceEditingCapabilities("test", ["Query", "Create", "Update", "Delete"]);
        _taskB = NewHost().ShareSchemaWith(_taskA);
        await _taskB.InitializeAsync();
        _taskB.EnableV2ServiceEditingCapabilities("test", ["Query", "Create", "Update", "Delete"]);

        _taskA.Client.BaseAddress.Should().NotBe(_taskB.Client.BaseAddress);
        _taskA.CurrentSchema.Should().Be(_taskB.CurrentSchema);
        var providerA = _taskA.GetService<IRealtimeFanoutProvider>();
        var providerB = _taskB.GetService<IRealtimeFanoutProvider>();
        providerA.Should().NotBeSameAs(providerB);
        providerA.Capabilities.CrossNode.Should().BeTrue();
        providerB.Capabilities.CrossNode.Should().BeTrue();

        // Establish a nonzero head and let both production relays finish their initial
        // live-only positioning before measuring any subscriber delivery.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = _taskA.CreateAdminClient();
        await AddAsync(client, -1, timeout.Token);
        await WaitForRelayAsync(_taskA, timeout.Token);
        await WaitForRelayAsync(_taskB, timeout.Token);
    }

    public async Task DisposeAsync()
    {
        if (_taskB is not null && !_taskBStopped)
        {
            await _taskB.DisposeAsync();
        }
        await _taskA.DisposeAsync();
    }

    [IntegrationTest(Timeout = 240000)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task HttpEditsOnTaskA_ReachTaskBSse_OnceInOrderWithinTarget()
        => await VerifyDeliveryAsync(TimeSpan.FromMilliseconds(500));

    [IntegrationTest(Timeout = 240000)]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task DirectSqlEdits_ReachTaskBSse_OnceInOrderWithinTarget()
        => await VerifyDeliveryAsync(TimeSpan.FromMilliseconds(500),
            edit: (_, ordinal, cancellationToken) => AddSqlAsync(ordinal, cancellationToken));

    [IntegrationTest(Timeout = 240000)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task ListenerKilledDuringHttpEdits_TaskBSseLosesNothingAndListenerReconnects()
    {
        var listener = _taskB.GetService<RealtimeListener>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!listener.IsConnected)
        {
            await Task.Delay(20, timeout.Token);
        }
        var reconnects = listener.Reconnects;
        var processId = listener.ProcessId;
        processId.Should().BeGreaterThan(0);

        await VerifyDeliveryAsync(TimeSpan.FromMilliseconds(1500), async (ordinal, cancellationToken) =>
        {
            if (ordinal != Edits / 2)
            {
                return;
            }
            await using var connection = new NpgsqlConnection(_taskB.DatabaseConnectionProvider.GetConnectionString());
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", connection);
            command.Parameters.AddWithValue("pid", processId);
            (await command.ExecuteScalarAsync(cancellationToken)).Should().Be(true);
        });

        while (!listener.IsConnected || listener.Reconnects <= reconnects)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private async Task VerifyDeliveryAsync(
        TimeSpan latencyTarget, Func<int, CancellationToken, Task>? beforeEdit = null,
        Func<HttpClient, int, CancellationToken, Task<long>>? edit = null)
    {
        edit ??= AddAsync;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var writer = _taskA.CreateAdminClient();
        using var subscriber = _taskB.CreateAdminClient();
        using var response = await OpenStreamAsync(subscriber, null, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var reading = ReadChangesAsync(reader, Edits, timeout.Token);
        var committed = new Dictionary<long, long>();
        for (var i = 0; i < Edits; i++)
        {
            if (beforeEdit is not null)
            {
                await beforeEdit(i, timeout.Token);
            }
            var startedAt = Stopwatch.GetTimestamp();
            committed.Add(await edit(writer, i, timeout.Token), startedAt);
        }

        var frames = await reading;
        AssertCompleteOrderedDelivery(frames, committed.Keys);
        // Measure from before the HTTP edit, so this includes request and commit time
        // rather than understating latency when SSE beats the edit response back.
        var latencies = frames.Select(frame => Stopwatch.GetElapsedTime(committed[frame.ObjectId], frame.ReceivedAt))
            .Order().ToArray();
        latencies[(int)Math.Ceiling(Edits * 0.95) - 1].Should().BeLessThanOrEqualTo(latencyTarget);
    }

    [IntegrationTest(Timeout = 240000)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task AfterTaskBStops_LastEventIdOnTaskA_ReplaysEveryRemainingHttpEdit()
    {
        const int beforeStop = 50;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var writer = _taskA.CreateAdminClient();
        var committed = new List<long>();
        IReadOnlyList<Frame> before;
        using (var subscriber = _taskB.CreateAdminClient())
        using (var response = await OpenStreamAsync(subscriber, null, timeout.Token))
        using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token)))
        {
            var reading = ReadChangesAsync(reader, beforeStop, timeout.Token);
            for (var i = 0; i < beforeStop; i++)
            {
                committed.Add(await AddAsync(writer, i, timeout.Token));
            }
            before = await reading;
        }

        await _taskB.DisposeAsync();
        _taskBStopped = true;
        for (var i = beforeStop; i < Edits; i++)
        {
            committed.Add(await AddAsync(writer, i, timeout.Token));
        }

        using var resumed = _taskA.CreateAdminClient();
        using var replay = await OpenStreamAsync(resumed, before[^1].Id, timeout.Token);
        using var replayReader = new StreamReader(await replay.Content.ReadAsStreamAsync(timeout.Token));
        var after = await ReadChangesAsync(replayReader, Edits - beforeStop, timeout.Token);
        AssertCompleteOrderedDelivery(before.Concat(after).ToArray(), committed);
    }

    private static WebAppFixture NewHost() => new WebAppFixture().UseKestrel()
        .WithTestLicense(HonuaEdition.Pro)
        .OverrideConfiguration(new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = "MultiNode",
            ["Realtime:Provider"] = "auto"
        });

    private static async Task WaitForRelayAsync(WebAppFixture task, CancellationToken cancellationToken)
    {
        var relay = task.Services.GetServices<IHostedService>().OfType<FeatureStreamRealtimeRelay>().Single();
        while (relay.Position == 0)
        {
            await Task.Delay(20, cancellationToken);
        }
    }

    private async Task<long> AddAsync(HttpClient client, int ordinal, CancellationToken cancellationToken)
    {
        var name = ordinal < 0 ? $"warmup-{_marker}" : $"{_marker}-{ordinal}";
        var json = $$$"""{"adds":[{"attributes":{"name":"{{{name}}}"},"geometry":{"x":-122.4194,"y":37.7749}}]}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(EditPath, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var result = document.RootElement.GetProperty("addResults")[0];
        result.GetProperty("success").GetBoolean().Should().BeTrue(body);
        return result.GetProperty("objectId").GetInt64();
    }

    // The shared fixture helper writes SQL in the isolated schema, with no HTTP request
    // or local publish. The database trigger is the only source of this event.
    private Task<long> AddSqlAsync(int ordinal, CancellationToken cancellationToken)
        => _taskA.InsertFeatureAsync(0, $"{_marker}-{ordinal}", cancellationToken);

    private static async Task<HttpResponseMessage> OpenStreamAsync(
        HttpClient client, string? lastEventId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/streaming/features?layers=0");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (lastEventId is not null)
        {
            request.Headers.Add("Last-Event-ID", lastEventId);
        }
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        return response;
    }

    private async Task<IReadOnlyList<Frame>> ReadChangesAsync(
        StreamReader reader, int count, CancellationToken cancellationToken)
    {
        var frames = new List<Frame>();
        string? eventName = null;
        string? id = null;
        string? data = null;
        while (frames.Count < count)
        {
            var line = await reader.ReadLineAsync(cancellationToken)
                ?? throw new EndOfStreamException("The feature SSE stream ended before all committed edits arrived.");
            if (line.Length == 0)
            {
                eventName.Should().NotBe("reset", "the test stays within the retained feature window");
                if (eventName == "feature-change" && data is not null)
                {
                    using var document = JsonDocument.Parse(data);
                    var envelope = document.RootElement;
                    // Other database tests share the operational log, so match our unique
                    // committed feature image rather than accepting an unrelated layer-0 edit.
                    if (envelope.GetProperty("serviceId").GetString() == "test" &&
                        envelope.TryGetProperty("attributes", out var attributes) &&
                        attributes.ValueKind == JsonValueKind.Object &&
                        attributes.TryGetProperty("name", out var name) &&
                        name.ValueKind == JsonValueKind.String &&
                        name.GetString()?.StartsWith(_marker, StringComparison.Ordinal) == true)
                    {
                        var generation = envelope.GetProperty("cursor").GetInt64();
                        id.Should().Be(FeatureStreamCursor.Format(generation));
                        frames.Add(new Frame(envelope.GetProperty("objectId").GetInt64(), generation, id!, Stopwatch.GetTimestamp()));
                    }
                }
                eventName = id = data = null;
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line[7..];
            }
            else if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = line[4..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line[6..];
            }
        }
        return frames;
    }

    private static void AssertCompleteOrderedDelivery(IReadOnlyList<Frame> frames, IEnumerable<long> committed)
    {
        frames.Select(frame => frame.ObjectId).Should().Equal(committed, "every edit must arrive once in commit order");
        frames.Select(frame => frame.Generation).Should().OnlyHaveUniqueItems().And.BeInAscendingOrder();
    }

    private sealed record Frame(long ObjectId, long Generation, string Id, long ReceivedAt);
}
