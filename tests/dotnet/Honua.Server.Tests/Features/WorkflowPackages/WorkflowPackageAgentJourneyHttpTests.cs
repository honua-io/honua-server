// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.WorkflowPackages;

/// <summary>Actual HTTP, production key/bearer authentication, Redis workspace/proposals/scheduler and canonical workflow service.</summary>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Admin, TestProtocols.Mcp)]
public sealed class WorkflowPackageAgentJourneyHttpTests(RedisFixture redis)
{
    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /mcp tools/call honua_workflow_package")]
    [Endpoint("POST /mcp tools/call honua_workflow_package_propose")]
    [Endpoint("GET /api/v1/admin/proposals/{id}")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task AgentAuthorsImmutableVersion_HumanApprovesScheduleStatusAndDelete_WithNoAgentApprovalOrExecution()
    {
        var fixture = new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
        {
            builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("AgentWorkspace:DeploymentId", $"workflow-http-{Guid.NewGuid():N}");
            builder.UseSetting("AgentWorkspace:ReleaseId", "workflow-http-preview");
            builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
            builder.UseSetting("Authentication:OperatorBearer:SigningKey", "workflow-http-only-signing-key-at-least-32-bytes");
            // Workspace approval remains mandatory even when ordinary human admins are exempt.
            builder.UseSetting("OperatorApproval:PublishRequiresApproval", "false");
            builder.UseSetting("OperatorApproval:AdminExemptFromApproval", "true");
        });
        WorkflowPackageHttpFixture.Configure(fixture);
        await fixture.InitializeAsync();
        try
        {
            await WorkflowPackageHttpFixture.InitializeSchemaAsync(fixture);
            using var bootstrap = fixture.CreateAdminClient();
            var key = await Read(await bootstrap.PostAsJsonAsync("/api/v1/admin/api-keys", new
            { name = "workflow-agent", permissions = new[] { "admin", "read:*", "agent:workspace" } }), HttpStatusCode.Created);
            var material = key.GetProperty("data").GetProperty("key").GetString()!;
            var keyId = key.GetProperty("data").GetProperty("apiKey").GetProperty("id").GetString()!;
            fixture.Services.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = $"apikey:api-key:{keyId}";
            var bearer = fixture.Services.GetRequiredService<OperatorBearerTokenService>().Issue(
                [new() { Type = ClaimTypes.NameIdentifier, Value = "workflow-human" },
                 new() { Type = ClaimTypes.Role, Value = "admin" }, new() { Type = "tenant_id", Value = "public" }], DateTimeOffset.UtcNow.AddMinutes(10));
            bearer.Should().NotBeNull();
            using var human = fixture.CreateClient(client => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer!.Token));
            using var agent = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", material));
            var target = await Read(await human.GetAsync("/api/v1/agent/target"), HttpStatusCode.OK);
            var created = await Read(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new
            { viewId = "workflow-tab", targetId = target.GetProperty("targetId").GetString() }), HttpStatusCode.OK);
            var workspaceId = created.GetProperty("workspaceId").GetString()!;
            var workspaceRoot = $"/api/v1/agent/workspaces/{workspaceId}";
            await Read(await human.PostAsJsonAsync(workspaceRoot + "/turns", new
            { turnId = "author-turn", viewId = "workflow-tab", contextRevision = 0, prompt = "Author an immutable area workflow; propose its schedule for review." }), HttpStatusCode.OK);
            agent.DefaultRequestHeaders.Add("X-Honua-Workspace", workspaceId);
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "author-turn");
            var graph = new WorkflowGraph
            {
                Nodes = [new WorkflowNode { NodeId = "area", NodeTypeId = "process:geometry.area", Parameters = new Dictionary<string, string>
                {
                    // Valid triangle WKB. This journey validates and publishes, but does not execute it.
                    ["wkb"] = "AQMAAAABAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADwPwAAAAAAAAAAAAAAAAAAAAAAAAAAAADwPwAAAAAAAAAAAAAAAAAAAAA=", ["srid"] = "4326",
                } }],
            };
            var createArguments = new { action = "create", workspaceId, turnId = "author-turn", commandId = "immutable-command", name = "Area workflow", graph };
            var authored = await Tool(agent, "honua_workflow_package", createArguments);
            var version = authored.GetProperty("version");
            var packageId = version.GetProperty("packageId").GetString()!;
            var hash = version.GetProperty("packageHash").GetString()!;
            version.GetProperty("version").GetInt32().Should().Be(1);
            await using var scope = fixture.Services.CreateAsyncScope();
            var workspace = (await scope.ServiceProvider.GetRequiredService<IAgentWorkspaceStore>().GetAsync(workspaceId))!;
            version.GetProperty("ownership").GetProperty("ownerActorId").GetString().Should().Be(workspace.OwnerId);
            version.GetProperty("createdBy").GetString().Should().Be($"apikey:api-key:{keyId}");
            // Lost response/repeated command reconciles the exact original immutable version.
            var retry = await Tool(agent, "honua_workflow_package", createArguments);
            retry.GetProperty("version").GetProperty("packageId").GetString().Should().Be(packageId);
            var conflict = await Rpc(agent, "honua_workflow_package", new
            { action = "create", workspaceId, turnId = "author-turn", commandId = "immutable-command", name = "Changed payload", graph });
            IsFailure(conflict).Should().BeTrue();
            var packages = scope.ServiceProvider.GetRequiredService<IWorkflowPackageStore>();
            (await packages.ListVersionsAsync(packageId)).Should().ContainSingle();
            (await packages.ListPublicationsAsync(packageId)).Should().BeEmpty();
            // This is the Studio Processes host attach/read contract: an immutable built-in
            // reference, never an invented draft id or client-provided ownership field.
            authored.GetProperty("attachmentState").GetString().Should().Be("Attached");
            authored.GetProperty("attachedRevision").GetInt64().Should().Be(1);
            workspace.Task.Should().Be(new AgentTaskReference(packageId, 1, "workflow-package", ContentHash: hash));
            var attached = await Read(await human.GetAsync(workspaceRoot + "?viewId=workflow-tab"), HttpStatusCode.OK);
            attached.GetProperty("task").GetProperty("contentHash").GetString().Should().Be(hash);
            await Read(await human.PostAsJsonAsync(workspaceRoot + "/turns", new
            { turnId = "publish-turn", viewId = "workflow-tab", contextRevision = 1, prompt = "Propose this exact attached built-in workflow for human review." }), HttpStatusCode.OK);
            agent.DefaultRequestHeaders.Remove("X-Honua-Turn");
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "publish-turn");
            var captured = await Read(await agent.GetAsync(workspaceRoot + "/turns/publish-turn"), HttpStatusCode.OK);
            captured.GetProperty("context").GetProperty("task").GetProperty("id").GetString().Should().Be(packageId);
            var storedVersion = await Read(await human.GetAsync($"/api/v1/console/workflow-packages/{packageId}/versions/1"), HttpStatusCode.OK);
            storedVersion.GetProperty("data").GetProperty("packageHash").GetString().Should().Be(hash);
            storedVersion.GetProperty("data").GetProperty("graph").GetProperty("nodes")[0].GetProperty("nodeTypeId").GetString().Should().Be("process:geometry.area");
            var agentVersion = await Tool(agent, "honua_workflow_package", new { action = "read", workspaceId, turnId = "publish-turn", packageId, version = 1 });
            agentVersion.GetProperty("version").GetProperty("packageHash").GetString().Should().Be(hash);
            (await human.PutAsJsonAsync(workspaceRoot + "/context", new
            {
                viewId = "workflow-tab",
                expectedRevision = 1,
                targetId = target.GetProperty("targetId").GetString(),
                selectedResources = Array.Empty<object>(),
                task = new { kind = "workflow-package", id = packageId, revision = 1, contentHash = new string('0', 64) },
            })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var publicationId = "workflow-http-publication-" + Guid.NewGuid().ToString("N");
            var scheduled = await ProposeAndApprove(new WorkflowPackageGovernanceIntent
            {
                Action = "publish",
                PackageId = packageId,
                Version = 1,
                PackageHash = hash,
                PublicationId = publicationId,
                Target = WorkflowPublicationTarget.Schedule,
                Schedule = new() { Enabled = true, CronExpression = "0 0 1 1 *", TimeZone = "UTC" },
            });
            var definitions = scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionStore>();
            var definition = await definitions.GetAsync(scheduled.WorkflowDefinitionId!);
            definition.Should().NotBeNull();
            definition!.Trigger!.Enabled.Should().BeTrue();
            definition.AuthorSecurityContext!.OwnerActorId.Should().Be($"apikey:api-key:{keyId}");
            await ProposeAndApprove(new()
            {
                Action = "set-status",
                PackageId = packageId,
                Version = 1,
                PackageHash = hash,
                PublicationId = publicationId,
                Status = WorkflowPublicationStatus.Disabled
            });
            (await definitions.GetAsync(scheduled.WorkflowDefinitionId!))!.Trigger!.Enabled.Should().BeFalse();
            await ProposeAndApprove(new()
            {
                Action = "set-status",
                PackageId = packageId,
                Version = 1,
                PackageHash = hash,
                PublicationId = publicationId,
                Status = WorkflowPublicationStatus.Active
            });
            (await definitions.GetAsync(scheduled.WorkflowDefinitionId!))!.Trigger!.Enabled.Should().BeTrue();
            await ProposeAndApprove(new() { Action = "delete", PackageId = packageId, Version = 1, PackageHash = hash, PublicationId = publicationId });
            (await packages.GetPublicationAsync(publicationId)).Should().BeNull();
            (await definitions.GetAsync(scheduled.WorkflowDefinitionId!)).Should().BeNull();
            (await packages.GetVersionAsync(packageId, 1)).Should().NotBeNull();

            async Task<WorkflowPublication> ProposeAndApprove(WorkflowPackageGovernanceIntent intent)
            {
                var before = await packages.GetPublicationAsync(publicationId);
                var handle = await Tool(agent, "honua_workflow_package_propose", new { workspaceId, turnId = "publish-turn", intent });
                handle.GetProperty("status").GetString().Should().Be("RequiresApproval");
                var proposalId = handle.GetProperty("proposalId").GetString()!;
                var operationId = handle.GetProperty("operationInstanceId").GetString()!;
                var repeated = await Tool(agent, "honua_workflow_package_propose", new { workspaceId, turnId = "publish-turn", intent });
                repeated.GetProperty("operationInstanceId").GetString().Should().Be(operationId);
                var pending = await packages.GetPublicationAsync(publicationId);
                pending.Should().Be(before, "proposing may not actuate the workflow transition");
                (await agent.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                using var inbox = await human.GetAsync($"/api/v1/admin/proposals/{proposalId}");
                inbox.StatusCode.Should().Be(HttpStatusCode.OK);
                var inboxText = await inbox.Content.ReadAsStringAsync();
                inboxText.Contains(material, StringComparison.Ordinal).Should().BeFalse("credential material cannot appear in inbox receipts");
                inboxText.Contains("executionPayload", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
                (await human.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
                var proposal = await scope.ServiceProvider.GetRequiredService<IOperationProposalStore>().GetAsync(proposalId);
                proposal!.Status.Should().Be(OperationProposalStatus.Succeeded);
                var after = await packages.GetPublicationAsync(publicationId);
                return after ?? before ?? throw new InvalidOperationException("No publication receipt after approved transition.");
            }
        }
        finally { await fixture.DisposeAsync(); }
    }

    private static async Task<JsonElement> Tool(HttpClient client, string name, object arguments)
    {
        var envelope = await Rpc(client, name, arguments);
        IsFailure(envelope).Should().BeFalse();
        return envelope.GetProperty("result").GetProperty("structuredContent").Clone();
    }
    private static bool IsFailure(JsonElement envelope) => envelope.TryGetProperty("error", out _) ||
        envelope.TryGetProperty("result", out var result) && result.TryGetProperty("isError", out var error) && error.GetBoolean();
    private static async Task<JsonElement> Rpc(HttpClient client, string name, object arguments)
        => await Read(await client.PostAsJsonAsync("/mcp", new
        { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "tools/call", @params = new { name, arguments } }), HttpStatusCode.OK);
    private static async Task<JsonElement> Read(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            response.StatusCode.Should().Be(expected);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }
    }
}
