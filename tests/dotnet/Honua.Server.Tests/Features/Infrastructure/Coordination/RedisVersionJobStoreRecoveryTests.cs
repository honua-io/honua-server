// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Coordination;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.Coordination;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.TestQuality)]
public sealed class RedisVersionJobStoreRecoveryTests(RedisFixture fixture) : IAsyncLifetime
{
    private IConnectionMultiplexer _connection = null!;
    private RedisFaults _faults = null!;
    private RedisVersionJobStore _owner = null!;
    private RedisVersionJobStore _replica = null!;

    public async Task InitializeAsync()
    {
        _connection = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        _faults = new RedisFaults(_connection.GetDatabase());
        _owner = Store(_faults.Connection);
        _replica = Store(_connection);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetAsync_TerminalSaveFallsBack_RecoversTerminalStateForEveryReplica()
    {
        var running = Job(VersionJobStatus.Running);
        await _owner.SaveAsync(running);
        var terminal = running with { Status = VersionJobStatus.Succeeded, CompletedAt = DateTimeOffset.UtcNow, AppliedChanges = 42 };
        _faults.WriteUnavailable = true;
        await _owner.SaveAsync(terminal);
        _faults.ReadUnavailable = true;
        Assert.Equal(terminal, await _owner.GetAsync(running.JobId));

        _faults.ReadUnavailable = false;
        _faults.WriteUnavailable = false;
        Assert.Equal(terminal, await _owner.GetAsync(running.JobId));
        Assert.Equal(terminal, await _replica.GetAsync(running.JobId));
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetAsync_DistributedTerminalWins_RetainsThatWinnerDuringAnotherOutage()
    {
        var running = Job(VersionJobStatus.Running);
        await _owner.SaveAsync(running);
        _faults.WriteUnavailable = true;
        await _owner.SaveAsync(running with { Status = VersionJobStatus.Succeeded, CompletedAt = DateTimeOffset.UtcNow });
        _faults.WriteUnavailable = false;
        var authoritative = running with { Status = VersionJobStatus.Failed, CompletedAt = DateTimeOffset.UtcNow, ErrorMessage = "Authoritative completion" };
        await _replica.SaveAsync(authoritative);

        Assert.Equal(authoritative, await _owner.GetAsync(running.JobId));
        _faults.ReadUnavailable = true;
        Assert.Equal(authoritative, await _owner.GetAsync(running.JobId));
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetAsync_CompletionWinsDuringRecovery_DoesNotOverwriteTheDistributedWinner()
    {
        var running = Job(VersionJobStatus.Running);
        await _owner.SaveAsync(running);
        _faults.WriteUnavailable = true;
        await _owner.SaveAsync(running with { Status = VersionJobStatus.Succeeded, CompletedAt = DateTimeOffset.UtcNow });
        _faults.WriteUnavailable = false;
        var authoritative = running with { Status = VersionJobStatus.Failed, CompletedAt = DateTimeOffset.UtcNow };
        _faults.BeforeCompareExchange = () => _replica.SaveAsync(authoritative);

        Assert.Equal(authoritative, await _owner.GetAsync(running.JobId));
        Assert.Equal(authoritative, await _replica.GetAsync(running.JobId));
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task SaveAsync_RecoveryHasDistributedTerminal_RejectsAnOlderLocalRunningWrite()
    {
        var pending = Job(VersionJobStatus.Pending);
        _faults.WriteUnavailable = true;
        await _owner.SaveAsync(pending);
        _faults.WriteUnavailable = false;
        var authoritative = pending with { Status = VersionJobStatus.Failed, CompletedAt = DateTimeOffset.UtcNow };
        await _replica.SaveAsync(authoritative);

        await _owner.SaveAsync(pending with { Status = VersionJobStatus.Running, StartedAt = DateTimeOffset.UtcNow });

        Assert.Equal(authoritative, await _replica.GetAsync(pending.JobId));
        Assert.Equal(authoritative, await _owner.GetAsync(pending.JobId));
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task SaveAsync_JobAdmittedDuringOutage_IsLocalUntilAHealthySave()
    {
        var pending = Job(VersionJobStatus.Pending);
        _faults.WriteUnavailable = true;
        await _owner.SaveAsync(pending);

        Assert.Equal(pending, await _owner.GetAsync(pending.JobId));
        Assert.Null(await _replica.GetAsync(pending.JobId));
        var replacement = Store(_faults.Connection);
        Assert.Null(await replacement.GetAsync(pending.JobId));

        _faults.WriteUnavailable = false;
        Assert.Equal(pending, await _owner.GetAsync(pending.JobId));
        Assert.Null(await _replica.GetAsync(pending.JobId));
        await _owner.SaveAsync(pending);
        Assert.Equal(pending, await _replica.GetAsync(pending.JobId));
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetAsync_MissingRedisRecord_DoesNotPublishAnOldLocalFallback()
    {
        var prior = Job(VersionJobStatus.Succeeded) with { CreatedAt = DateTimeOffset.UtcNow.AddDays(-8) };
        _faults.WriteUnavailable = true;
        await _owner.SaveAsync(prior);
        _faults.WriteUnavailable = false;

        Assert.Equal(prior, await _owner.GetAsync(prior.JobId));
        Assert.Null(await _replica.GetAsync(prior.JobId));
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task SaveAsync_LongRunningJobCompletesDuringOutage_PersistsItsRecentCompletion()
    {
        var terminal = Job(VersionJobStatus.Succeeded) with { CreatedAt = DateTimeOffset.UtcNow.AddDays(-8) };
        _faults.WriteUnavailable = true;
        await _owner.SaveAsync(terminal);
        _faults.WriteUnavailable = false;

        await _owner.SaveAsync(terminal);
        Assert.Equal(terminal, await _owner.GetAsync(terminal.JobId));
        Assert.Equal(terminal, await _replica.GetAsync(terminal.JobId));
    }

    private static RedisVersionJobStore Store(IConnectionMultiplexer? connection)
        => new(connection, NullLogger<RedisVersionJobStore>.Instance);

    private static VersionJob Job(VersionJobStatus status)
        => new(Guid.NewGuid(), "version-service", Guid.NewGuid(), VersionJobKind.Reconcile, status,
            VersionReconcilePolicy.None, DateTimeOffset.UtcNow.AddMinutes(-2),
            StartedAt: status == VersionJobStatus.Pending ? null : DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt: status is VersionJobStatus.Succeeded or VersionJobStatus.Failed or VersionJobStatus.LockContended
                ? DateTimeOffset.UtcNow : null);

    private sealed class RedisFaults
    {
        public bool WriteUnavailable { get; set; }
        public bool ReadUnavailable { get; set; }
        public Func<Task>? BeforeCompareExchange { get; set; }
        public IConnectionMultiplexer Connection { get; } = Substitute.For<IConnectionMultiplexer>();

        public RedisFaults(IDatabase realDatabase)
        {
            var database = Substitute.For<IDatabase>();
            Connection.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);
            database.StringSetAsync(Arg.Any<RedisKey>(), Arg.Any<RedisValue>(), Arg.Any<TimeSpan?>(),
                    Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>())
                .Returns(call => WriteUnavailable
                    ? Task.FromException<bool>(Unavailable())
                    : realDatabase.StringSetAsync(call.ArgAt<RedisKey>(0), call.ArgAt<RedisValue>(1),
                        call.ArgAt<TimeSpan?>(2), call.ArgAt<bool>(3), call.ArgAt<When>(4), call.ArgAt<CommandFlags>(5)));
            database.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
                .Returns(call => ReadUnavailable
                    ? Task.FromException<RedisValue>(Unavailable())
                    : realDatabase.StringGetAsync(call.ArgAt<RedisKey>(0), call.ArgAt<CommandFlags>(1)));
            database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
                .Returns(async call =>
                {
                    if (WriteUnavailable || ReadUnavailable)
                    {
                        throw Unavailable();
                    }
                    if (BeforeCompareExchange is { } completeBeforeCompare)
                    {
                        BeforeCompareExchange = null;
                        await completeBeforeCompare();
                    }
                    return await realDatabase.ScriptEvaluateAsync(call.ArgAt<string>(0), call.ArgAt<RedisKey[]>(1),
                        call.ArgAt<RedisValue[]>(2), call.ArgAt<CommandFlags>(3));
                });
        }

        private static RedisConnectionException Unavailable()
            => new(ConnectionFailureType.UnableToConnect, "Injected Redis unavailability");
    }
}
