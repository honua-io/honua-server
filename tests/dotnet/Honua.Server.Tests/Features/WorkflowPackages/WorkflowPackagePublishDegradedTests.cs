// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.WorkflowPackages;

// A Schedule publication is real only once its compiled WorkflowDefinition reaches
// IWorkflowDefinitionStore. A host without that store must refuse with the capability-unavailable
// receipt that geoprocessing submission and the proposal control plane emit, rather than return
// 200 with a workflowDefinitionId and an Active status that no scheduler would ever fire.
// A WebAppFixture test host registers its data source after the feature graph, so AddOrchestration
// sees no system database and registers no store: the degraded composition under test.
// WorkflowPackageEndpointsTests covers the durable path.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ProcessExecution)]
public sealed class WorkflowPackagePublishDegradedTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebAppFixture _fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro);
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/publish")]
    public async Task PublishVersion_ScheduleTargetWithoutDurableWorkflowStore_ReturnsTypedCapabilityUnavailableRefusal()
    {
        // The fixture composes no IWorkflowDefinitionStore, the posture of a host without a system database.
        _fixture.Services.GetService<IWorkflowDefinitionStore>().Should().BeNull(
            "this test asserts the refusal that fires precisely when the durable workflow store is absent");

        var packageId = await CreatePackageAsync("schedule-degraded");
        var version = await CreateVersionAsync(packageId);

        using var response = await _client.PostAsJsonAsync(
            $"/api/v1/console/workflow-packages/{packageId}/versions/{version}/publish",
            new
            {
                publicationId = "pub-schedule-3585",
                target = "Schedule",
                schedule = new { cronExpression = "0 0 * * *", timeZone = "UTC", enabled = true },
                enabled = true
            },
            JsonOptions);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("type").GetString().Should().Be(CapabilityUnavailableCodes.ProblemType);
        root.GetProperty("title").GetString().Should().Be(CapabilityUnavailableCodes.Title);
        root.GetProperty("status").GetInt32().Should().Be(503);
        root.GetProperty("code").GetString().Should().Be(CapabilityUnavailableCodes.ErrorCode);
        // The refusal names the workflow definition store and sends the operator to the system
        // database connection that composes it.
        root.GetProperty("missingDependency").GetString().Should().Be("workflow-definition-store");
        root.GetProperty("capability").GetString().Should().Be(CapabilityUnavailableCodes.DurableJobsCapability);
        var remediation = root.GetProperty("remediation").GetString();
        remediation.Should().Contain("ConnectionStrings__DefaultConnection").And.NotContain("Redis");
        root.GetProperty("remediationRef").GetString().Should().NotBe(CapabilityUnavailableCodes.SystemDatabaseRemediationRef);
        root.GetProperty("detail").GetString()
            .Should().Be(CapabilityUnavailableCodes.DurableWorkflowPublicationDetail);
        root.TryGetProperty("missingEntitlement", out _).Should().BeFalse(
            "no system database is composed, so a dependency is missing rather than a licence");

        // A refused publish must leave no trace: the fabricated-success bug was visible precisely
        // because a publication record existed for a definition that did not.
        using var publications = await _client.GetAsync("/api/v1/console/workflow-publications");
        publications.StatusCode.Should().Be(HttpStatusCode.OK);
        using var publicationsDocument = JsonDocument.Parse(await publications.Content.ReadAsStringAsync());
        publicationsDocument.RootElement.GetProperty("data").GetProperty("items").EnumerateArray()
            .Should().NotContain(item => item.GetProperty("publicationId").GetString() == "pub-schedule-3585");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/publish")]
    public async Task PublishVersion_NonScheduleTargetsWithoutDurableWorkflowStore_StillPublish()
    {
        // The refusal is scoped to the target whose durable write was being skipped. Job and
        // process-endpoint publications never wrote a workflow definition, so they are unaffected;
        // their runs are gated separately by the geoprocessing job store's own typed refusal.
        var packageId = await CreatePackageAsync("non-schedule-degraded");
        var version = await CreateVersionAsync(packageId);

        foreach (var (publicationId, target) in new[]
                 {
                     ("pub-job-3585", "Job"),
                     ("pub-process-3585", "ProcessEndpoint")
                 })
        {
            using var response = await _client.PostAsJsonAsync(
                $"/api/v1/console/workflow-packages/{packageId}/versions/{version}/publish",
                new { publicationId, target, processId = "workflow.tests.degraded", enabled = true },
                JsonOptions);

            response.StatusCode.Should().Be(
                HttpStatusCode.OK,
                await response.Content.ReadAsStringAsync());
        }
    }

    private async Task<string> CreatePackageAsync(string name, HttpClient? client = null)
    {
        using var response = await (client ?? _client).PostAsJsonAsync(
            "/api/v1/console/workflow-packages",
            new
            {
                packageId = (string?)null,
                name,
                description = "Workflow package publish degraded-posture test (#3585).",
                @namespace = "tests.workflow-packages",
                graph = new
                {
                    nodes = new[]
                    {
                        new
                        {
                            nodeId = "area-1",
                            nodeTypeId = "process:geometry.area",
                            label = "Area",
                            parameters = new Dictionary<string, string>
                            {
                                ["wkb"] = "AQ==",
                                ["srid"] = "4326"
                            }
                        }
                    },
                    edges = Array.Empty<object>(),
                    metadata = new Dictionary<string, string>()
                },
                metadata = new Dictionary<string, string>
                {
                    ["test"] = "3585"
                }
            },
            JsonOptions);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").GetProperty("packageId").GetString()!;
    }

    private async Task<int> CreateVersionAsync(string packageId, HttpClient? client = null)
    {
        using var response = await (client ?? _client).PostAsync(
            $"/api/v1/console/workflow-packages/{packageId}/versions",
            content: null);

        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").GetProperty("version").GetInt32();
    }
}
