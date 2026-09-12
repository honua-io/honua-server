// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

internal static class StaleAttemptWorkerProgram
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 8)
        {
            Console.Error.WriteLine("Expected redis, operation, checkpoint, resume, rejected, receipt, postgres, schema.");
            return 2;
        }

        var redisConnectionString = args[0];
        var operationId = args[1];
        var checkpointKey = args[2];
        var resumeKey = args[3];
        var rejectedKey = args[4];
        var receiptKey = args[5];
        var postgresConnectionString = args[6];
        var schema = args[7];

        await using var redis = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
        var database = redis.GetDatabase();
        var jobStore = new RedisExecutionJobStore(redis, NullLogger<RedisExecutionJobStore>.Instance);
        var logStore = new RedisExecutionLogStore(redis, NullLogger<RedisExecutionLogStore>.Instance);
        var queue = new RedisJobQueue(redis, jobStore, NullLogger<RedisJobQueue>.Instance);
        var executor = new CheckpointingExecutor(
            database, checkpointKey, resumeKey, rejectedKey, receiptKey, postgresConnectionString, schema);
        var service = new JobExecutionService(
            queue, jobStore, [executor], new ExecutionJobCancellationTokens(), [], logStore,
            NullLogger<JobExecutionService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }
}

internal sealed class CheckpointingExecutor(
    IDatabase database,
    string checkpointKey,
    string resumeKey,
    string rejectedKey,
    string receiptKey,
    string postgresConnectionString,
    string schema) : IJobExecutor
{
    private readonly ExternalPostgisSinkExecutor _sink = new(
        new StaticOptionsMonitor(),
        new StaticConnectionResolver(postgresConnectionString),
        NullLogger<ExternalPostgisSinkExecutor>.Instance);

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var workerId = job.ClaimedBy ?? "unknown-worker";
        await database.HashIncrementAsync(receiptKey, $"invocations:{workerId}").ConfigureAwait(false);
        await database.HashIncrementAsync(receiptKey, "executorInvocations").ConfigureAwait(false);

        if (job.AttemptCount == 1)
        {
            await database.HashIncrementAsync(receiptKey, "executorCheckpointCount").ConfigureAwait(false);
            await database.StringSetAsync(checkpointKey, $"{workerId}|attempt={job.AttemptCount}").ConfigureAwait(false);
            while (!await database.KeyExistsAsync(resumeKey).ConfigureAwait(false))
            {
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
            }

            await database.StringSetAsync($"{checkpointKey}:resumed", "true").ConfigureAwait(false);
        }

        try
        {
            var parameters = new Dictionary<string, string>(job.Spec.Parameters, StringComparer.Ordinal)
            {
                [$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.batchId"] = $"{job.OperationId}:attempt:{job.AttemptCount}",
                [$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.schema"] = schema
            };
            var attemptJob = job with { Spec = job.Spec with { Parameters = parameters } };
            return await _sink.ExecuteAsync(attemptJob, context, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await database.StringSetAsync(rejectedKey, $"{workerId}|attempt={job.AttemptCount}").ConfigureAwait(false);
            throw;
        }
    }
}

internal sealed class StaticOptionsMonitor : IOptionsMonitor<GeoprocessingExecutorOptions>
{
    public GeoprocessingExecutorOptions CurrentValue { get; } = new();
    public GeoprocessingExecutorOptions Get(string? name) => CurrentValue;
    public IDisposable OnChange(Action<GeoprocessingExecutorOptions, string?> listener) => NoopDisposable.Instance;

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}

internal sealed class StaticConnectionResolver(string connectionString) : ISecureConnectionResolver
{
    public Task<string> ResolveConnectionStringAsync(string connectionName, CancellationToken cancellationToken = default)
        => Task.FromResult(connectionString);
    public Task<string> ResolveConnectionStringAsync(Guid connectionId, CancellationToken cancellationToken = default)
        => Task.FromResult(connectionString);
    public Task<bool> TestConnectionHealthAsync(string connectionName, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
    public Task<IReadOnlyList<string>> GetAvailableConnectionsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(["test"]);
}
