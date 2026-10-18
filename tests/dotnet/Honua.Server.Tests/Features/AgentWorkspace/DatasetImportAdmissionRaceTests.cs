// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.AgentWorkspace.Domain;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.AgentWorkspace;

// Real Redis CAS and restart proof. The task authorization port is controlled;
// this does not qualify destination enforcement or OIDC refresh authentication.
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Admin)]
public sealed class DatasetImportAdmissionRaceTests(RedisFixture redis)
{
    [IntegrationTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PreparationAndAuthorityWriterHaveOneWinnerAndRestartRecoversOriginal(bool authorityWins, bool contribution)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var deployment = $"admission-test-{Guid.NewGuid():N}";
        var store = new AgentWorkspaceStore(connection, deployment);
        var workspaceId = $"workspace-{Guid.NewGuid():N}";
        var keys = new InMemoryAdminApiKeyStore();
        var issued = await keys.CreateAsync("admission-agent", ["agent:workspace", "admin:read"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(issued.Record);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var humanIdentity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human-1")], "Bearer");
        CanonicalSecurityActor.StampAuthorityClaims(humanIdentity);
        var human = new ClaimsPrincipal(humanIdentity);
        var humanActor = CanonicalSecurityActor.Resolve(human)!.ActorId;
        var caller = contribution ? human : principal;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        var turns = Substitute.For<IAgentWorkspaceTurnAuthorizer>();
        var contributors = Substitute.For<IDatasetImportContributorAuthorizer>();
        using var services = new ServiceCollection().AddSingleton(turns).AddSingleton(contributors).AddSingleton<IAdminApiKeyStore>(keys).AddSingleton(tenant)
            .AddSingleton<TimeProvider>(TimeProvider.System).BuildServiceProvider();
        var context = new DefaultHttpContext { User = caller, RequestServices = services };
        var target = await store.GetTargetIdAsync();
        turns.AuthorizeAsync(principal, workspaceId, "turn-1", Arg.Any<CancellationToken>())
            .Returns(new AgentWorkspaceTurnAuthorization(workspaceId, "turn-1", target, "tenant-1", "test-release", humanActor));
        var task = new DatasetImportTask("import-task-1", 1, string.Empty, actor, "tenant-1", target, workspaceId,
            new("public", "new_table"), new("browser-file", "large.geojson", 5L * 1024 * 1024 * 1024, new string('a', 64)),
            new(), DateTimeOffset.UtcNow.AddHours(24));
        task = task with { ContentHash = DatasetImportTaskState.ContentHash(task) };
        var turn = new AgentWorkspaceTurn("turn-1", humanActor, "Import selected source",
            new(workspaceId, target, "tab-1", 0, [], null, "tenant-1"), null);
        var snapshot = (await AgentCredentialAuthority.CaptureAsync(principal, services, default))!;
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot, AgentWorkspaceJsonContext.Default.AgentCredentialSnapshot)));
        var turnHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn)));
        var policy = new JobSecurityContext(actor, "tenant-1", [], "role") { OwnerActorId = actor, AgentCredential = snapshot };
        var binding = new DatasetImportExecutionBinding(task.TaskId, 1, task.ContentHash, 1, fingerprint, actor,
            humanActor, turn.TurnId, turnHash, policy, JobSecurityContextFingerprint.Compute(policy), snapshot);
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = workspaceId, TargetId = target, OwnerId = humanActor, AgentActorId = actor,
            Members = [actor, humanActor], TenantId = "tenant-1",
            Turns = new() { [turn.TurnId] = turn }, DatasetImportTasks = new() { [task.TaskId] = [task] },
            DatasetImportExecutionBindings = new() { [task.TaskId] = binding },
        };
        var tasks = Substitute.For<IDatasetImportTaskProvider>();
        tasks.AuthorizeAsync(principal, workspaceId, task.TaskId, 1, task.ContentHash, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(task);
        contributors.AuthorizeAsync(human, workspaceId, task.TaskId, 1, task.ContentHash, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new DatasetImportContribution(task, actor, humanActor, humanActor, turn.TurnId, turnHash, 1, fingerprint, binding.PolicyContextFingerprint));
        try
        {
            (await store.CreateAsync(document)).Should().BeTrue();
            var paused = new PausingStore(store);
            var provider = new DatasetImportAdmissionProvider(paused, tasks, new HttpContextAccessor { HttpContext = context });
            var preparation = contribution
                ? provider.PrepareContributionAsync(caller, workspaceId, task.TaskId, 1, task.ContentHash, 1, fingerprint)
                : provider.PrepareAsync(caller, workspaceId, task.TaskId, 1, task.ContentHash, 1, fingerprint);
            await paused.Ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stale = (await store.GetAsync(workspaceId))!;
            var nextAuthority = stale with { DatasetImportExecutionBindings = new(stale.DatasetImportExecutionBindings)
                { [task.TaskId] = binding with { AuthorityRevision = 2 } } };
            if (authorityWins) (await store.SaveAsync(nextAuthority)).Should().BeTrue();
            paused.Continue.SetResult();
            if (authorityWins) await FluentActions.Awaiting(async () => await preparation).Should().ThrowAsync<AgentWorkspaceConflictException>();
            else
            {
                var prepared = await preparation;
                prepared!.AuthorityRevision.Should().Be(1);
                prepared.OperationId.Should().MatchRegex("\\A[0-9a-f]{32}\\z");
                prepared.AcceptBefore.Should().Be(task.UploadAdmissionDeadline);
                (await store.SaveAsync(nextAuthority)).Should().BeFalse();
            }
            await using var restartedConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
            var restarted = new AgentWorkspaceStore(restartedConnection, deployment);
            var recovered = (await restarted.GetAsync(workspaceId))!;
            recovered.DatasetImportTasks[task.TaskId].Single().Should().Be(task);
            recovered.DatasetImportExecutionBindings[task.TaskId].AuthorityRevision.Should().Be(authorityWins ? 2 : 1);
            if (authorityWins) recovered.DatasetImportPreparedAdmissions.Should().BeEmpty();
            else
            {
                var replayProvider = new DatasetImportAdmissionProvider(restarted, tasks, new HttpContextAccessor { HttpContext = context });
                var replay = contribution
                    ? await replayProvider.PrepareContributionAsync(caller, workspaceId, task.TaskId, 1, task.ContentHash, 1, fingerprint)
                    : await replayProvider.PrepareAsync(caller, workspaceId, task.TaskId, 1, task.ContentHash, 1, fingerprint);
                replay!.OperationId.Should().Be(recovered.DatasetImportPreparedAdmissions[task.TaskId].OperationId);
                replay.ExecutorActorId.Should().Be(actor);
                replay.InitiatingHumanActorId.Should().Be(humanActor);
                (await restarted.GetAsync(workspaceId))!.Version.Should().Be(recovered.Version);
            }
        }
        finally
        {
            await connection.GetDatabase().KeyDeleteAsync($"agent-workspace:{deployment}:document:{workspaceId}");
            await connection.GetDatabase().KeyDeleteAsync($"agent-workspace:{deployment}:target");
        }
    }

    private sealed class PausingStore(IAgentWorkspaceStore inner) : IAgentWorkspaceStore
    {
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsAvailable => inner.IsAvailable;
        public Task<string> GetTargetIdAsync() => inner.GetTargetIdAsync();
        public Task<AgentWorkspaceDocument?> GetAsync(string id) => inner.GetAsync(id);
        public Task<bool> CreateAsync(AgentWorkspaceDocument document) => inner.CreateAsync(document);
        public async Task<bool> SaveAsync(AgentWorkspaceDocument document)
        {
            Ready.TrySetResult();
            await Continue.Task;
            return await inner.SaveAsync(document);
        }
    }
}
