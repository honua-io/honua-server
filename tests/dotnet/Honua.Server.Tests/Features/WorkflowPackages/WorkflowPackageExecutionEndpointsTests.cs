// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.WorkflowPackages;

public sealed partial class WorkflowPackageEndpointsTests
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-packages")]
    [Endpoint("GET /api/v1/console/workflow-packages/{packageId}")]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions")]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/publish")]
    public async Task WorkflowExecutionFields_HttpSaveReloadAndPublish_PreserveRuntimeContract()
    {
        var graph = JsonSerializer.SerializeToNode(CreateAreaGraph())!;
        var work = graph["nodes"]![0]!;
        work["retryPolicy"] = JsonNode.Parse("""{"maxAttempts":3,"initialDelaySeconds":2,"backoffMultiplier":2,"maxDelaySeconds":30}""");
        work["failurePolicy"] = "Skip";
        work["timeoutSeconds"] = 9;
        work["forEach"] = JsonNode.Parse("""{"items":["east","west"],"maxIterations":2}""");
        var packageId = await CreatePackageAsync("execution-contract", graph);
        var read = await _client.GetAsync($"/api/v1/console/workflow-packages/{packageId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var saved = await ReadJsonAsync(read);
        var savedWork = saved.RootElement.GetProperty("data").GetProperty("graph").GetProperty("nodes")[0];
        Assert.Equal(3, savedWork.GetProperty("retryPolicy").GetProperty("maxAttempts").GetInt32());
        Assert.Equal("Skip", savedWork.GetProperty("failurePolicy").GetString());
        Assert.Equal(9, savedWork.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(2, savedWork.GetProperty("forEach").GetProperty("items").GetArrayLength());
        var version = await CreateVersionAsync(packageId);
        await PublishAsync(packageId, version.Version, new
        {
            target = "Schedule", enabled = true,
            schedule = new { cronExpression = "0 * * * *", timeZone = "UTC", enabled = true }
        });
        var definition = Assert.Single(await _definitionStore.ListAsync());
        Assert.Equal(3, definition.Steps[0].RetryPolicy!.MaxAttempts);
        Assert.Equal(9, definition.Steps[0].TimeoutSeconds);
        Assert.Equal(new[] { "east", "west" }, definition.Steps[0].ForEach!.Items);
    }
}
