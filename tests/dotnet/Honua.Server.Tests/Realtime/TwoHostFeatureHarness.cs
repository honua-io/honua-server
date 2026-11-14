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
            ["FileStorage:AwsS3:Region"] = "us-west-2"
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            configuration[key] = value;
        }
        // Program chooses the realtime provider from builder.Configuration while it registers
        // services, before app-configuration overrides are applied, so the settings also go in as
        // host settings, the way HONUA_REGISTER_TEST_INFRASTRUCTURE reaches registration.
        return new WebAppFixture().UseKestrel()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                foreach (var (key, value) in configuration)
                {
                    builder.UseSetting(key, value);
                }
            })
            .OverrideConfiguration(configuration);
    }

    public static void EnableEditing(WebAppFixture task)
        => task.EnableV2ServiceEditingCapabilities("test", ["Query", "Create", "Update", "Delete"]);

    public static async Task WaitForRelayAsync(WebAppFixture task, CancellationToken cancellationToken)
    {
        var relay = task.Services.GetServices<IHostedService>().OfType<FeatureStreamRealtimeRelay>().Single();
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
        var result = document.RootElement.GetProperty("addResults")[0];
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

    /// <summary>
    /// Yields this run's feature-change frames until the stream ends. Other database tests share
    /// the operational log, so only committed images whose name starts with the marker count.
    /// </summary>
    public static async IAsyncEnumerable<FeatureFrame> ReadFramesAsync(
        StreamReader reader, string marker, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? eventName = null;
        string? id = null;
        string? data = null;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                eventName.Should().NotBe("reset", "the run stays within the retained feature window");
                if (eventName == "feature-change" && data is not null &&
                    TryReadMarkedFrame(data, marker, id) is { } frame)
                {
                    yield return frame;
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

    private static FeatureFrame? TryReadMarkedFrame(string data, string marker, string? id)
    {
        using var document = JsonDocument.Parse(data);
        var envelope = document.RootElement;
        if (envelope.GetProperty("serviceId").GetString() != "test" ||
            !envelope.TryGetProperty("attributes", out var attributes) ||
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
