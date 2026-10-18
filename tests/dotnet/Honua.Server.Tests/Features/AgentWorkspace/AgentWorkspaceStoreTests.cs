// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.AgentWorkspace;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Admin)]
public sealed class AgentWorkspaceStoreTests(RedisFixture redis)
{
    [IntegrationTest]
    public async Task RestartRecoversCapturedTurnAndConflictingWritersCannotOverwriteIt()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var deploymentId = $"test-{Guid.NewGuid():N}";
        var first = new AgentWorkspaceStore(connection, deploymentId);
        var workspaceId = $"test-{Guid.NewGuid():N}";
        var targetId = await first.GetTargetIdAsync();
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = workspaceId, TargetId = targetId, OwnerId = "human-1", Members = ["human-1", "agent-1"], TenantId = "tenant-1",
        };
        document.Views["tab-1"] = [AgentWorkspaceState.Current(document, "tab-1")];
        try
        {
            (await first.CreateAsync(document)).Should().BeTrue();
            var stale = await first.GetAsync(workspaceId);
            var next = AgentWorkspaceState.AddTurn(document, "human-1", new("turn-1", "tab-1", 0, "Publish the selected layer"));
            (await first.SaveAsync(next)).Should().BeTrue();
            (await first.SaveAsync(stale! with { Messages = [] })).Should().BeFalse();

            // A new connection/store represents process restart after a committed response was lost.
            await using var restartedConnection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
            var restarted = new AgentWorkspaceStore(restartedConnection, deploymentId);
            (await restarted.GetTargetIdAsync()).Should().Be(targetId);
            var recovered = await restarted.GetAsync(workspaceId);
            recovered!.Turns.Should().ContainKey("turn-1");
            recovered.Messages.Should().ContainSingle();
            recovered.Version.Should().Be(1);
            AgentWorkspaceState.AddTurn(recovered, "human-1", new("turn-1", "tab-1", 0, "Publish the selected layer"))
                .Should().BeSameAs(recovered);
            (await restartedConnection.GetDatabase().KeyTimeToLiveAsync($"agent-workspace:{deploymentId}:document:{workspaceId}")).Should().BeNull();
        }
        finally
        {
            await connection.GetDatabase().KeyDeleteAsync($"agent-workspace:{deploymentId}:document:{workspaceId}");
            await connection.GetDatabase().KeyDeleteAsync($"agent-workspace:{deploymentId}:target");
        }
    }
    [IntegrationTest]
    public async Task DeploymentsSharingRedisCannotReadOrOverwriteEachOthersWorkspace()
    {
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var firstId = $"test-{Guid.NewGuid():N}";
        var secondId = $"test-{Guid.NewGuid():N}";
        var first = new AgentWorkspaceStore(connection, firstId);
        var second = new AgentWorkspaceStore(connection, secondId);
        const string workspaceId = "same-workspace-id";
        try
        {
            var firstTarget = await first.GetTargetIdAsync();
            var secondTarget = await second.GetTargetIdAsync();
            secondTarget.Should().NotBe(firstTarget);
            var document = new AgentWorkspaceDocument
            {
                WorkspaceId = workspaceId, TargetId = firstTarget, OwnerId = "same-human",
                Members = ["same-human"], TenantId = "same-tenant",
            };
            (await first.CreateAsync(document)).Should().BeTrue();
            (await second.GetAsync(workspaceId)).Should().BeNull();
            (await second.SaveAsync(document)).Should().BeFalse();
            (await second.CreateAsync(document with { TargetId = secondTarget })).Should().BeTrue();
            (await first.GetAsync(workspaceId))!.TargetId.Should().Be(firstTarget);
            (await second.GetAsync(workspaceId))!.TargetId.Should().Be(secondTarget);
            var unconfigured = new AgentWorkspaceStore(connection, null);
            unconfigured.IsAvailable.Should().BeFalse();
            await ((Func<Task<string>>)unconfigured.GetTargetIdAsync).Should().ThrowAsync<AgentWorkspaceUnavailableException>();
        }
        finally
        {
            foreach (var id in new[] { firstId, secondId })
            {
                await connection.GetDatabase().KeyDeleteAsync($"agent-workspace:{id}:target");
                await connection.GetDatabase().KeyDeleteAsync($"agent-workspace:{id}:document:{workspaceId}");
            }
        }
    }

}
