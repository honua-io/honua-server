// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Honua.Server.Tests.Features.Spec;

/// <summary>
/// End-to-end coverage of the <c>/v1/spec/*</c> HTTP surface. Proves the SSE
/// event shape, plan structure, artifact retrieval, and error envelopes — the
/// operator-facing evidence for ticket #789's acceptance criteria.
/// </summary>
public sealed class SpecEndpointsTests
{
    [Fact]
    public async Task Plan_LinearChain_ReturnsDagWithContentHashesInTopologicalOrder()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();

        var document = BuildDocument(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")));

        using var response = await client.PostAsync("/v1/spec/plan", JsonContent(document));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = payload.RootElement;
        var nodes = root.GetProperty("nodes");
        Assert.Equal(2, nodes.GetArrayLength());
        Assert.Equal("a", nodes[0].GetProperty("nodeId").GetString());
        Assert.Equal("b", nodes[1].GetProperty("nodeId").GetString());

        var hashA = nodes[0].GetProperty("contentHash").GetString()!;
        var hashB = nodes[1].GetProperty("contentHash").GetString()!;
        Assert.Matches("^[0-9a-f]{64}$", hashA);
        Assert.Matches("^[0-9a-f]{64}$", hashB);
        Assert.NotEqual(hashA, hashB);

        Assert.Equal("a", nodes[1].GetProperty("dependsOn")[0].GetString());
    }

    [Fact]
    public async Task Plan_Cycle_Returns400WithDagCycleCode()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();

        var document = BuildDocument(
            ComputeNode("a", ("src", "@b")),
            ComputeNode("b", ("src", "@a")));

        using var response = await client.PostAsync("/v1/spec/plan", JsonContent(document));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("dag-cycle", payload.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Apply_WithoutEventStreamAccept_Returns400()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();

        var document = BuildDocument(ComputeNode("a"));
        using var response = await client.PostAsync("/v1/spec/apply", JsonContent(document));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("accept-required", payload.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Apply_LinearChain_StreamsSseEvents_AndSucceeds()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();

        var document = BuildDocument(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")));

        var events = await CollectSseEventsAsync(client, document);
        var kinds = events.Select(e => e.Kind).ToArray();

        Assert.Contains("ApplyStarted", kinds);
        Assert.Contains("ApplyCompleted", kinds);
        Assert.Equal(2, kinds.Count(k => k == "Succeeded"));

        var applyCompleted = events.Single(e => e.Kind == "ApplyCompleted");
        var summary = applyCompleted.Payload.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("totalNodes").GetInt32());
        Assert.Equal(2, summary.GetProperty("ranNodes").GetInt32());
        Assert.Equal(0, summary.GetProperty("cachedNodes").GetInt32());
        Assert.False(summary.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task Apply_RerunSameDocument_YieldsCachedEventsForEveryNode()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();

        var document = BuildDocument(
            ComputeNode("a"),
            ComputeNode("b", ("src", "@a")));

        var first = await CollectSseEventsAsync(client, document);
        var firstSummary = first.Single(e => e.Kind == "ApplyCompleted")
            .Payload.GetProperty("summary");
        Assert.Equal(2, firstSummary.GetProperty("ranNodes").GetInt32());

        var second = await CollectSseEventsAsync(client, document);
        var secondSummary = second.Single(e => e.Kind == "ApplyCompleted")
            .Payload.GetProperty("summary");
        Assert.Equal(0, secondSummary.GetProperty("ranNodes").GetInt32());
        Assert.Equal(2, secondSummary.GetProperty("cachedNodes").GetInt32());

        var cachedKinds = second.Where(e => e.Kind == "Cached").Select(e =>
            e.Payload.GetProperty("nodeId").GetString()).ToArray();
        Assert.Contains("a", cachedKinds);
        Assert.Contains("b", cachedKinds);
    }

    [Fact]
    public async Task Cancel_UnknownToken_Returns404WithApplyTokenUnknown()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/spec/cancel",
            new StringContent("{\"applyToken\":\"does-not-exist\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("apply-token-unknown", payload.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cancel_MissingToken_Returns400()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/spec/cancel",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("apply-token-missing", payload.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Artifact_UnknownHash_Returns404WithArtifactNotFound()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/spec/artifact/deadbeef");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("artifact-not-found", payload.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Artifact_AfterApply_ReturnsStoredBytes()
    {
        using var factory = new TestWebApplicationFactory();
        using var client = factory.CreateClient();

        var document = BuildDocument(ComputeNode("a"));
        var events = await CollectSseEventsAsync(client, document);

        var succeeded = events.Single(e => e.Kind == "Succeeded");
        var contentHash = succeeded.Payload.GetProperty("contentHash").GetString()!;
        Assert.Matches("^[0-9a-f]{64}$", contentHash);

        using var response = await client.GetAsync($"/v1/spec/artifact/{contentHash}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentHash, response.Headers.GetValues("X-Spec-Content-Hash").Single());
        var body = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrEmpty(body), "Artifact body should not be empty.");

        using var doc = JsonDocument.Parse(body);
        Assert.Equal("a", doc.RootElement.GetProperty("nodeId").GetString());
        Assert.Equal(contentHash, doc.RootElement.GetProperty("contentHash").GetString());
    }

    // ---- helpers --------------------------------------------------------

    private static StringContent JsonContent(object document) =>
        new(JsonSerializer.Serialize(document), Encoding.UTF8, "application/json");

    private static Dictionary<string, object?> ComputeNode(string id, params (string Key, string Value)[] inputs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in inputs)
        {
            map[k] = v;
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["kind"] = "Compute",
            ["op"] = "compute.noop",
            ["inputs"] = map
        };
    }

    private static Dictionary<string, object?> BuildDocument(params Dictionary<string, object?>[] nodes) =>
        new(StringComparer.Ordinal)
        {
            ["grammarVersion"] = "grammar/1.0",
            ["processFamilyVersion"] = "family/1.0",
            ["nodes"] = nodes
        };

    private static async Task<IReadOnlyList<SseFrame>> CollectSseEventsAsync(
        HttpClient client,
        object document,
        TimeSpan? timeout = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/spec/apply")
        {
            Content = JsonContent(document)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var frames = new List<SseFrame>();
        string? currentEvent = null;
        string? currentData = null;
        while (!cts.Token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cts.Token);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (currentEvent is not null && currentData is not null)
                {
                    var doc = JsonDocument.Parse(currentData);
                    frames.Add(new SseFrame(currentEvent, doc.RootElement.Clone()));
                    doc.Dispose();
                }

                currentEvent = null;
                currentData = null;
                continue;
            }

            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                currentEvent = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                currentData = line["data: ".Length..];
            }
        }

        return frames;
    }

    private readonly record struct SseFrame(string Kind, JsonElement Payload);
}
