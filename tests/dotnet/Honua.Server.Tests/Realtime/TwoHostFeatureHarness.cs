// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Streaming;
using Honua.TestKit;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Realtime;

/// <summary>
/// Shared pieces of the two-host feature acceptance and scale runs: real Kestrel hosts on one
/// schema, HTTP edits and a feature SSE reader that keeps only this run's marked edits.
/// </summary>
internal static class TwoHostFeatureHarness
{
    private const string EditPath = "/rest/services/test/FeatureServer/0/applyEdits";

    public static WebAppFixture NewHost(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Deployment:Mode"] = "MultiNode",
            ["Realtime:Provider"] = "auto",
            // Multi-node startup refuses local file storage. These edits carry no attachments,
            // so the object store is configured but never reached (as in the multi-node startup tests).
            ["FileStorage:Provider"] = "AwsS3",
            ["FileStorage:AwsS3:BucketName"] = "honua-two-host-realtime",
            ["FileStorage:AwsS3:Region"] = "us-west-2",
            // One admin key writes every edit across the whole run, and limiter state can outlive
            // a host; keep the limiter on but sized above the offered rate.
            ["RateLimiting:GlobalRequestsPerMinute"] = "100000"
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            configuration[key] = value;
        }
        // Program chooses the realtime provider from builder.Configuration while it registers
        // services, before app-configuration overrides are applied, so the settings also go in as
        // host settings, the way HONUA_REGISTER_TEST_INFRASTRUCTURE reaches registration.
        var host = new WebAppFixture();
        return host.UseKestrel()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                foreach (var (key, value) in configuration)
                {
                    builder.UseSetting(key, value);
                }
                // The realtime log and feature-change source read the system schema. In production
                // the change trigger writes there; here migrations run in the fixture schema, so the
                // system schema must be that schema or each task follows an unrelated table. The
                // fixture chooses (or shares) its schema before it builds the host.
                builder.UseSetting("Database:Schema", host.CurrentSchema);
            })
            .OverrideConfiguration(configuration);
    }

    public static void EnableEditing(WebAppFixture task)
        => task.EnableV2ServiceEditingCapabilities("test", ["Query", "Create", "Update", "Delete"]);

    public static FeatureStreamRealtimeRelay Relay(WebAppFixture task)
        => task.Services.GetServices<IHostedService>().OfType<FeatureStreamRealtimeRelay>().Single();

    public static async Task WaitForRelayAsync(WebAppFixture task, CancellationToken cancellationToken)
    {
        var relay = Relay(task);
        while (relay.Position == 0)
        {
            await Task.Delay(20, cancellationToken);
        }
    }

    public static async Task<long> AddAsync(HttpClient client, string name, CancellationToken cancellationToken)
    {
        var json = $$$"""{"adds":[{"attributes":{"name":"{{{name}}}"},"geometry":{"x":-122.4194,"y":37.7749}}]}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(EditPath, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("addResults", out var results).Should().BeTrue(body);
        var result = results[0];
        result.GetProperty("success").GetBoolean().Should().BeTrue(body);
        return result.GetProperty("objectId").GetInt64();
    }

    public static async Task<HttpResponseMessage> OpenStreamAsync(
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
}

/// <summary>
/// Reads this run's feature-change frames from one SSE stream until it ends. Other database
/// tests share the operational log, so only committed images whose name starts with the marker
/// count. Every event is tallied so a stalled read can say what the stream did deliver.
/// </summary>
internal sealed class MarkedFeatureStream(string marker)
{
    private readonly Dictionary<string, int> _events = new(StringComparer.Ordinal);
    private string? _lastUnmatched;

    public string Describe()
        => $"events [{string.Join(", ", _events.Select(pair => $"{pair.Key}={pair.Value}"))}], " +
           $"last unmatched data: {_lastUnmatched ?? "none"}";

    public async IAsyncEnumerable<FeatureFrame> ReadAsync(
        StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? eventName = null;
        string? id = null;
        string? data = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                eventName.Should().NotBe("reset", "the run stays within the retained feature window");
                var name = eventName ?? "message";
                _events[name] = _events.GetValueOrDefault(name) + 1;
                if (eventName == "feature-change" && data is not null)
                {
                    if (TryReadMarkedFrame(data, id) is { } frame)
                    {
                        yield return frame;
                    }
                    else
                    {
                        _lastUnmatched = data.Length > 400 ? data[..400] : data;
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
    }

    private FeatureFrame? TryReadMarkedFrame(string data, string? id)
    {
        using var document = JsonDocument.Parse(data);
        var envelope = document.RootElement;
        // The envelope carries the internal service id, not the route name; the per-run GUID
        // marker on the committed image is what identifies this run's edits.
        if (!envelope.TryGetProperty("attributes", out var attributes) ||
            attributes.ValueKind != JsonValueKind.Object ||
            !attributes.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String ||
            nameElement.GetString() is not { } name ||
            !name.StartsWith(marker, StringComparison.Ordinal))
        {
            return null;
        }
        var generation = envelope.GetProperty("cursor").GetInt64();
        id.Should().Be(FeatureStreamCursor.Format(generation));
        return new FeatureFrame(envelope.GetProperty("objectId").GetInt64(), generation, id!, name)
        {
            ReceivedAt = Stopwatch.GetTimestamp()
        };
    }
}

/// <summary>One marked feature-change frame as a subscriber received it.</summary>
internal sealed record FeatureFrame(long ObjectId, long Generation, string Id, string Name)
{
    public long ReceivedAt { get; init; }
}
