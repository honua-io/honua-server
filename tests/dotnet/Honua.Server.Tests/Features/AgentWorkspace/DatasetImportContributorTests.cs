// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class DatasetImportContributorTests
{
    [UnitTheory]
    [InlineData("allowed-read")]
    [InlineData("allowed-write")]
    [InlineData("wrong-initiator")]
    [InlineData("human-grant")]
    [InlineData("executor-grant")]
    [InlineData("executor-revoked")]
    [InlineData("membership-during-check")]
    [InlineData("target-during-check")]
    public async Task ContributorNeedsOriginalBindingAndIndependentCurrentHumanAndExecutorAuthority(string scenario)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human-1")], "Bearer");
        CanonicalSecurityActor.StampAuthorityClaims(identity);
        var human = new ClaimsPrincipal(identity);
        var humanActor = CanonicalSecurityActor.Resolve(human)!.ActorId;
        var keys = new InMemoryAdminApiKeyStore();
        var issued = await keys.CreateAsync("contribution-agent", ["agent:workspace", "admin:read"], null, null, default);
        var agent = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(issued.Record);
        var agentActor = CanonicalSecurityActor.Resolve(agent)!.ActorId;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        var destination = Substitute.For<IDatasetImportDestinationAuthorizer>();
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>()).Returns(AuthorizationResult.Success());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AgentWorkspace:ReleaseId"] = "test-release" }).Build();
        using var services = new ServiceCollection().AddSingleton<IAdminApiKeyStore>(keys).AddSingleton(tenant)
            .AddSingleton(destination).AddSingleton(authorization).AddSingleton<IConfiguration>(configuration).BuildServiceProvider();
        var context = new DefaultHttpContext { User = human, RequestServices = services };
        var snapshot = (await AgentCredentialAuthority.CaptureAsync(agent, services, default))!;
        var policy = new JobSecurityContext(agentActor, "tenant-1", [], "role") { OwnerActorId = agentActor, AgentCredential = snapshot };
        var task = new DatasetImportTask("import-task-1", 1, string.Empty, agentActor, "tenant-1", "target-1", "workspace-1",
            new("public", "new_table"), new("browser-file", "file.geojson", 123, new string('a', 64)), new(), DateTimeOffset.UtcNow.AddHours(24));
        task = task with { ContentHash = DatasetImportTaskState.ContentHash(task) };
        var turn = new AgentWorkspaceTurn("turn-1", humanActor, "Import my selected file",
            new("workspace-1", "target-1", "view-1", 0, [], null, "tenant-1"), null);
        var binding = new DatasetImportExecutionBinding(task.TaskId, 1, task.ContentHash, 1, "credential-fingerprint", agentActor,
            scenario == "wrong-initiator" ? "other-human" : humanActor, turn.TurnId,
            Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn))),
            policy, JobSecurityContextFingerprint.Compute(policy), snapshot);
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = "workspace-1",
            TargetId = "target-1",
            OwnerId = humanActor,
            AgentActorId = agentActor,
            TenantId = "tenant-1",
            Members = [humanActor, agentActor],
            Turns = new() { [turn.TurnId] = turn },
            DatasetImportTasks = new() { [task.TaskId] = [task] },
            DatasetImportExecutionBindings = new() { [task.TaskId] = binding },
        };
        var store = Substitute.For<IAgentWorkspaceStore>();
        var target = "target-1";
        store.GetTargetIdAsync().Returns(_ => target);
        store.GetAsync("workspace-1").Returns(_ => document);
        var write = scenario == "allowed-write";
        destination.IsAuthorizedAsync(Arg.Any<ClaimsPrincipal>(), "target-1", task.Destination, task.Options, write, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var actor = CanonicalSecurityActor.Resolve(call.ArgAt<ClaimsPrincipal>(0))!.ActorId;
                if (scenario == "membership-during-check") document = document with { Members = [humanActor] };
                if (scenario == "target-during-check") target = "target-2";
                return !(scenario == "human-grant" && actor == humanActor || scenario == "executor-grant" && actor == agentActor);
            });
        if (scenario == "executor-revoked") await keys.RevokeAsync(issued.Record.Id, default);
        var contributor = new DatasetImportContributorAuthorizer(store, new HttpContextAccessor { HttpContext = context });
        var result = await contributor.AuthorizeAsync(human, "workspace-1", task.TaskId, 1, task.ContentHash, write);
        if (scenario.StartsWith("allowed-", StringComparison.Ordinal))
        {
            result.Should().NotBeNull();
            result!.ExecutorActorId.Should().Be(agentActor);
            result.ContributorActorId.Should().Be(humanActor);
            result.Task.OwnerActorId.Should().Be(agentActor);
            await destination.Received().IsAuthorizedAsync(human, "target-1", task.Destination, task.Options, write, Arg.Any<CancellationToken>());
            await destination.Received().IsAuthorizedAsync(Arg.Is<ClaimsPrincipal>(value => CanonicalSecurityActor.Resolve(value)!.ActorId == agentActor),
                "target-1", task.Destination, task.Options, write, Arg.Any<CancellationToken>());
        }
        else result.Should().BeNull();
        await store.DidNotReceive().SaveAsync(Arg.Any<AgentWorkspaceDocument>());
    }
}
