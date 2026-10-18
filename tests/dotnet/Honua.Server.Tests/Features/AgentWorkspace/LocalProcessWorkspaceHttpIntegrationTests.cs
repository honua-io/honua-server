// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.AgentWorkspace.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

/// <summary>Real Kestrel, bearer authentication and Redis membership; the daemon transport is substituted, not an execution qualification.</summary>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Admin)]
public sealed class LocalProcessWorkspaceHttpIntegrationTests(RedisFixture redis)
{
    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/project/identity")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/processes/local/project/bind")]
    [Endpoint("DELETE /api/v1/agent/workspaces/{workspaceId}/processes/local/project/bindings/{bindingId}")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/project")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/sources/{**path}")]
    [Endpoint("PUT /api/v1/agent/workspaces/{workspaceId}/processes/local/sources/{**path}")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/processes/local/snapshots")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/snapshots/{snapshotId}")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/snapshots/{snapshotId}/sources/{**path}")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/processes/local/runs")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/runs")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/runs/{runId}")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/processes/local/runs/{runId}/cancel")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/commands/{commandId}")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/runs/{runId}/artifacts/{**path}")]
    public async Task HumanRoutes_UseCurrentWorkspaceOwnerAndTenant_NotBrowserAssertions()
    {
        var gateway = Substitute.For<ILocalProcessWorkspaceGateway>();
        gateway.IsConfigured.Returns(true);
        string projectId = new('a', 32), digest = new('b', 64), snapshotId = new('c', 32), runId = new('d', 32);
        var snapshot = new LocalProcessSnapshot(projectId, snapshotId, digest, "sha256:test-runtime", 1);
        var file = new LocalProcessSourceFile("process.py", "print('fixture')", digest);
        var run = new LocalProcessRun
        {
            ProjectId = projectId,
            SnapshotId = snapshotId,
            SnapshotDigest = digest,
            RunId = runId,
            Status = "succeeded",
            Image = snapshot.Image
        };
        static LocalProcessProjectBinding Binding(LocalProcessWorkspaceAccess access) => new(access.OwnerActorId, access.TenantId, access.TargetId, access.WorkspaceId);
        gateway.GetIdentityAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Arg.Any<CancellationToken>())
            .Returns(call => new LocalProcessProjectIdentity(projectId, 1, digest, snapshot.Image, Binding(call.Arg<LocalProcessWorkspaceAccess>())));
        gateway.BindAsync(Arg.Any<LocalProcessWorkspaceAccess>(), projectId, Arg.Any<CancellationToken>())
            .Returns(call => new LocalProcessProjectIdentity(projectId, 1, digest, snapshot.Image, Binding(call.Arg<LocalProcessWorkspaceAccess>())));
        gateway.GetProjectAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Arg.Any<CancellationToken>())
            .Returns(call => new LocalProcessProject(projectId, 1, digest, snapshot.Image, Binding(call.Arg<LocalProcessWorkspaceAccess>()), [file]));
        gateway.ReadSourceAsync(Arg.Any<LocalProcessWorkspaceAccess>(), file.Path, Arg.Any<CancellationToken>()).Returns(file);
        gateway.ReadSnapshotSourceAsync(Arg.Any<LocalProcessWorkspaceAccess>(), snapshotId, file.Path, Arg.Any<CancellationToken>()).Returns(file);
        gateway.WriteSourceAsync(Arg.Any<LocalProcessWorkspaceAccess>(), file.Path, digest, Arg.Any<LocalProcessSourceWrite>(), Arg.Any<CancellationToken>())
            .Returns(new LocalProcessSourceWriteReceipt(file.Path, digest, projectId, 2, digest));
        gateway.SnapshotAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Arg.Any<LocalProcessSnapshotRequest>(), Arg.Any<CancellationToken>()).Returns(snapshot);
        gateway.GetSnapshotAsync(Arg.Any<LocalProcessWorkspaceAccess>(), snapshotId, Arg.Any<CancellationToken>()).Returns(snapshot);
        gateway.RunAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Arg.Any<LocalProcessRunRequest>(), Arg.Any<CancellationToken>()).Returns(run);
        gateway.GetRunAsync(Arg.Any<LocalProcessWorkspaceAccess>(), runId, Arg.Any<CancellationToken>()).Returns(run);
        gateway.ListRunsAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Arg.Any<CancellationToken>()).Returns(new LocalProcessRunList([run]));
        gateway.GetCommandAsync(Arg.Any<LocalProcessWorkspaceAccess>(), "command", Arg.Any<CancellationToken>()).Returns(run);
        gateway.CancelAsync(Arg.Any<LocalProcessWorkspaceAccess>(), runId, Arg.Any<CancellationToken>()).Returns(run);
        gateway.ReadArtifactAsync(Arg.Any<LocalProcessWorkspaceAccess>(), runId, "result.geojson", Arg.Any<CancellationToken>())
            .Returns(new LocalProcessArtifact(new("result.geojson", 2, digest), Encoding.UTF8.GetBytes("{}")));
        var fixture = new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
        {
            builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("AgentWorkspace:DeploymentId", $"local-process-http-{Guid.NewGuid():N}");
            builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
            builder.UseSetting("Authentication:OperatorBearer:SigningKey", "local-process-http-only-signing-key-at-least-32-bytes");
            builder.ConfigureServices(services => services.AddSingleton(gateway));
        });
        await fixture.InitializeAsync();
        try
        {
            using var human = Human("process-owner");
            using var foreign = Human("foreign-owner");
            var created = await Read(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "view" }));
            var workspaceId = created.GetProperty("workspaceId").GetString()!;
            var root = $"/api/v1/agent/workspaces/{workspaceId}/processes/local";
            Assert.Equal(HttpStatusCode.NotFound, (await foreign.GetAsync(root + "/project/identity")).StatusCode);
            // Browser headers cannot replace the current principal, tenant or persisted workspace owner.
            human.DefaultRequestHeaders.Add("X-Honua-Actor-Id", "forged-actor");
            human.DefaultRequestHeaders.Add("X-Honua-Workspace-Owner-Id", "forged-owner");
            human.DefaultRequestHeaders.Add("X-Honua-Tenant-Id", "forged-tenant");
            await Read(await human.GetAsync(root + "/project/identity"));
            await Read(await human.PostAsJsonAsync(root + "/project/bind", new { projectId }));
            await Read(await human.GetAsync(root + "/project"));
            await Read(await human.GetAsync(root + "/sources/process.py"));
            using var write = new HttpRequestMessage(HttpMethod.Put, root + "/sources/process.py")
            {
                Content = JsonContent.Create(new { projectId, expectedRevision = 1, content = "corrected fixture" }),
            };
            write.Headers.IfMatch.Add(new EntityTagHeaderValue('"' + digest + '"'));
            await Read(await human.SendAsync(write));
            await Read(await human.PostAsJsonAsync(root + "/snapshots", new { projectId, expectedRevision = 2, sourceDigest = digest }));
            await Read(await human.GetAsync(root + "/snapshots/" + snapshotId));
            await Read(await human.GetAsync(root + "/snapshots/" + snapshotId + "/sources/process.py"));
            await Read(await human.PostAsJsonAsync(root + "/runs", new { projectId, snapshotId, snapshotDigest = digest, commandId = "command" }));
            await Read(await human.GetAsync(root + "/runs"));
            await Read(await human.GetAsync(root + "/runs/" + runId));
            await Read(await human.PostAsync(root + "/runs/" + runId + "/cancel", null));
            await Read(await human.GetAsync(root + "/commands/command"));
            using var artifact = await human.GetAsync(root + "/runs/" + runId + "/artifacts/result.geojson");
            Assert.Equal(HttpStatusCode.OK, artifact.StatusCode);
            Assert.Equal("{}", await artifact.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.NoContent, (await human.DeleteAsync(root + "/project/bindings/" + workspaceId)).StatusCode);
            var calls = gateway.ReceivedCalls().SelectMany(call => call.GetArguments().OfType<LocalProcessWorkspaceAccess>()).ToArray();
            Assert.NotEmpty(calls);
            Assert.All(calls, access =>
            {
                Assert.Equal(access.ActorId, access.OwnerActorId);
                Assert.NotEqual("forged-actor", access.ActorId);
                Assert.NotEqual("forged-owner", access.OwnerActorId);
                Assert.Equal("public", access.TenantId);
                Assert.Equal(workspaceId, access.WorkspaceId);
                Assert.Equal(created.GetProperty("targetId").GetString(), access.TargetId);
                Assert.False(access.Delegated);
            });
        }
        finally { await fixture.DisposeAsync(); }

        HttpClient Human(string subject)
        {
            var bearer = fixture.Services.GetRequiredService<OperatorBearerTokenService>().Issue(
                [new() { Type = ClaimTypes.NameIdentifier, Value = subject }, new() { Type = ClaimTypes.Role, Value = "admin" },
                 new() { Type = "tenant_id", Value = "public" }], DateTimeOffset.UtcNow.AddMinutes(5))!;
            return fixture.CreateClient(client => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer.Token));
        }
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.Clone();
        }
    }
}
