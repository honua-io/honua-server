// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Server.Features.WorkflowPackages;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.WorkflowPackages;

/// <summary>Observes physical Redis triggers from outside the real Postgres publication transaction.</summary>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Admin)]
public sealed class WorkflowPackageSchedulerCommitTests(RedisFixture redis)
{
    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /api/v1/console/workflow-packages/{packageId}/versions/{packageVersion}/publish")]
    public async Task TriggerRemainsDormantUntilCatalogCommit_AndFailureReconcilesCommittedState(bool commitBeforeFailure)
    {
        var publicationId = "commit-observer-" + Guid.NewGuid().ToString("N");
        string? definitionId = null;
        var observed = false;
        var fixture = new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
        {
            builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });
        WorkflowPackageHttpFixture.Configure(fixture, async (transaction, cancellationToken) =>
        {
            await using var observer = fixture.Services.CreateAsyncScope();
            var definitions = observer.ServiceProvider.GetRequiredService<IWorkflowDefinitionStore>();
            var catalog = observer.ServiceProvider.GetRequiredService<IWorkflowPackageStore>();
            var dormant = await definitions.GetAsync(definitionId!, cancellationToken);
            Assert.NotNull(dormant);
            Assert.False(dormant.Trigger!.Enabled);
            Assert.DoesNotContain(await definitions.ListScheduledAsync(cancellationToken), item => item.WorkflowId == definitionId);
            Assert.Null(await catalog.GetPublicationAsync(publicationId, cancellationToken));
            observed = true;
            if (commitBeforeFailure) await transaction.CommitAsync(cancellationToken);
            throw new IOException("Injected publication commit receipt failure.");
        });
        await fixture.InitializeAsync();
        try
        {
            await WorkflowPackageHttpFixture.InitializeSchemaAsync(fixture);
            using var human = fixture.CreateAdminClient();
            var graph = new WorkflowGraph
            {
                Nodes = [new WorkflowNode { NodeId = "area", NodeTypeId = "process:geometry.area", Parameters = new Dictionary<string, string>()
                {
                    ["wkb"] = "AQMAAAABAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADwPwAAAAAAAAAAAAAAAAAAAAAAAAAAAADwPwAAAAAAAAAAAAAAAAAAAAA=", ["srid"] = "4326",
                } }],
            };
            using var savedResponse = await human.PostAsJsonAsync("/api/v1/console/workflow-packages", new { name = "Commit observer", graph });
            Assert.Equal(HttpStatusCode.Created, savedResponse.StatusCode);
            var saved = await savedResponse.Content.ReadFromJsonAsync<JsonElement>();
            var packageId = saved.GetProperty("data").GetProperty("packageId").GetString()!;
            var root = $"/api/v1/console/workflow-packages/{packageId}";
            using var versionResponse = await human.PostAsync(root + "/versions", null);
            Assert.True(versionResponse.IsSuccessStatusCode);
            definitionId = WorkflowPackageService.ScheduleWorkflowDefinitionId(packageId, 1, publicationId);
            using var publish = await human.PostAsJsonAsync(root + "/versions/1/publish", new
            {
                publicationId,
                target = "Schedule",
                enabled = true,
                schedule = new { cronExpression = "0 0 1 1 *", timeZone = "UTC", enabled = true },
            });
            Assert.False(publish.IsSuccessStatusCode);
            Assert.True(observed);
            await using var scope = fixture.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IWorkflowPackageStore>();
            var definitions = scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionStore>();
            var publication = await catalog.GetPublicationAsync(publicationId);
            var definition = await definitions.GetAsync(definitionId);
            if (commitBeforeFailure)
            {
                Assert.NotNull(publication);
                Assert.Equal(WorkflowPublicationStatus.Active, publication.Status);
                Assert.NotNull(definition);
                Assert.True(definition.Trigger!.Enabled);
            }
            else
            {
                Assert.Null(publication);
                Assert.Null(definition);
            }
        }
        finally
        {
            if (definitionId is not null)
            {
                await using var scope = fixture.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionStore>().DeleteAsync(definitionId);
            }
            await fixture.DisposeAsync();
        }
    }
}
