// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Licensing;

public sealed class LicenseRuntimeContractTests
{
    [UnitTest]
    public async Task WarningSchedule_EmitsThirtyFourteenSevenOneDayThresholdsOnce()
    {
        var clock = new LicenseFailureContractTests.TransitionClock();
        var license = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro, clock.GetUtcNow().AddDays(31));
        var logger = new WarningLogger();
        using var service = new FileBackedLicenseService(Options.Create(new LicenseOptions
        {
            Edition = HonuaEdition.Pro, LicenseContent = Encoding.UTF8.GetString(license.LicenseData),
            TrustedKeys = new() { [LicenseTestSupport.KeyId] = license.PublicKeySetting }
        }), new BouncyCastleEd25519Verifier(), logger, timeProvider: clock);
        await service.StartAsync(CancellationToken.None);
        Assert.Empty(logger.Warnings);
        foreach (var (advance, days) in new[] { (1, 30), (16, 14), (7, 7), (6, 1) })
        {
            clock.Advance(TimeSpan.FromDays(advance));
            await service.RevalidateAsync(CancellationToken.None);
            Assert.Contains(logger.Warnings, warning => warning.Contains($"{days}-day", StringComparison.Ordinal));
        }
        await service.RevalidateAsync(CancellationToken.None);
        Assert.Equal(4, logger.Warnings.Count);
        Assert.All(logger.Warnings, warning => Assert.Contains("backup/export before expiry", warning, StringComparison.Ordinal));
        await service.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Tier", "Fast")]
    public async Task Expiry_InFlightJob_CancelsAndFailsWithoutCompletedPartialOutputs(bool ignoreCancellation)
    {
        var clock = new LicenseFailureContractTests.TransitionClock();
        var license = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro, clock.GetUtcNow().AddMinutes(2));
        using var policy = CreateService(license, clock);
        await policy.StartAsync(CancellationToken.None);
        var now = clock.GetUtcNow();
        var job = new ExecutionJobRecord
        {
            OperationId = "synthetic-license-expiry-job", Status = ExecutionJobStatus.Provisioning,
            CreatedAt = now, UpdatedAt = now, ClaimedBy = "test-worker", ClaimedAt = now,
            LastHeartbeatAt = now, AttemptCount = 1,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing, TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local", WorkloadName = "test"
            }
        };
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(_ => job);
        store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => { job = call.Arg<ExecutionJobRecord>(); return true; });
        var queue = Substitute.For<IJobQueue>();
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Geoprocessing);
        var cancelled = false;
        executor.ExecuteAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var context = call.Arg<IJobExecutionContext>();
                var token = call.Arg<CancellationToken>();
                await context.PublishArtifactAsync("synthetic-partial-output", token);
                Assert.NotEmpty(job.ArtifactReferences);
                using var registration = token.Register(() => cancelled = true);
                clock.Advance(TimeSpan.FromMinutes(2));
                Assert.True(policy.IsBlocked);
                if (!ignoreCancellation)
                {
                    token.ThrowIfCancellationRequested();
                }
                return JobExecutionResult.Succeeded();
            });
        var worker = new JobExecutionService(queue, store, [executor], new ExecutionJobCancellationTokens(), [], null,
            NullLogger<JobExecutionService>.Instance, policy);
        var method = typeof(JobExecutionService).GetMethod("ProcessJobAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)method.Invoke(worker, [job.OperationId, "test-worker", CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(cancelled);
        Assert.Equal(ExecutionJobStatus.Failed, job.Status);
        Assert.Equal("license expired", job.ErrorMessage);
        Assert.Empty(job.ArtifactReferences);
        Assert.NotEqual(100, job.PercentComplete);
        Assert.Equal(HonuaEdition.Pro, policy.GetSnapshot().Edition);
        Assert.False(policy.GetSnapshot().HasEntitlement("temporal.filtering"));
        await policy.StopAsync(CancellationToken.None);
    }

    [UnitTest]
    public async Task Renewal_ChangedSource_RevalidatesOnIntervalAndSurvivesRestart()
    {
        var clock = new LicenseFailureContractTests.TransitionClock();
        var old = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro, clock.GetUtcNow().AddMinutes(2));
        var renewed = LicenseTestSupport.CreateSignedLicense(HonuaEdition.Pro, clock.GetUtcNow().AddDays(60));
        var directory = Directory.CreateTempSubdirectory();
        var path = Path.Join(directory.FullName, "synthetic-license.json");
        try
        {
            await File.WriteAllBytesAsync(path, old.LicenseData);
            var options = new LicenseOptions
            {
                Edition = HonuaEdition.Pro, LicensePath = path,
                TrustedKeys = new() { [LicenseTestSupport.KeyId] = old.PublicKeySetting }
            };
            using var service = new FileBackedLicenseService(Options.Create(options), new BouncyCastleEd25519Verifier(),
                NullLogger<FileBackedLicenseService>.Instance, timeProvider: clock);
            await service.StartAsync(CancellationToken.None);
            var originalToken = service.OperationCancellation;
            clock.Advance(TimeSpan.FromMinutes(2));
            Assert.True(service.IsBlocked);
            Assert.True(originalToken.IsCancellationRequested);
            await File.WriteAllBytesAsync(path, renewed.LicenseData);
            clock.Advance(TimeSpan.FromMinutes(1));
            await WaitUntilAsync(() => !service.IsBlocked);
            Assert.False(service.OperationCancellation.IsCancellationRequested);
            Assert.True(originalToken.IsCancellationRequested);
            await service.StopAsync(CancellationToken.None);

            using var restarted = new FileBackedLicenseService(Options.Create(options), new BouncyCastleEd25519Verifier(),
                NullLogger<FileBackedLicenseService>.Instance, timeProvider: clock);
            await restarted.StartAsync(CancellationToken.None);
            Assert.Equal(HonuaEdition.Pro, restarted.GetSnapshot().Edition);
            Assert.False(restarted.IsBlocked);
            await restarted.StopAsync(CancellationToken.None);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static FileBackedLicenseService CreateService(SignedLicenseTestFile license, TimeProvider clock)
        => new(Options.Create(new LicenseOptions
        {
            Edition = HonuaEdition.Pro, LicenseContent = Encoding.UTF8.GetString(license.LicenseData),
            TrustedKeys = new() { [LicenseTestSupport.KeyId] = license.PublicKeySetting }
        }), new BouncyCastleEd25519Verifier(), NullLogger<FileBackedLicenseService>.Instance, timeProvider: clock);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class WarningLogger : ILogger<FileBackedLicenseService>
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Warnings { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 10015)
            {
                Warnings.Enqueue(formatter(state, exception));
            }
        }
    }
}
