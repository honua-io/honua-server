// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.WorkflowPackages;

public sealed partial class WorkflowPackageEndpointsTests
{
    [IntegrationTheory]
    [InlineData("Job")]
    [InlineData("Schedule")]
    [InlineData("ProcessEndpoint")]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/publish")]
    public async Task AgentAdministrator_CannotPublishWorkflow(string target)
    {
        var packageId = await CreatePackageAsync("agent-publish", CreateAreaGraph());
        var version = await CreateVersionAsync(packageId);
        var publicationId = "agent-publication-" + Guid.NewGuid().ToString("N");
        using var agent = await CreateWorkflowAgentAsync();
        using var response = await agent.PostAsJsonAsync(
            $"/api/v1/console/workflow-packages/{packageId}/versions/{version.Version}/publish",
            new
            {
                publicationId,
                target,
                enabled = true,
                processId = "workflow.tests.agent",
                schedule = new { cronExpression = "0 0 * * *", timeZone = "UTC", enabled = true }
            }, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IWorkflowPackageStore>()
            .GetPublicationAsync(publicationId)).Should().BeNull();
        (await _definitionStore.GetAsync($"workflow-package:{packageId}:v{version.Version}:{publicationId}"))
            .Should().BeNull();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/console/workflow-publications/{publicationId}/status")]
    public async Task AgentAdministrator_CannotReactivateWorkflowSchedule()
    {
        var packageId = await CreatePackageAsync("agent-reactivate", CreateAreaGraph());
        var version = await CreateVersionAsync(packageId);
        var publicationId = "agent-schedule-" + Guid.NewGuid().ToString("N");
        await PublishAsync(packageId, version.Version, new
        {
            publicationId,
            target = "Schedule",
            enabled = false,
            schedule = new { cronExpression = "0 0 * * *", timeZone = "UTC", enabled = false }
        });
        using var agent = await CreateWorkflowAgentAsync();
        using var response = await agent.PostAsJsonAsync(
            $"/api/v1/console/workflow-publications/{publicationId}/status",
            new { status = "Active" }, JsonOptions);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var scope = _fixture.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IWorkflowPackageStore>()
            .GetPublicationAsync(publicationId))!.Status.Should().Be(WorkflowPublicationStatus.Disabled);
        var definitionId = $"workflow-package:{packageId}:v{version.Version}:{publicationId}";
        (await _definitionStore.GetAsync(definitionId))!.Trigger!.Enabled.Should().BeFalse();
        (await _definitionStore.ListScheduledAsync()).Should().NotContain(d => d.WorkflowId == definitionId);
        using var humanResponse = await _client.PostAsJsonAsync(
            $"/api/v1/console/workflow-publications/{publicationId}/status",
            new { status = "Active" }, JsonOptions);
        humanResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await _definitionStore.GetAsync(definitionId))!.Trigger!.Enabled.Should().BeTrue();
    }

    private async Task<HttpClient> CreateWorkflowAgentAsync()
    {
        var key = await _fixture.Services.GetRequiredService<IAdminApiKeyStore>().CreateAsync(
            "workflow-agent", ["admin", "agent"], null, "test", CancellationToken.None);
        return _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", key.Key));
    }
}
