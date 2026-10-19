// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// State/ownership/CAS tests. PostgreSQL decision-lock/receipt ordering is covered
// separately by the actual importer and receipt-reader integration tests.
public sealed class ImportCommitReconciliationTests
{
    [UnitTest]
    public async Task ReceiptOnlyClaims_DoNotConsumeExecutionRetryBudgetOrLoseDeferredFailure()
    {
        var fixture = new Fixture();
        var started = await ExecutionJobCommitReconciliation.SetPendingAsync(fixture.Store, fixture.Current, true, default, startExecution: true);
        Assert.Equal(1, started.ImportExecutionAttemptCount);
        Assert.True(await ExecutionJobCommitReconciliation.RequeueAsync(fixture.Store, fixture.Queue, started, default,
            ExecutionJobStatus.Failed, retryAllowed: false, reason: "Nonretryable importer failure."));
        fixture.Current = fixture.Current with { ClaimedBy = "replacement", AttemptCount = 12, Status = ExecutionJobStatus.Running };
        var adopted = await ExecutionJobCommitReconciliation.SetPendingAsync(fixture.Store, fixture.Current, true, default);
        Assert.Equal(1, adopted.ImportExecutionAttemptCount);
        Assert.True(adopted.CommitRecovery!.ExecutionStarted);
        Assert.False(adopted.CommitRecovery.RetryAllowed);
        Assert.Equal(ExecutionJobStatus.Failed, adopted.CommitRecovery.DeferredStatus);
        var absent = await ExecutionJobCommitReconciliation.SetPendingAsync(fixture.Store, adopted, false, default);
        Assert.Null(absent.CommitRecovery);
        Assert.Equal(1, absent.ImportExecutionAttemptCount);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementClaim_PreventsStaleWorkerFromClearingUnknownCommit(bool replaceWorker)
    {
        var fixture = new Fixture();
        var old = await ExecutionJobCommitReconciliation.SetPendingAsync(fixture.Store, fixture.Current, true, default, startExecution: true);
        fixture.Current = replaceWorker ? fixture.Current with { ClaimedBy = "other" }
            : fixture.Current with { AttemptCount = 2 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExecutionJobCommitReconciliation.SetPendingAsync(fixture.Store, old, false, default));
        Assert.False(await ExecutionJobCommitReconciliation.RequeueAsync(fixture.Store, fixture.Queue, old, default));
        Assert.NotNull(fixture.Current.CommitRecovery);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleHeartbeatOrTimeout_PreservesCancelledPendingImportForReceiptOnlyRecovery(bool timedOut)
    {
        var fixture = new Fixture();
        await ExecutionJobCommitReconciliation.SetPendingAsync(fixture.Store, fixture.Current, true, default, startExecution: true);
        fixture.Current = fixture.Current with { CancellationRequestedAt = DateTimeOffset.UtcNow,
            ClaimedAt = DateTimeOffset.UtcNow.AddHours(-2), LastHeartbeatAt = DateTimeOffset.UtcNow.AddHours(-1),
            TimeoutPolicy = new() { MaxDuration = timedOut ? TimeSpan.FromMinutes(1) : TimeSpan.FromDays(1) } };
        using var service = new JobReconciliationService(fixture.Store, fixture.Queue, Substitute.For<IQueueClaimReconciler>(),
            new ExecutionJobCancellationTokens(), [], null, NullLogger<JobReconciliationService>.Instance);
        await service.SweepActiveJobsAsync(default);
        Assert.Equal(ExecutionJobStatus.Queued, fixture.Current.Status);
        Assert.NotNull(fixture.Current.CommitRecovery);
        Assert.NotNull(fixture.Current.CancellationRequestedAt);
        Assert.Null(fixture.Current.CompletedAt);
        Assert.Null(fixture.Current.ClaimedBy);
    }

    [UnitTest]
    public async Task ProviderAndCancelPaths_CannotErasePendingDecisionEvenAfterLicenseExpiry()
    {
        var fixture = new Fixture();
        await ExecutionJobCommitReconciliation.SetPendingAsync(fixture.Store, fixture.Current, true, default, startExecution: true);
        var merged = ExecutionJobCancellationHelper.MergeBackendCancelObservation(fixture.Current,
            new() { Status = ExecutionJobStatus.Cancelled }, DateTimeOffset.UtcNow);
        Assert.Equal(ExecutionJobStatus.Running, merged.Status);
        Assert.NotNull(merged.CancellationRequestedAt);
        fixture.Current = merged with { Status = ExecutionJobStatus.Queued, ClaimedBy = null };
        _ = await ExecutionJobCancellationHelper.TryCancelPreSubmissionAsync(fixture.Store, fixture.Current);
        Assert.Equal(ExecutionJobStatus.Queued, fixture.Current.Status);
        using var expired = new CancellationTokenSource();
        expired.Cancel();
        var license = Substitute.For<ILicenseOperationPolicy>();
        license.OperationCancellation.Returns(expired.Token);
        fixture.Store.TryAcquireLeaseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        var backend = Substitute.For<IBatchComputeBackend>();
        var reconciler = new ExecutionJobReconciler(fixture.Store, [backend], Substitute.For<IUniversalProgressStore>(),
            NullLogger<ExecutionJobReconciler>.Instance, license);
        await reconciler.ReconcileExecutionJobAsync(fixture.Current.OperationId);
        Assert.Equal(ExecutionJobStatus.Queued, fixture.Current.Status);
        Assert.NotNull(fixture.Current.CommitRecovery);
        await backend.DidNotReceive().StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>());
    }

    private sealed class Fixture
    {
        internal IExecutionJobStore Store { get; } = Substitute.For<IExecutionJobStore>();
        internal IJobQueue Queue { get; } = Substitute.For<IJobQueue>();
        internal ExecutionJobRecord Current { get; set; }
        internal Fixture()
        {
            var now = DateTimeOffset.UtcNow;
            var id = new string('a', 32);
            // Lease metadata is controlled; no authentication or execution authority
            // is claimed by these persistence-only state transition tests.
            Current = new() { OperationId = id, Status = ExecutionJobStatus.Running, CreatedAt = now, UpdatedAt = now,
                ClaimedBy = "worker", ClaimedAt = now, LastHeartbeatAt = now, AttemptCount = 1, RetryPolicy = JobRetryPolicy.None,
                Spec = new() { Kind = ExecutionJobKind.FileImport, Backend = "local", TargetKind = BatchComputeTargetKind.KubernetesJob, WorkloadName = "import" },
                Audit = new() { SubmitterSecurityContext = new("actor", null, []) { ExecutionLease = new()
                    { LeaseId = "lease", JobId = id, IssuedAt = now, ExpiresAt = now.AddHours(1), ProtectedAuthority = "controlled" } } } };
            Store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Current);
            Store.ListActiveAsync(Arg.Any<ExecutionJobKind?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<ExecutionJobRecord>)[Current]);
            Store.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                var next = call.Arg<ExecutionJobRecord>();
                if (next.Version != Current.Version) return false;
                Current = next with { Version = next.Version + 1 };
                return true;
            });
        }
    }
}
