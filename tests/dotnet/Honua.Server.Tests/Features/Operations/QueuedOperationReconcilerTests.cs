// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin;
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
        completed.MetadataRevision.Should().Be(12);
        completed.ResourceIds["serviceName"].Should().Be("roads");
        completed.Result!.Details["serviceUri"].Should().Be("honua://published-services/roads");

        // A terminal envelope must retain its readback identity without polling
        // the backend again on subsequent sweeps.
        await reconciler.SweepOnceAsync();
        executor.StatusReads.Should().Be(1);
        (await store.GetAsync(queued.OperationInstanceId))!.MetadataRevision.Should().Be(12);
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

    [UnitTest]
    public async Task SweepOnceAsync_TenantOwnedCompletedJobs_ReconcileOnlyMatchingEnvelopes()
    {
        var store = new VolatileOperationInstanceStore();
        var tenantA = Handle() with
        {
            OperationInstanceId = "opinst-tenant-a",
            JobId = "job-tenant-a",
            TenantId = "tenant-a",
        };
        var tenantB = tenantA with
        {
            OperationInstanceId = "opinst-tenant-b",
            JobId = "job-tenant-b",
            TenantId = "tenant-b",
        };
        var wrongTenant = tenantA with
        {
            OperationInstanceId = "opinst-cross-tenant",
            JobId = "job-tenant-b",
        };
        (await store.TryCreateAsync(tenantA)).Should().BeTrue();
        (await store.TryCreateAsync(tenantB)).Should().BeTrue();
        (await store.TryCreateAsync(wrongTenant)).Should().BeTrue();

        var progress = Substitute.For<IUniversalProgressStore>();
        var completedAt = DateTimeOffset.UtcNow;
        var jobs = new Dictionary<string, LayerPublishJobProgress>(StringComparer.Ordinal)
        {
            ["job-tenant-a"] = new()
            {
                OperationId = "job-tenant-a",
                TenantId = "tenant-a",
                Status = Honua.Core.Features.Infrastructure.Domain.OperationStatus.Completed,
                StartedAt = completedAt.AddMinutes(-1),
                CompletedAt = completedAt,
                LayerId = 741,
            },
            ["job-tenant-b"] = new()
            {
                OperationId = "job-tenant-b",
                TenantId = "tenant-b",
                Status = Honua.Core.Features.Infrastructure.Domain.OperationStatus.Completed,
                StartedAt = completedAt.AddMinutes(-1),
                CompletedAt = completedAt,
                LayerId = 742,
            },
        };
        progress.GetProgressAsync<LayerPublishJobProgress>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(jobs.GetValueOrDefault(call.ArgAt<string>(0))));

        var audit = new RecordingAuditLog();
        await using var services = new ServiceCollection()
            .AddScoped<RequestTenantContext>()
            .AddScoped<ITenantContext>(provider => provider.GetRequiredService<RequestTenantContext>())
            .AddScoped<ILayerPublishJobs>(provider => new LayerPublishJobService(
                null!, null!, null!, progress, provider.GetRequiredService<ITenantContext>()))
            .AddScoped<IOperationExecutor>(provider => new TenantScopedJobExecutor(
                provider.GetRequiredService<ILayerPublishJobs>()))
            .AddScoped<IAuditLog>(_ => audit)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var reconciler = new QueuedOperationReconciler(
            store,
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<QueuedOperationReconciler>.Instance);

        await reconciler.SweepOnceAsync();

        (await store.GetAsync(tenantA.OperationInstanceId))!.Status.Should().Be(OperationHandleStatus.Completed);
        (await store.GetAsync(tenantB.OperationInstanceId))!.Status.Should().Be(OperationHandleStatus.Completed);
        (await store.GetAsync(wrongTenant.OperationInstanceId))!.Status.Should().Be(OperationHandleStatus.Queued);
        audit.Events.Should().HaveCount(2);
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
                MetadataRevision = 12,
                ResourceIds = new Dictionary<string, string> { ["serviceName"] = "roads" },
                Result = new OperationResultSummary
                {
                    Summary = "Backend completed.",
                    Details = new Dictionary<string, string>
                    {
                        ["serviceUri"] = "honua://published-services/roads",
                        ["metadataRevision"] = "12"
                    }
                },
            });
        }
    }

    private sealed class TenantScopedJobExecutor(ILayerPublishJobs jobs) : IOperationExecutor
    {
        public string OperationId => "test.async-operation";

        public Task<OperationValidation> ValidateAsync(
            OperationRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OperationHandle> SubmitAsync(
            OperationRequest request,
            OperationPolicyContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async Task<OperationStatus> GetStatusAsync(
            OperationHandle handle,
            CancellationToken cancellationToken = default)
        {
            var progress = await jobs.GetAsync(handle.JobId!, cancellationToken);
            return new OperationStatus
            {
                OperationInstanceId = handle.OperationInstanceId,
                OperationId = handle.OperationId,
                CorrelationId = handle.CorrelationId,
                AuditId = handle.AuditId,
                CreatedAt = handle.CreatedAt,
                UpdatedAt = progress?.CompletedAt ?? handle.UpdatedAt,
                Status = progress is null ? OperationHandleStatus.Queued : OperationHandleStatus.Completed,
                JobId = handle.JobId,
            };
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
