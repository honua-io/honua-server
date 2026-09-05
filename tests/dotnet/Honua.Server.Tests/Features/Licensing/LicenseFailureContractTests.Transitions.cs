// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Honua.Server.Tests.Features.Licensing;

// Uses the pre-existing registration and execution boundaries so the identical tests
// compile against the baseline, without backporting any production implementation.
public sealed partial class LicenseFailureContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Tier", "Fast")]
    public async Task RuntimeExpiry_RegisteredWorker_CancelsAndFailsPublishedPartialOutput(bool expireDuringCommit)
    {
        var clock = new TransitionClock();
        var expires = clock.GetUtcNow().AddMinutes(2);
        var license = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro, expires);
        var services = LicensingServices(license, clock);
        var now = clock.GetUtcNow();
        var job = new ExecutionJobRecord
        {
            OperationId = "synthetic-expiry-boundary",
            Status = ExecutionJobStatus.Provisioning,
            CreatedAt = now,
            UpdatedAt = now,
            ClaimedBy = "test-worker",
            ClaimedAt = now,
            LastHeartbeatAt = now,
            AttemptCount = 1,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test"
            }
        };
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(_ => job);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                job = call.Arg<ExecutionJobRecord>();
                if (expireDuringCommit && job.Status == ExecutionJobStatus.Succeeded)
                {
                    clock.Advance(TimeSpan.FromMinutes(2));
                }
                return true;
            });
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        var cancelled = false;
        CancellationToken observedToken = default;
        executor.ExecuteAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                observedToken = token;
                await call.Arg<IJobExecutionContext>().PublishArtifactAsync("synthetic-partial-output", token);
                Assert.NotEmpty(job.ArtifactReferences);
                using var registration = token.Register(() => cancelled = true);
                if (!expireDuringCommit)
                {
                    clock.Advance(TimeSpan.FromMinutes(2));
                }
                // Challenge the completion guard too: a late executor may return success
                // even after receiving cancellation. The durable outcome must still fail.
                return JobExecutionResult.Succeeded();
            });
        services.AddSingleton(store);
        services.AddSingleton(Substitute.For<IJobQueue>());
        services.AddSingleton(Substitute.For<IExecutionLogStore>());
        services.AddSingleton(executor);
        services.AddSingleton<ExecutionJobCancellationTokens>();
        services.AddSingleton<JobExecutionService>();
        await using var provider = services.BuildServiceProvider();
        var licensing = provider.GetRequiredService<FileBackedLicenseService>();
        await licensing.StartAsync(CancellationToken.None);
        var worker = provider.GetRequiredService<JobExecutionService>();
        var method = typeof(JobExecutionService).GetMethod("ProcessJobAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)method.Invoke(worker, [job.OperationId, "test-worker", CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(observedToken.IsCancellationRequested, "License expiry must cancel the executor's in-flight token.");
        if (!expireDuringCommit)
        {
            Assert.True(cancelled);
        }
        Assert.Equal(ExecutionJobStatus.Failed, job.Status);
        Assert.Equal("license expired", job.ErrorMessage);
        Assert.Empty(job.ArtifactReferences);
        Assert.NotEqual(100, job.PercentComplete);
        Assert.False(licensing.GetSnapshot().HasEntitlement("temporal.filtering"));
        await licensing.StopAsync(CancellationToken.None);
    }

    [UnitTest]
    public async Task Renewal_RegisteredService_ReloadsChangedFileAtOneMinuteAndOnRestart()
    {
        var clock = new TransitionClock();
        var license = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro, clock.GetUtcNow().AddDays(31));
        var expiry = clock.GetUtcNow().AddDays(60);
        var renewed = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro, expiry);
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Join(directory.FullName, "synthetic-license.json");
        try
        {
            await File.WriteAllBytesAsync(path, license.LicenseData);
            await using (var provider = LicensingServices(license, clock, path).BuildServiceProvider())
            {
                var service = provider.GetRequiredService<FileBackedLicenseService>();
                await service.StartAsync(CancellationToken.None);
                await File.WriteAllBytesAsync(path, renewed.LicenseData);
                clock.Advance(TimeSpan.FromMinutes(1));
                for (var attempt = 0; attempt < 100 && service.GetSnapshot().ExpiresAt != expiry; attempt++)
                {
                    await Task.Delay(50);
                }
                Assert.Equal(expiry, service.GetSnapshot().ExpiresAt);
                await service.StopAsync(CancellationToken.None);
            }
            await using var restarted = LicensingServices(renewed, new TransitionClock(), path).BuildServiceProvider();
            var restartedService = restarted.GetRequiredService<FileBackedLicenseService>();
            await restartedService.StartAsync(CancellationToken.None);
            Assert.Equal(expiry, restartedService.GetSnapshot().ExpiresAt);
            Assert.Equal(HonuaEdition.Pro, restartedService.GetSnapshot().Edition);
            await restartedService.StopAsync(CancellationToken.None);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ServiceCollection LicensingServices(SignedLicenseTestFile license, TimeProvider clock, string? path = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Licensing:Edition"] = "Pro",
            ["Licensing:LicensePath"] = path,
            ["Licensing:LicenseContent"] = path is null ? System.Text.Encoding.UTF8.GetString(license.LicenseData) : null,
            [$"Licensing:TrustedKeys:{LicenseTestSupport.KeyId}"] = license.PublicKeySetting
        }).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Test");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(clock);
        services.AddHonuaLicensing(config, environment);
        return services;
    }

    internal sealed class TransitionClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly List<TransitionTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TransitionTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan time)
        {
            _now += time;
            foreach (var timer in _timers.ToArray())
            {
                timer.Fire();
            }
        }
        private sealed class TransitionTimer(TransitionClock clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset? _due;
            private TimeSpan _period;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
                _period = period;
                return true;
            }
            public void Fire()
            {
                if (_due <= clock.GetUtcNow())
                {
                    Change(_period, _period);
                    callback(state);
                }
            }
            public void Dispose() => _due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
