// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Orchestration.Domain;
using Honua.Server.Features.Orchestration;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Orchestration;

/// <summary>Real Redis qualification of atomic workflow-command acceptance and expiry behavior.</summary>
[Collection("Redis")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ProcessExecution)]
public sealed class RedisWorkflowRunStoreTests(RedisFixture redis)
{
    [IntegrationTest]
    public async Task CommandAcceptance_IsAtomicAcrossWriters_AndOutlivesRunExpiry()
    {
        using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var scope = Guid.NewGuid().ToString("N");
        var run = new WorkflowRun
        {
            RunId = $"wf-{scope}", WorkflowId = $"definition-{scope}", Status = WorkflowRunStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, StepStates = []
        };
        var command = new WorkflowRunCommand(run.RunId, "immutable-input-hash");
        var writers = Enumerable.Range(0, 8).Select(_ => new RedisWorkflowRunStore(connection));
        var accepted = await Task.WhenAll(writers.Select(store => store.TryCreateIdempotentAsync(run, scope, command)));
        Assert.Single(accepted, value => value);
        var database = connection.GetDatabase();
        Assert.Null(await database.KeyTimeToLiveAsync($"orchestration:run:command:{scope}"));
        Assert.NotNull(await database.KeyTimeToLiveAsync($"orchestration:run:{run.RunId}"));

        // Reopen through an independent store and simulate ordinary run-detail expiry.
        var reopened = new RedisWorkflowRunStore(connection);
        Assert.Equal(command, await reopened.GetCommandAsync(scope));
        await database.KeyDeleteAsync($"orchestration:run:{run.RunId}");
        Assert.Null(await reopened.GetAsync(run.RunId));
        Assert.Equal(command, await reopened.GetCommandAsync(scope));
        Assert.False(await reopened.TryCreateIdempotentAsync(run, scope, command));
        Assert.Null(await reopened.GetAsync(run.RunId));
    }
}
