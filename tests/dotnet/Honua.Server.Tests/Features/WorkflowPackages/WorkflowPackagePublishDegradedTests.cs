// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// honua-server#3585: publishing a workflow package to a <c>Schedule</c> target used to return
/// <c>200 OK</c> on a Redis-less install while silently skipping the one durable write that makes
/// the publication real — the compiled <c>WorkflowDefinition</c> in
/// <c>IWorkflowDefinitionStore</c>. The response advertised a <c>workflowDefinitionId</c> and an
/// <c>Active</c> status for a definition that was never persisted and that no scheduler existed to
/// fire, which is a fabricated success under the 2026.1 arc invariant (a skipped write cannot
/// satisfy a write step). The publish must instead refuse with the machine-readable
/// capability-unavailable receipt (honua-release#202) that geoprocessing submission and the
/// proposal control plane already emit.
/// </summary>
/// <remarks>
/// <see cref="WebAppFixture"/> runs without Redis, so
/// <c>OrchestrationServiceCollectionExtensions.AddOrchestration</c> registers no
/// <see cref="IWorkflowDefinitionStore"/> — exactly the degraded composition under test. The
/// durable path (store present, definition actually persisted) is covered by
/// <c>WorkflowPackageEndpointsTests</c>, which supplies the store a Redis-backed deployment has.
/// </remarks>
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
        // The fixture composes no IWorkflowDefinitionStore, which is the Redis-less posture.
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
        root.GetProperty("missingDependency").GetString().Should().Be(CapabilityUnavailableCodes.RedisDependency);
        root.GetProperty("capability").GetString().Should().Be(CapabilityUnavailableCodes.DurableJobsCapability);
        root.GetProperty("remediation").GetString().Should().Be(CapabilityUnavailableCodes.RedisRemediation);
        root.GetProperty("remediationRef").GetString().Should().Be(CapabilityUnavailableCodes.RedisRemediationRef);
        root.GetProperty("detail").GetString()
            .Should().Be(CapabilityUnavailableCodes.DurableWorkflowPublicationDetail);
        root.TryGetProperty("missingEntitlement", out _).Should().BeFalse(
            "no Redis connection string is configured, so a dependency is missing rather than a licence");

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
    public async Task PublishVersion_ScheduleTargetWithRedisConfiguredButUnentitled_ReportsLicenseNotMissingRedis()
    {
        // The default quickstart lands here, not on the no-Redis path: outside Production, Redis
        // is deployed but the Pro `caching.redis` entitlement gates IConnectionMultiplexer, so the
        // durable workflow definition store is composed out by licensing. "Configure Redis and
        // restart" would be remediation that cannot work (honua-release#202), so the receipt must
        // name the entitlement and match what the manifest reports for `jobs.runner` on this host.
        var fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureServices(static services =>
                services.Configure<DurableJobSubstrateOptions>(options =>
                {
                    options.RedisConfigured = true;
                    options.RedisEntitled = false;
                }));
        await fixture.InitializeAsync();

        try
        {
            using var client = fixture.CreateAdminClient();
            var packageId = await CreatePackageAsync("schedule-unentitled", client);
            var version = await CreateVersionAsync(packageId, client);

            using var response = await client.PostAsJsonAsync(
                $"/api/v1/console/workflow-packages/{packageId}/versions/{version}/publish",
                new
                {
                    publicationId = "pub-schedule-3585-unentitled",
                    target = "Schedule",
                    schedule = new { cronExpression = "0 0 * * *", timeZone = "UTC", enabled = true },
                    enabled = true
                },
                JsonOptions);

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = document.RootElement;
            root.GetProperty("type").GetString().Should().Be(CapabilityUnavailableCodes.ProblemType);
            root.GetProperty("code").GetString().Should().Be(CapabilityUnavailableCodes.EntitlementErrorCode);
            root.GetProperty("missingEntitlement").GetString()
                .Should().Be(CapabilityUnavailableCodes.RedisCacheEntitlement);
            root.TryGetProperty("missingDependency", out _).Should().BeFalse(
                "Redis is present; nothing is missing but a licence");
            root.GetProperty("capability").GetString().Should().Be(CapabilityUnavailableCodes.DurableJobsCapability);
            root.GetProperty("detail").GetString()
                .Should().Be(CapabilityUnavailableCodes.UnentitledWorkflowPublicationDetail);
            root.GetProperty("remediation").GetString().Should().NotContain("Set ConnectionStrings__Redis");
            root.GetProperty("remediationRef").GetString()
                .Should().Be(CapabilityUnavailableCodes.EntitlementRemediationRef);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
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
