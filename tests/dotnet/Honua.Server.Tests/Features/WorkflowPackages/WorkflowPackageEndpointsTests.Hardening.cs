// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.WorkflowPackages;

public sealed partial class WorkflowPackageEndpointsTests
{
    public static TheoryData<string> UnsafeIdentifiers => new()
    {
        "a/b",
        "a:v1:b",
        "a\r\nb",
        "-leading-dash",
        "a b",
        new string('p', 129),
    };

    [IntegrationTheory]
    [MemberData(nameof(UnsafeIdentifiers))]
    [Endpoint("POST /api/v1/console/workflow-packages")]
    public async Task SavePackage_WithUnsafePackageId_IsRefusedAndNotStored(string packageId)
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/v1/console/workflow-packages",
            CreatePackageRequest("unsafe-id", CreateAreaGraph(), packageId),
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("packageId");
        (await GetStoredPackageAsync(packageId.Trim())).Should().BeNull();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-packages")]
    public async Task SavePackage_WithDottedPackageId_IsCreatedAtItsOwnRoute()
    {
        var packageId = $"team.flood_{Guid.NewGuid():N}-v2";
        using var response = await _client.PostAsJsonAsync(
            "/api/v1/console/workflow-packages",
            CreatePackageRequest("dotted-id", CreateAreaGraph(), packageId),
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location!.OriginalString.Should().Be($"/api/v1/console/workflow-packages/{packageId}");
        using var reread = await _client.GetAsync(response.Headers.Location);
        reread.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-packages")]
    public async Task SavePackage_WithNonAsciiNameAndNoId_GetsAValidGeneratedId()
    {
        using var response = await _client.PostAsJsonAsync(
            "/api/v1/console/workflow-packages",
            CreatePackageRequest("Écoulement crue 洪水", CreateAreaGraph()),
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = await ReadJsonAsync(response);
        doc.RootElement.GetProperty("data").GetProperty("packageId").GetString()
            .Should().MatchRegex("^pkg-[a-z0-9-]+$");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/publish")]
    public async Task PublishVersion_WithColonPublicationId_DoesNotWriteAPublicationOrSchedule()
    {
        var packageId = await CreatePackageAsync("colon-publication", CreateAreaGraph());
        var version = await CreateVersionAsync(packageId);
        var publicationId = $"p{Guid.NewGuid():N}:v1:q";
        using var response = await _client.PostAsJsonAsync(
            $"/api/v1/console/workflow-packages/{packageId}/versions/{version.Version}/publish",
            new
            {
                publicationId,
                target = "Schedule",
                schedule = new { cronExpression = "0 0 * * *", timeZone = "UTC", enabled = true }
            },
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetStoredPublicationAsync(publicationId)).Should().BeNull();
        (await _definitionStore.GetAsync($"workflow-package:{packageId}:v{version.Version}:{publicationId}"))
            .Should().BeNull();
    }

    [IntegrationTheory]
    [InlineData("name")]
    [InlineData("namespace")]
    [InlineData("description")]
    [InlineData("nodes")]
    [InlineData("edges")]
    [InlineData("metadata")]
    [InlineData("editorMetadata")]
    [Endpoint("POST /api/v1/console/workflow-packages")]
    public async Task SavePackage_OverASizeLimit_IsRefusedAndNotStored(string oversized)
    {
        var packageId = $"oversized-{oversized}-{Guid.NewGuid():N}";
        using var response = await _client.PostAsJsonAsync(
            "/api/v1/console/workflow-packages",
            OversizedPackageRequest(packageId, oversized),
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("exceeds the limit");
        (await GetStoredPackageAsync(packageId)).Should().BeNull();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-packages")]
    public async Task SavePackage_AtTheNodeLimit_IsStored()
    {
        var packageId = $"at-limit-{Guid.NewGuid():N}";
        using var response = await _client.PostAsJsonAsync(
            "/api/v1/console/workflow-packages",
            CreatePackageRequest("at-limit", AreaNodesGraph(500, 0), packageId),
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await GetStoredPackageAsync(packageId))!.Graph.Nodes.Should().HaveCount(500);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-publications/{publicationId}/status")]
    public async Task SetPublicationStatus_WithUndefinedNumericStatus_LeavesTheScheduleArmed()
    {
        var packageId = await CreatePackageAsync("undefined-status", CreateAreaGraph());
        var version = await CreateVersionAsync(packageId);
        var publicationId = $"status-{Guid.NewGuid():N}";
        await PublishAsync(packageId, version.Version, new
        {
            publicationId,
            target = "Schedule",
            schedule = new { cronExpression = "0 0 * * *", timeZone = "UTC", enabled = true }
        });

        using var response = await _client.PostAsJsonAsync(
            $"/api/v1/console/workflow-publications/{publicationId}/status",
            new { status = 7 },
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetStoredPublicationAsync(publicationId))!.Status.Should().Be(WorkflowPublicationStatus.Active);
        (await _definitionStore.GetAsync($"workflow-package:{packageId}:v{version.Version}:{publicationId}"))!
            .Trigger!.Enabled.Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/publish")]
    public async Task PublishVersion_WithUndefinedNumericTarget_DoesNotWriteAPublication()
    {
        var packageId = await CreatePackageAsync("undefined-target", CreateAreaGraph());
        var version = await CreateVersionAsync(packageId);
        var publicationId = $"target-{Guid.NewGuid():N}";
        using var response = await _client.PostAsJsonAsync(
            $"/api/v1/console/workflow-packages/{packageId}/versions/{version.Version}/publish",
            new { publicationId, target = 9 },
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetStoredPublicationAsync(publicationId)).Should().BeNull();
    }

    private static object OversizedPackageRequest(string packageId, string oversized) => new
    {
        packageId,
        name = oversized == "name" ? new string('n', 201) : "oversized",
        @namespace = oversized == "namespace" ? new string('s', 201) : null,
        description = oversized == "description" ? new string('d', 4_001) : null,
        graph = AreaNodesGraph(
            oversized == "nodes" ? 501 : 1,
            oversized == "edges" ? 2_001 : 0,
            oversized == "editorMetadata" ? 65 : 0),
        metadata = Enumerable.Range(0, oversized == "metadata" ? 65 : 1)
            .ToDictionary(i => $"key{i}", _ => "value"),
    };

    private static object AreaNodesGraph(int nodeCount, int edgeCount, int editorMetadataCount = 0) => new
    {
        nodes = Enumerable.Range(0, nodeCount)
            .Select(i => new { nodeId = $"area-{i}", nodeTypeId = "process:geometry.area" })
            .ToArray(),
        edges = Enumerable.Range(0, edgeCount)
            .Select(_ => new { sourceNodeId = "area-0", targetNodeId = "area-0" })
            .ToArray(),
        editorMetadata = Enumerable.Range(0, editorMetadataCount)
            .ToDictionary(i => $"key{i}", _ => "value"),
    };

    private async Task<WorkflowPackage?> GetStoredPackageAsync(string packageId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkflowPackageStore>().GetPackageAsync(packageId);
    }

    private async Task<WorkflowPublication?> GetStoredPublicationAsync(string publicationId)
    {
        using var scope = _fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkflowPackageStore>().GetPublicationAsync(publicationId);
    }
}
