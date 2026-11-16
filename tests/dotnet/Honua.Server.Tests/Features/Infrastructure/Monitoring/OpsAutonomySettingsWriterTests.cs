// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Db.Postgres.Features.Observability;
using Honua.Server.Tests.Infrastructure;
using Honua.Core.Features.Observability.Domain;
using Honua.Core.Features.Observability.Services;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Collection("Database")]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class OpsAutonomySettingsWriterTests : IAsyncLifetime
{
    private readonly PostgresFixture _postgres = new();
    private readonly InMemoryOpsAutonomyPolicyStore _store = new();

    public Task InitializeAsync() => _postgres.InitializeAsync();
    public Task DisposeAsync() => _postgres.DisposeAsync();

    [IntegrationTest]
    public async Task SetAsync_StopWatchdogFails_ServerRemainsStoppedAndFailureIsVisible()
    {
        var watchdog = new RecordingWatchdog
        {
            Suspend = async () =>
            {
                Assert.True((await _store.GetSettingsAsync()).KillSwitchEnabled);
                throw new IOException("mode update failed");
            },
        };
        var writer = new OpsAutonomySettingsWriter(_postgres.DataSource, _store, watchdog);

        await Assert.ThrowsAsync<IOException>(() => writer.SetAsync(StopChange(), CancellationToken.None));

        Assert.True((await _store.GetSettingsAsync()).KillSwitchEnabled);
        Assert.Equal(1, watchdog.SuspendCalls);
    }

    [IntegrationTest]
    public async Task SetAsync_RestoreWatchdogFails_ServerRemainsStopped()
    {
        await _store.SetSettingsAsync(new OpsAutonomySettings { KillSwitchEnabled = true }, "operator");
        var watchdog = new RecordingWatchdog { Resume = () => throw new IOException("mode update failed") };
        var writer = new OpsAutonomySettingsWriter(_postgres.DataSource, _store, watchdog);

        await Assert.ThrowsAsync<IOException>(() => writer.SetAsync(RestoreChange(), CancellationToken.None));

        Assert.True((await _store.GetSettingsAsync()).KillSwitchEnabled);
    }

    [IntegrationTest]
    public async Task SetAsync_RestoreResponseLost_SuspendsWatchdogWithFreshToken()
    {
        using var request = new CancellationTokenSource();
        var mode = "Off";
        var watchdog = Substitute.For<IOpsWatchdogKillSwitch>();
        watchdog.ResumeAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            mode = "Act";
            request.Cancel();
            throw new OperationCanceledException(request.Token);
        });
        watchdog.SuspendAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            Assert.False(call.Arg<CancellationToken>().IsCancellationRequested);
            mode = "Off";
            return Task.CompletedTask;
        });
        await _store.SetSettingsAsync(new OpsAutonomySettings { KillSwitchEnabled = true }, "operator");
        var writer = new OpsAutonomySettingsWriter(_postgres.DataSource, _store, watchdog);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.SetAsync(RestoreChange(), request.Token));

        Assert.Equal("Off", mode);
        Assert.True((await _store.GetSettingsAsync()).KillSwitchEnabled);
    }

    [IntegrationTest]
    public async Task SetAsync_AuditCancelledAfterRestoreCommit_ReassertsDurableStop()
    {
        using var request = new CancellationTokenSource();
        var audit = Substitute.For<IAuditLog>();
        var store = new PostgresOpsAutonomyPolicyStore(new TestDatabaseConnectionProvider(_postgres.DataSource), audit);
        await store.SetSettingsAsync(new OpsAutonomySettings { KillSwitchEnabled = true }, "operator");
        CancelRequestWhenAuditRecords(audit, request, "false");
        var watchdog = new RecordingWatchdog();
        var writer = new OpsAutonomySettingsWriter(_postgres.DataSource, store, watchdog);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.SetAsync(RestoreChange(), request.Token));

        Assert.True((await store.GetSettingsAsync()).KillSwitchEnabled);
        Assert.Equal(1, watchdog.SuspendCalls);
    }

    [IntegrationTest]
    public async Task SetAsync_AuditCancelledAfterStopCommit_StillSuspendsWatchdog()
    {
        using var request = new CancellationTokenSource();
        var audit = Substitute.For<IAuditLog>();
        var store = new PostgresOpsAutonomyPolicyStore(new TestDatabaseConnectionProvider(_postgres.DataSource), audit);
        CancelRequestWhenAuditRecords(audit, request, "true");
        var watchdog = new RecordingWatchdog();
        var writer = new OpsAutonomySettingsWriter(_postgres.DataSource, store, watchdog);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.SetAsync(StopChange(), request.Token));

        Assert.True((await store.GetSettingsAsync()).KillSwitchEnabled);
        Assert.Equal(1, watchdog.SuspendCalls);
    }

    [IntegrationTest]
    public async Task SetAsync_RestoreRefusedBeforeStoreWrite_KeepsOriginalStopAuthor()
    {
        var store = Substitute.For<IOpsAutonomyPolicyStore>();
        var watchdog = new RecordingWatchdog { Resume = () => throw new IOException("access denied") };
        var writer = new OpsAutonomySettingsWriter(_postgres.DataSource, store, watchdog);

        await Assert.ThrowsAsync<IOException>(() => writer.SetAsync(RestoreChange(), CancellationToken.None));

        await store.DidNotReceiveWithAnyArgs().SetSettingsAsync(default!, default!, default, default);
        Assert.Equal(1, watchdog.SuspendCalls);
    }

    [IntegrationTest]
    public async Task SetAsync_RestorePersistenceFails_SuspendsWatchdogAgain()
    {
        var store = Substitute.For<IOpsAutonomyPolicyStore>();
        store.SetSettingsAsync(Arg.Is<OpsAutonomySettings>(settings => !settings.KillSwitchEnabled), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<OpsAutonomySettings>>(_ => throw new TimeoutException("database unavailable"));
        var watchdog = new RecordingWatchdog();
        var writer = new OpsAutonomySettingsWriter(_postgres.DataSource, store, watchdog);

        await Assert.ThrowsAsync<TimeoutException>(() => writer.SetAsync(RestoreChange(), CancellationToken.None));

        Assert.Equal(1, watchdog.ResumeCalls);
        Assert.Equal(1, watchdog.SuspendCalls);
    }

    [IntegrationTest]
    public async Task SetAsync_ConcurrentTaskUpdates_CompleteInDatabaseLockOrder()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchdog = new RecordingWatchdog
        {
            Suspend = async () => { entered.SetResult(); await release.Task; },
            Resume = () => { Assert.True(release.Task.IsCompleted); return Task.CompletedTask; },
        };
        var first = new OpsAutonomySettingsWriter(_postgres.DataSource, _store, watchdog);
        var second = new OpsAutonomySettingsWriter(_postgres.DataSource, _store, watchdog);
        var stopping = first.SetAsync(StopChange(), CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var restoring = second.SetAsync(RestoreChange(), CancellationToken.None);
            await Task.Delay(100);
            Assert.False(restoring.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(stopping, restoring).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False((await _store.GetSettingsAsync()).KillSwitchEnabled);
            Assert.Equal(1, watchdog.SuspendCalls);
            Assert.Equal(1, watchdog.ResumeCalls);
        }
        finally
        {
            release.TrySetResult();
            await stopping;
        }
    }

    [IntegrationTest]
    public async Task ReconcileAsync_DurableStop_SuspendsWatchdogWithoutResumingIt()
    {
        await _store.SetSettingsAsync(new OpsAutonomySettings { KillSwitchEnabled = true }, "operator");
        var watchdog = new RecordingWatchdog();
        var writer = new OpsAutonomySettingsWriter(_postgres.DataSource, _store, watchdog);

        await writer.ReconcileAsync(new OpsAutonomySettings(), CancellationToken.None);

        Assert.Equal(1, watchdog.SuspendCalls);
        Assert.Equal(0, watchdog.ResumeCalls);
    }

    [IntegrationTest]
    public async Task ReconcileAsync_NoServerStop_DoesNotOverrideManualWatchdogMode()
    {
        var watchdog = new RecordingWatchdog();
        var writer = new OpsAutonomySettingsWriter(_postgres.DataSource, _store, watchdog);

        await writer.ReconcileAsync(new OpsAutonomySettings(), CancellationToken.None);

        Assert.Equal(0, watchdog.SuspendCalls);
        Assert.Equal(0, watchdog.ResumeCalls);
    }

    [IntegrationTest]
    public async Task ReconcileAsync_ConfiguredStop_SuspendsWatchdogWithoutPersistingSharedStop()
    {
        var watchdog = new RecordingWatchdog();
        var writer = new OpsAutonomySettingsWriter(_postgres.DataSource, _store, watchdog);

        await writer.ReconcileAsync(new OpsAutonomySettings { KillSwitchEnabled = true }, CancellationToken.None);

        Assert.False((await _store.GetSettingsAsync()).KillSwitchEnabled);
        Assert.Equal(1, watchdog.SuspendCalls);
        Assert.Equal(0, watchdog.ResumeCalls);
    }

    [IntegrationTest]
    public async Task SetAsync_StopRequestAbortedAfterCommit_StillSuspendsWatchdog()
    {
        using var request = new CancellationTokenSource();
        var store = Substitute.For<IOpsAutonomyPolicyStore>();
        store.SetSettingsAsync(Arg.Any<OpsAutonomySettings>(), Arg.Any<string>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                request.Cancel();
                return new OpsAutonomySettings { KillSwitchEnabled = true };
            });
        var suspendTokenCancelled = true;
        var watchdog = Substitute.For<IOpsWatchdogKillSwitch>();
        watchdog.SuspendAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            suspendTokenCancelled = call.Arg<CancellationToken>().IsCancellationRequested;
            return Task.CompletedTask;
        });
        var writer = new OpsAutonomySettingsWriter(_postgres.DataSource, store, watchdog);

        try
        {
            await writer.SetAsync(StopChange(), request.Token);
        }
        catch (OperationCanceledException)
        {
            // The response may be lost; the watchdog stop must still have been sent.
        }

        await watchdog.Received(1).SuspendAsync(Arg.Any<CancellationToken>());
        Assert.False(suspendTokenCancelled);
    }

    [IntegrationTest]
    public async Task StartupReconciliation_UnexpectedFailure_RetriesWithoutStoppingHost()
    {
        await _store.SetSettingsAsync(new OpsAutonomySettings { KillSwitchEnabled = true }, "operator");
        var attempts = 0;
        var suspended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchdog = new RecordingWatchdog
        {
            Suspend = () =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("credentials endpoint unavailable");
                }

                suspended.TrySetResult();
                return Task.CompletedTask;
            },
        };
        await using var services = new ServiceCollection()
            .AddScoped(_ => new OpsAutonomySettingsWriter(_postgres.DataSource, _store, watchdog))
            .BuildServiceProvider();
        using var reconciliation = new OpsWatchdogStartupReconciliation(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OpsAutonomyOptions()),
            NullLogger<OpsWatchdogStartupReconciliation>.Instance);

        await reconciliation.StartAsync(CancellationToken.None);
        await suspended.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await reconciliation.StopAsync(CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.Null(reconciliation.ExecuteTask?.Exception);
    }

    private static void CancelRequestWhenAuditRecords(IAuditLog audit, CancellationTokenSource request, string committedValue)
        => audit.RecordAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            if (!call.Arg<AuditEvent>().Details!.Contains(committedValue, StringComparison.Ordinal))
            {
                return Task.FromResult<string?>("audit-id");
            }

            request.Cancel();
            return Task.FromException<string?>(new OperationCanceledException(request.Token));
        });

    private static OpsAutonomySettingsChange StopChange() =>
        new(new OpsAutonomySettings { KillSwitchEnabled = true }, "operator", "incident control");

    private static OpsAutonomySettingsChange RestoreChange() =>
        new(new OpsAutonomySettings { KillSwitchEnabled = false }, "operator", "incident control");

    private sealed class RecordingWatchdog : IOpsWatchdogKillSwitch
    {
        internal Func<Task> Suspend { get; init; } = () => Task.CompletedTask;
        internal Func<Task> Resume { get; init; } = () => Task.CompletedTask;
        internal int SuspendCalls { get; private set; }
        internal int ResumeCalls { get; private set; }

        public Task SuspendAsync(CancellationToken cancellationToken)
        {
            SuspendCalls++;
            return Suspend();
        }

        public Task ResumeAsync(CancellationToken cancellationToken)
        {
            ResumeCalls++;
            return Resume();
        }
    }
}
