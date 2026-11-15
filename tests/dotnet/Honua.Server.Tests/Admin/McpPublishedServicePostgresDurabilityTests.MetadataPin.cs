// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Admin;

public sealed partial class McpPublishedServicePostgresDurabilityTests
{
    // An agent's publication proposal is pinned to the metadata base it saw. When that base changes
    // before a person approves, the approved queued publication is refused and creates no layer.
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp")]
    public async Task ApprovedPublish_AfterTheMetadataBaseChanged_IsRefusedWithoutALayer()
    {
        var first = await PublishAsync(_firstTable, "first layer", "Succeeded");
        first.GetProperty("status").GetString().Should().Be("Completed", first.GetRawText());
        _publishedLayerIds.Add(int.Parse(
            first.GetProperty("result").GetProperty("details").GetProperty("storageLayerId").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture));
        var proposalId = await ProposeAsync(_secondTable, "second layer");

        using var change = await _client.PutAsJsonAsync(
            $"/api/v1/admin/services/{_serviceName}/access-policy", new { allowAnonymous = false });
        change.IsSuccessStatusCode.Should().BeTrue(await change.Content.ReadAsStringAsync());

        var approved = await HumanApprovedOperation.ApproveAndReadHandleAsync(_fixture, proposalId, "Submitted");
        var jobId = approved.GetProperty("jobId").GetString();
        jobId.Should().NotBeNullOrWhiteSpace(approved.GetRawText());
        (await AwaitJobFailureAsync(jobId!)).Should().Contain("metadata", "the worker refuses the stale base");
        (await PublishedLayerCountAsync(_secondTable)).Should().Be(0);
    }

    private async Task<string> ProposeAsync(string table, string layerName)
    {
        using var response = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "honua_publish_service",
                arguments = new
                {
                    connectionId = _connectionId.ToString("D"),
                    schema = "public",
                    table,
                    layerName,
                    serviceName = _serviceName,
                    geometryColumn = "geom",
                    geometryType = "Point",
                    srid = 4326,
                    primaryKey = "id",
                    fields = new[] { "id", "name" },
                },
            },
        });
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var proposal = document.RootElement.GetProperty("result").GetProperty("structuredContent");
        proposal.GetProperty("status").GetString().Should().Be("RequiresApproval", body);
        return proposal.GetProperty("proposalId").GetString()!;
    }

    private async Task<string> AwaitJobFailureAsync(string jobId)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!deadline.IsCancellationRequested)
        {
            using var response = await _client.GetAsync(
                $"/api/v1/admin/connections/{_connectionId:D}/layers/jobs/{jobId}", deadline.Token);
            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            using var document = JsonDocument.Parse(body);
            var status = document.RootElement.GetProperty("data").GetProperty("status").GetString();
            status.Should().NotBe("Completed", body);
            if (status is "Failed")
            {
                return body;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token);
        }

        throw new TimeoutException($"Layer publish job {jobId} did not finish within 90 seconds.");
    }

    private async Task<long> PublishedLayerCountAsync(string table)
    {
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM honua.layers WHERE table_schema = 'public' AND table_name = @table";
        command.Parameters.AddWithValue("table", table);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
