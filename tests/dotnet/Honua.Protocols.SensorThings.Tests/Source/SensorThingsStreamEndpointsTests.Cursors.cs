// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

public sealed partial class SensorThingsStreamEndpointsTests
{
    [IntegrationTheory]
    [InlineData("cursor=")]
    [InlineData("cursor=features:1")]
    [InlineData("cursor=observations:-1")]
    [InlineData("cursor=observations:9223372036854775808")]
    [InlineData("cursor=observations:%201")]
    [InlineData("datastreamId=0")]
    [InlineData("datastreamId=-1")]
    [InlineData("datastreamId=invalid")]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_InvalidResumeOrDatastream_Returns400(string query)
    {
        using var client = _fixture.CreateAdminClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sta/v1.1/ObservationsStream?" + query);
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_ConflictingResumeSources_Returns400()
    {
        using var client = _fixture.CreateAdminClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/sta/v1.1/ObservationsStream?cursor=observations:0");
        request.Headers.Accept.ParseAdd("text/event-stream");
        request.Headers.Add("Last-Event-ID", "observations:1");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Streaming)]
    [Endpoint("GET /sta/v1.1/ObservationsStream")]
    public async Task ObservationsStream_Resume_ReplaysCommittedBatchAfterCursor(bool useHeader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var firstConnection = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        using var client = _fixture.CreateAdminClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sta/v1.1/ObservationsStream?datastreamId=1");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, firstConnection.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var stream = await response.Content.ReadAsStreamAsync(firstConnection.Token);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await ReadUntilAsync(reader, "event: status", firstConnection.Token);
        using var first = await client.PostAsJsonAsync("/sta/v1.1/Datastreams(1)/Observations",
            new { result = 17 }, timeout.Token);
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstBatch = await ReadUntilAsync(reader, "event: observations", firstConnection.Token);
        var cursor = firstBatch.Split('\n')[0];
        cursor.Should().StartWith("id: observations:");
        cursor = cursor[4..];
        firstConnection.Cancel();

        using var second = await client.PostAsJsonAsync("/sta/v1.1/Datastreams(1)/Observations",
            new { result = 28 }, timeout.Token);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        var path = "/sta/v1.1/ObservationsStream?datastreamId=1";
        if (!useHeader) path += "&cursor=" + Uri.EscapeDataString(cursor);
        using var resumeRequest = new HttpRequestMessage(HttpMethod.Get, path);
        resumeRequest.Headers.Accept.ParseAdd("text/event-stream");
        if (useHeader) resumeRequest.Headers.Add("Last-Event-ID", cursor);
        using var resumed = await client.SendAsync(resumeRequest, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        resumed.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var resumedStream = await resumed.Content.ReadAsStreamAsync(timeout.Token);
        using var resumedReader = new StreamReader(resumedStream, Encoding.UTF8);
        var replay = await ReadUntilAsync(resumedReader, "event: observations", timeout.Token);
        using var batch = JsonDocument.Parse(replay[(replay.IndexOf("data: ", StringComparison.Ordinal) + 6)..]);
        batch.RootElement.GetProperty("observations")[0].GetProperty("result").GetInt32().Should().Be(28);
        batch.RootElement.GetProperty("cursor").GetString().Should().NotBe(cursor);
        batch.RootElement.TryGetProperty("scope", out _).Should().BeFalse();
    }
}
