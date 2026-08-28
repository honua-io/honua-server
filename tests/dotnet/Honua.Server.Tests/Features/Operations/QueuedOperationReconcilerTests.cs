// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed class QueuedOperationReconcilerTests
{
    [UnitTest]
    public async Task SweepOnceAsync_BackendTerminal_LeasesAuditsAndCompletesEnvelope()
    {
        var store = new VolatileOperationInstanceStore();
        var queued = Handle();
        (await store.TryCreateAsync(queued)).Should().BeTrue();
        var audit = new RecordingAuditLog();
        var executor = new TerminalStatusExecutor();
        await using var services = new ServiceCollection()
            .AddScoped<IAuditLog>(_ => audit)
            .AddScoped<IOperationExecutor>(_ => executor)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var reconciler = new QueuedOperationReconciler(
            store,
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<QueuedOperationReconciler>.Instance);

        await reconciler.SweepOnceAsync();

        executor.StatusReads.Should().Be(1);
        var completed = await store.GetAsync(queued.OperationInstanceId);
        completed.Should().NotBeNull();
        completed!.Status.Should().Be(OperationHandleStatus.Completed);
        completed.AuditId.Should().Be("audit-reconciled");
        audit.Events.Should().ContainSingle(entry => entry.Action == "operation.completed");
    }

    [UnitTest]
    public async Task SweepOnceAsync_LeaseUnavailable_DoesNotPollBackend()
    {
        var store = Substitute.For<IOperationInstanceStore>();
        store.TryAcquireLeaseAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
        await using var services = new ServiceCollection().BuildServiceProvider();
        var reconciler = new QueuedOperationReconciler(
            store,
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<QueuedOperationReconciler>.Instance);

        await reconciler.SweepOnceAsync();

        await store.DidNotReceive().ListActiveAsync(Arg.Any<CancellationToken>());
    }

    private static OperationHandle Handle() => new()
    {
        OperationInstanceId = "opinst-queued-reconcile",
        OperationId = "test.async-operation",
        CorrelationId = "corr-queued-reconcile",
        AuditId = "audit-submitted",
        JobId = "job-1",
        Status = OperationHandleStatus.Queued,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class TerminalStatusExecutor : IOperationExecutor
    {
        public string OperationId => "test.async-operation";

        public int StatusReads { get; private set; }

        public Task<OperationValidation> ValidateAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OperationStatus> GetStatusAsync(
            OperationHandle handle,
            CancellationToken cancellationToken = default)
        {
            StatusReads++;
            return Task.FromResult(new OperationStatus
            {
                OperationInstanceId = handle.OperationInstanceId,
                OperationId = handle.OperationId,
                CorrelationId = handle.CorrelationId,
                AuditId = handle.AuditId,
                CreatedAt = handle.CreatedAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                Status = OperationHandleStatus.Completed,
                JobId = handle.JobId,
                Result = new OperationResultSummary { Summary = "Backend completed." },
            });
        }
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.FromResult<string?>("audit-reconciled");
        }
    }
}
