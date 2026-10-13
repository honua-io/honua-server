// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.Protocols.Mcp;
using Honua.ControlPlane;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin;
using Honua.Server.Startup;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProgressStatus = Honua.Core.Features.Infrastructure.Domain.OperationStatus;

namespace Honua.Server.Tests.Admin;

public sealed class LayerPublishWorkerControlsTests
{
    [UnitTest]
    public void WorkerProfile_RetainsPublishExecutorAndOnlyAssignedQueue()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IExecutionJobStore>(new InMemoryExecutionJobStore());
        services.AddSingleton(Substitute.For<IUniversalProgressStore>());
        services.AddSingleton(Substitute.For<IBatchComputeBackend>());
        services.AddLayerPublishJobs();
        services.ApplyExecutionWorkerProfile(new ExecutionWorkerLaunch
        {
            OperationId = "publish", JobKind = ExecutionJobKind.LayerPublish, ExecutionAttempt = 1,
        });
        using var provider = services.BuildServiceProvider();
        Assert.IsType<LayerPublishJobExecutor>(Assert.Single(provider.GetServices<IJobExecutor>()));
        Assert.IsType<AssignedExecutionJobQueue>(provider.GetRequiredService<IJobQueue>());
        Assert.Empty(provider.GetServices<IBatchComputeBackend>());
    }

    [UnitTest]
    public async Task Execute_RestoresTenantAndSchemaBeforeResolvingPublisher_AndKeepsActualFinalCount()
    {
        using var fixture = new Fixture();
        fixture.Publisher.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Assert.Equal("tenant-a", fixture.ResolvedTenant);
                Assert.Equal("tenant_a", fixture.ResolvedSchema);
                // The provider deliberately tolerates progress-store faults after a copied page.
                fixture.RejectFinalTick = true;
                await Assert.ThrowsAsync<IOException>(() => LayerSnapshotProgressScope.Current!.ReportAsync(7, 7, CancellationToken.None));
                return Summary();
            });

        var result = await fixture.ExecuteAsync();
        Assert.Equal(ExecutionJobStatus.Succeeded, result.Status);
        Assert.Equal(ProgressStatus.Completed, fixture.Progress.Status);
        Assert.Equal(7, fixture.Progress.RowsMaterialized);
        Assert.Equal(7, fixture.Progress.RowsTotal);
        Assert.Equal(23, fixture.Progress.LayerId);
        await fixture.Context.Received(1).RecordCommittedEffectAsync("layer-publish:23:7:7", CancellationToken.None);
    }

    [UnitTest]
    public async Task Execute_JobCancellationReachesPublisher_WithoutReportingSuccess()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Publisher.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(2));
                return Summary();
            });
        using var cancellation = new CancellationTokenSource();
        var running = fixture.ExecuteAsync(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.NotEqual(ProgressStatus.Completed, fixture.Progress.Status);
        Assert.Null(fixture.Progress.LayerId);
        await fixture.Context.DidNotReceive().RecordCommittedEffectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Execute_TerminalProjectionWriteFailsAfterCommit_DoesNotRetryPublication()
    {
        using var fixture = new Fixture { RejectTerminal = true };
        fixture.Publisher.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await LayerSnapshotProgressScope.Current!.ReportAsync(7, 7, CancellationToken.None);
                return Summary();
            });
        var result = await fixture.ExecuteAsync();
        Assert.Equal(ExecutionJobStatus.Succeeded, result.Status);
        Assert.True(result.CompletedWithCommittedEffects);
        Assert.Equal("layer-publish:23:7:7", fixture.Receipt);
        var recovered = LayerPublishJobService.ProjectExecutionOutcome(fixture.Progress, CompletedJob(fixture.Receipt!));
        Assert.Equal(ProgressStatus.Completed, recovered.Status);
        Assert.Equal(23, recovered.LayerId);
        Assert.Equal(7, recovered.RowsMaterialized);
        Assert.Equal(7, recovered.RowsTotal);
        await fixture.Publisher.Received(1).PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Execute_NotificationThrowsAfterCommit_DoesNotRetryPublication()
    {
        using var fixture = new Fixture();
        fixture.Notifications.BroadcastResourcesListChanged().Returns(_ => throw new IOException("notification failed"));
        fixture.Publisher.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(Summary());
        var result = await fixture.ExecuteAsync();
        Assert.Equal(ExecutionJobStatus.Succeeded, result.Status);
        Assert.True(result.CompletedWithCommittedEffects);
        Assert.Equal(ProgressStatus.Completed, fixture.Progress.Status);
        Assert.Equal("layer-publish:23", fixture.Receipt);
    }

    [UnitTheory]
    [InlineData("layer-publish:23:7:7", 23, 7, 7)]
    [InlineData("layer-publish:23", 23, 1, 9)]
    [InlineData("layer-publish:23:7:6", null, 1, 9)]
    [InlineData("layer-publish:23:-1:7", null, 1, 9)]
    [InlineData("layer-publish:0:7:7", null, 1, 9)]
    [InlineData("layer-publish:23:7", null, 1, 9)]
    public void Poll_RecoversOnlyRecordedValidCounts_AndKeepsLegacyUnknownCounts(string receipt, int? layerId, long materialized, long total)
    {
        var progress = new LayerPublishJobProgress
        {
            OperationId = "publish", Status = ProgressStatus.Processing,
            RowsMaterialized = 1, RowsTotal = 9, StartedAt = DateTimeOffset.UtcNow,
        };
        var recovered = LayerPublishJobService.ProjectExecutionOutcome(progress, CompletedJob(receipt));
        Assert.Equal(layerId.HasValue ? ProgressStatus.Completed : ProgressStatus.Failed, recovered.Status);
        Assert.Equal(layerId, recovered.LayerId);
        Assert.Equal(materialized, recovered.RowsMaterialized);
        Assert.Equal(total, recovered.RowsTotal);
    }

    private static ExecutionJobRecord CompletedJob(string receipt) => new()
    {
        OperationId = "publish", Status = ExecutionJobStatus.Succeeded,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.LayerPublish, TargetKind = BatchComputeTargetKind.AwsBatch,
            Backend = "honua-aws-batch", WorkloadName = "publish",
        },
        CommittedEffectReferences = [receipt],
    };

    private static PublishedLayerSummary Summary() => new()
    {
        LayerId = 23, LayerName = "Roads", Schema = "public", Table = "roads", GeometryType = "Point", ServiceName = "roads",
    };

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly IUniversalProgressStore _store = Substitute.For<IUniversalProgressStore>();
        internal ILayerPublishingService Publisher { get; } = Substitute.For<ILayerPublishingService>();
        internal IJobExecutionContext Context { get; } = Substitute.For<IJobExecutionContext>();
        internal IMcpNotificationPublisher Notifications { get; } = Substitute.For<IMcpNotificationPublisher>();
        internal string? Receipt { get; private set; }
        internal string? ResolvedTenant { get; private set; }
        internal string? ResolvedSchema { get; private set; }
        internal bool RejectFinalTick { get; set; }
        internal bool RejectTerminal { get; set; }
        internal LayerPublishJobProgress Progress { get; private set; } = new()
        {
            OperationId = "publish", Status = ProgressStatus.Queued, RowsTotal = 9, StartedAt = DateTimeOffset.UtcNow,
        };

        internal Fixture()
        {
            Context.RecordCommittedEffectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                Receipt = call.ArgAt<string>(0);
                return Task.CompletedTask;
            });
            _store.GetProgressAsync<LayerPublishJobProgress>("publish", Arg.Any<CancellationToken>()).Returns(_ => Progress);
            _store.SetProgressAsync("publish", Arg.Any<IOperationProgress>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var next = Assert.IsType<LayerPublishJobProgress>(call.ArgAt<IOperationProgress>(1));
                    if ((RejectFinalTick && next.Status == ProgressStatus.Processing && next.RowsMaterialized == 7)
                        || (RejectTerminal && next.Status == ProgressStatus.Completed))
                    {
                        throw new IOException("transient progress projection failure");
                    }
                    Progress = next;
                    return Task.CompletedTask;
                });
            var services = new ServiceCollection();
            services.AddSingleton(Notifications);
            services.AddScoped<RequestTenantContext>();
            services.AddScoped<ISchemaContext>(_ => new SchemaContext());
            services.AddScoped<ILayerPublishingService>(provider =>
            {
                ResolvedTenant = provider.GetRequiredService<RequestTenantContext>().TenantId;
                ResolvedSchema = provider.GetRequiredService<ISchemaContext>().CurrentSchema;
                return Publisher;
            });
            var resolver = Substitute.For<ISecureConnectionResolver>();
            resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns("Host=private;Password=secret");
            services.AddSingleton(resolver);
            services.Configure<LayerPublishingOptions>(options => options.MaterializationTimeoutSeconds = 30);
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        internal Task<JobExecutionResult> ExecuteAsync(CancellationToken cancellationToken = default)
            => LayerPublishJobWork.ExecuteAsync(_provider.GetRequiredService<IServiceScopeFactory>(), _store,
                NullLogger<LayerPublishWorkerControlsTests>.Instance, new LayerPublishJobPayload
                {
                    ConnectionId = Guid.NewGuid(), ConnectionKey = "roads", RowCount = 9,
                    TenantId = "tenant-a", TenantSource = TenantContextSource.Header, CurrentSchema = "tenant_a",
                    Request = new PublishLayerRequest { Schema = "public", Table = "roads", LayerName = "Roads" },
                }, "publish", Context, cancellationToken);

        public void Dispose() => _provider.Dispose();
    }
}
