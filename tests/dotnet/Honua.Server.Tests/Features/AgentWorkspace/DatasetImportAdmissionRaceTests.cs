// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationAndAuthorityWriterHaveOneWinnerAndRestartRecoversOriginal(bool authorityWins)
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var deployment = $"admission-test-{Guid.NewGuid():N}";
        var store = new AgentWorkspaceStore(connection, deployment);
        var workspaceId = $"workspace-{Guid.NewGuid():N}";
        var keys = new InMemoryAdminApiKeyStore();
        var issued = await keys.CreateAsync("admission-agent", ["agent:workspace", "admin:read"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(issued.Record);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        using var services = new ServiceCollection().AddSingleton<IAdminApiKeyStore>(keys).AddSingleton(tenant)
            .AddSingleton<TimeProvider>(TimeProvider.System).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        var target = await store.GetTargetIdAsync();
        var task = new DatasetImportTask("import-task-1", 1, string.Empty, actor, "tenant-1", target, workspaceId,
            new("public", "new_table"), new("browser-file", "large.geojson", 5L * 1024 * 1024 * 1024, new string('a', 64)),
            new(), DateTimeOffset.UtcNow.AddHours(24));
        task = task with { ContentHash = DatasetImportTaskState.ContentHash(task) };
        var turn = new AgentWorkspaceTurn("turn-1", "human-1", "Import selected source",
            new(workspaceId, target, "tab-1", 0, [], null, "tenant-1"), null);
        var snapshot = (await AgentCredentialAuthority.CaptureAsync(principal, services, default))!;
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot, AgentWorkspaceJsonContext.Default.AgentCredentialSnapshot)));
        var turnHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(turn, AgentWorkspaceJsonContext.Default.AgentWorkspaceTurn)));
        var binding = new DatasetImportExecutionBinding(task.TaskId, 1, task.ContentHash, 1, fingerprint, actor,
            "human-1", turn.TurnId, turnHash, snapshot);
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = workspaceId, TargetId = target, OwnerId = "human-1", AgentActorId = actor,
            Members = [actor, "human-1"], TenantId = "tenant-1",
            Turns = new() { [turn.TurnId] = turn }, DatasetImportTasks = new() { [task.TaskId] = [task] },
            DatasetImportExecutionBindings = new() { [task.TaskId] = binding },
        };
        var tasks = Substitute.For<IDatasetImportTaskProvider>();
        tasks.AuthorizeAsync(principal, workspaceId, task.TaskId, 1, task.ContentHash, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(task);
        try
        {
            (await store.CreateAsync(document)).Should().BeTrue();
            var paused = new PausingStore(store);
            var provider = new DatasetImportAdmissionProvider(paused, tasks, new HttpContextAccessor { HttpContext = context });
            var preparation = provider.PrepareAsync(principal, workspaceId, task.TaskId, 1, task.ContentHash, 1, fingerprint);
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
                var replay = await replayProvider.PrepareAsync(principal, workspaceId, task.TaskId, 1, task.ContentHash, 1, fingerprint);
                replay!.OperationId.Should().Be(recovered.DatasetImportPreparedAdmissions[task.TaskId].OperationId);
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
