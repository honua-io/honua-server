// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using ProgressStatus = Honua.Core.Features.Infrastructure.Domain.OperationStatus;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Admin;

public sealed class LayerPublishJobTests
{
    [Theory]
    [InlineData(false, false, (int)LayerPublishRunnerMode.InProcess)]
    [InlineData(true, false, (int)LayerPublishRunnerMode.DurableLocal)]
    [InlineData(true, true, (int)LayerPublishRunnerMode.Unavailable)]
    [InlineData(false, true, (int)LayerPublishRunnerMode.Unavailable)]
    public void Decide_SelectsDurableInProcessOrFrozen(bool durable, bool frozen, int expected)
        => Assert.Equal((LayerPublishRunnerMode)expected, LayerPublishRunnerDecision.Decide(durable, frozen));

    [Fact]
    public async Task Submit_AtThreshold_PublishesWithoutTheRequestToken()
    {
        using var request = new CancellationTokenSource();
        request.Cancel();
        var publisher = Substitute.For<ILayerPublishingService>();
        publisher.CountSourceRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(10L);
        CancellationToken observed = default;
        publisher.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                observed = call.ArgAt<CancellationToken>(2);
                return Summary();
            });

        var admission = await Service(publisher, LayerPublishRunnerMode.InProcess, threshold: 10)
            .SubmitAsync(Connection, Guid.NewGuid(), DomainRequest(), HttpRequest(), "roads", request.Token);

        Assert.Equal(LayerPublishAdmissionKind.Completed, admission.Kind);
        Assert.Equal(7, admission.Layer!.LayerId);
        Assert.False(observed.CanBeCanceled);
    }

    [Fact]
    public async Task Submit_AboveThreshold_QueuesInProcessAndDetachesFromTheRequest()
    {
        using var request = new CancellationTokenSource();
        var publisher = Substitute.For<ILayerPublishingService>();
        publisher.CountSourceRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(11L);
        var scopePublisher = Substitute.For<ILayerPublishingService>();
        var seen = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        scopePublisher.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seen.TrySetResult(call.ArgAt<CancellationToken>(2));
                return Summary();
            });
        var store = new MemoryProgress();
        var service = Service(
            publisher,
            LayerPublishRunnerMode.InProcess,
            threshold: 10,
            store,
            ScopeFactory(scopePublisher));

        var admission = await service.SubmitAsync(Connection, Guid.NewGuid(), DomainRequest(), HttpRequest(), "roads", request.Token);
        var observed = await seen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        request.Cancel();

        Assert.Equal(LayerPublishAdmissionKind.Queued, admission.Kind);
        Assert.False(string.IsNullOrWhiteSpace(admission.JobId));
        Assert.Contains("/layers/jobs/", admission.StatusUrl, StringComparison.Ordinal);
        Assert.False(observed.IsCancellationRequested);
        await publisher.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
        var progress = await store.GetProgressAsync<LayerPublishJobProgress>(admission.JobId!, CancellationToken.None);
        Assert.NotNull(progress);
        Assert.Equal(11, progress!.RowsTotal);
    }

    [Fact]
    public async Task Submit_AboveThresholdOnFrozenHost_IsUnavailable()
    {
        var publisher = Substitute.For<ILayerPublishingService>();
        publisher.CountSourceRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(11L);
        var queue = Substitute.For<IJobQueue>();

        var admission = await Service(publisher, LayerPublishRunnerMode.Unavailable, threshold: 10, queue: queue)
            .SubmitAsync(Connection, Guid.NewGuid(), DomainRequest(), HttpRequest(), "roads", CancellationToken.None);

        Assert.Equal(LayerPublishAdmissionKind.Unavailable, admission.Kind);
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());
        await publisher.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_AboveThresholdWithRedis_EnqueuesTheSharedQueue()
    {
        var publisher = Substitute.For<ILayerPublishingService>();
        publisher.CountSourceRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(11L);
        var store = new MemoryProgress();
        var jobs = Substitute.For<IExecutionJobStore>();
        var queue = Substitute.For<IJobQueue>();
        ExecutionJobRecord? created = null;
        jobs.TryCreateAsync(
                Arg.Do<ExecutionJobRecord>(record => created = record),
                Arg.Any<TimeSpan?>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        var admission = await Service(
                publisher,
                LayerPublishRunnerMode.DurableLocal,
                threshold: 10,
                store,
                jobStore: jobs,
                queue: queue)
            .SubmitAsync(Connection, Guid.NewGuid(), DomainRequest(), HttpRequest(), "roads", CancellationToken.None);

        Assert.Equal(LayerPublishAdmissionKind.Queued, admission.Kind);
        await queue.Received(1).EnqueueAsync(admission.JobId!, Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());
        Assert.Equal(ExecutionJobKind.LayerPublish, created!.Spec.Kind);
        Assert.Equal("local", created.Spec.Backend);
        await publisher.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_ReportsRowsAndLayerId()
    {
        var store = new MemoryProgress();
        var jobId = "job-1";
        await store.SetProgressAsync(jobId, new LayerPublishJobProgress
        {
            OperationId = jobId,
            Status = ProgressStatus.Queued,
            RowsTotal = 5,
            StartedAt = DateTimeOffset.UtcNow,
        });
        var publisher = Substitute.For<ILayerPublishingService>();
        publisher.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var sink = LayerSnapshotProgressScope.Current;
                Assert.NotNull(sink);
                await sink!.ReportAsync(2, 5, CancellationToken.None);
                await sink.ReportAsync(5, 5, CancellationToken.None);
                return Summary();
            });
        var resolver = Substitute.For<ISecureConnectionResolver>();
        resolver.ResolveConnectionStringAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Connection);
        var payload = new LayerPublishJobPayload
        {
            ConnectionId = Guid.NewGuid(),
            ConnectionKey = "roads",
            RowCount = 5,
            Request = HttpRequest(),
        };

        var result = await LayerPublishJobWork.ExecuteAsync(
            ScopeFactory(publisher, resolver),
            store,
            NullLogger<LayerPublishJobTests>.Instance,
            payload,
            jobId,
            context: null,
            CancellationToken.None);

        Assert.Equal(ExecutionJobStatus.Succeeded, result.Status);
        Assert.True(result.CompletedWithCommittedEffects);
        var progress = await store.GetProgressAsync<LayerPublishJobProgress>(jobId, CancellationToken.None);
        Assert.Equal(ProgressStatus.Completed, progress!.Status);
        Assert.Equal(5, progress.RowsMaterialized);
        Assert.Equal(7, progress.LayerId);
    }

    [Fact]
    public async Task Refusal_IsTheTypedCapabilityProblem()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        await LayerPublishRefusals.ToResult(context).ExecuteAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Contains("durable-runner-required", body, StringComparison.Ordinal);
        Assert.Contains("layer-publish-runner", body, StringComparison.Ordinal);
        Assert.Contains("jobs.runner", body, StringComparison.Ordinal);
        var exception = LayerPublishRefusals.Unavailable();
        Assert.IsType<CapabilityUnavailableException>(exception);
        Assert.Equal(LayerPublishRunnerDecision.RunnerDependency, exception.MissingDependency);
    }

    [Fact]
    public void SubmissionStatus_Is202OnlyForQueuedServicePublish()
    {
        var queued = Handle(OperationHandleStatus.Queued, ServicePublishOperation.OperationId);
        var completed = Handle(OperationHandleStatus.Completed, ServicePublishOperation.OperationId);
        var other = Handle(OperationHandleStatus.Queued, "service.other");

        Assert.Equal(StatusCodes.Status202Accepted, OperationsEndpoints.SubmissionStatusCode(queued));
        Assert.Equal(StatusCodes.Status200OK, OperationsEndpoints.SubmissionStatusCode(completed));
        Assert.Equal(StatusCodes.Status200OK, OperationsEndpoints.SubmissionStatusCode(other));
    }

    [Fact]
    public async Task Executor_QueuedPublish_ReturnsQueuedHandleWithoutPublishingInline()
    {
        var jobs = Substitute.For<ILayerPublishJobs>();
        jobs.SubmitAsync(
                Arg.Any<string>(),
                Arg.Any<Guid>(),
                Arg.Any<LayerPublishRequest>(),
                Arg.Any<PublishLayerRequest>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new LayerPublishAdmission(
                LayerPublishAdmissionKind.Queued, null, "abc", 12, "/api/v1/admin/connections/roads/layers/jobs/abc"));
        var executor = Executor(jobs);
        var handle = await executor.SubmitAsync(Operation(), Policy(), CancellationToken.None);

        Assert.Equal(OperationHandleStatus.Queued, handle.Status);
        Assert.Equal("abc", handle.JobId);
        Assert.Equal("12", handle.Result!.Details["rowsTotal"]);
    }

    [Fact]
    public async Task Executor_GetStatus_ProjectsLayerId()
    {
        var jobs = Substitute.For<ILayerPublishJobs>();
        jobs.GetAsync("abc", Arg.Any<CancellationToken>()).Returns(new LayerPublishJobProgress
        {
            OperationId = "abc",
            Status = ProgressStatus.Completed,
            RowsMaterialized = 12,
            RowsTotal = 12,
            LayerId = 7,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        });
        var status = await Executor(jobs).GetStatusAsync(Handle(OperationHandleStatus.Queued, ServicePublishOperation.OperationId) with
        {
            JobId = "abc",
        });

        Assert.Equal(OperationHandleStatus.Completed, status.Status);
        Assert.Equal("7", status.ResourceIds["layerId"]);
        Assert.Equal("12", status.Result!.Details["rowsMaterialized"]);
    }

    private const string Connection = "Host=localhost;Username=honua;Database=honua";

    private static LayerPublishJobService Service(
        ILayerPublishingService publisher,
        LayerPublishRunnerMode mode,
        int threshold,
        MemoryProgress? store = null,
        IServiceScopeFactory? scopes = null,
        IExecutionJobStore? jobStore = null,
        IJobQueue? queue = null)
    {
        var options = Options.Create(new LayerPublishingOptions
        {
            AsyncPublishRowThreshold = threshold,
            MaterializationTimeoutSeconds = 30,
        });
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        var tenant = new RequestTenantContext();
        tenant.Set("tenant-a", TenantContextSource.Header);
        return new LayerPublishJobService(
            publisher,
            options,
            new LayerPublishRunnerDecision(mode),
            store ?? new MemoryProgress(),
            scopes ?? Substitute.For<IServiceScopeFactory>(),
            lifetime,
            tenant,
            NullLogger<LayerPublishJobService>.Instance,
            new SchemaContext(),
            jobStore,
            queue);
    }

    private static IServiceScopeFactory ScopeFactory(
        ILayerPublishingService publisher,
        ISecureConnectionResolver? resolver = null)
    {
        resolver ??= Substitute.For<ISecureConnectionResolver>();
        var provider = Substitute.For<IServiceProvider>();
        var tenant = new RequestTenantContext();
        provider.GetService(typeof(RequestTenantContext)).Returns(tenant);
        provider.GetService(typeof(ISchemaContext)).Returns(new SchemaContext());
        provider.GetService(typeof(ILayerPublishingService)).Returns(publisher);
        provider.GetService(typeof(ISecureConnectionResolver)).Returns(resolver);
        provider.GetService(typeof(IOptions<LayerPublishingOptions>))
            .Returns(Options.Create(new LayerPublishingOptions { MaterializationTimeoutSeconds = 30 }));
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    private static ServicePublishExecutor Executor(ILayerPublishJobs jobs)
        => new(
            Substitute.For<ILayerPublishingService>(),
            Substitute.For<ISecureConnectionResolver>(),
            Substitute.For<IMetadataV2GraphProvider>(),
            TimeProvider.System,
            notifications: null,
            jobs);

    private static LayerPublishRequest DomainRequest()
        => new() { Schema = "public", Table = "roads", LayerName = "Roads" };

    private static PublishLayerRequest HttpRequest()
        => new() { Schema = "public", Table = "roads", LayerName = "Roads" };

    private static PublishedLayerSummary Summary()
        => new()
        {
            LayerId = 7,
            LayerName = "Roads",
            Schema = "public",
            Table = "roads",
            GeometryType = "LineString",
            ServiceName = "default",
        };

    private static OperationRequest Operation()
        => new()
        {
            OperationId = ServicePublishOperation.OperationId,
            ConnectionId = Guid.NewGuid().ToString("D"),
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["schema"] = "public",
                ["table"] = "roads",
                ["layerName"] = "Roads",
            },
        };

    private static OperationPolicyContext Policy()
        => new() { ResolvedConnectionString = Connection };

    private static OperationHandle Handle(OperationHandleStatus status, string operationId)
        => new()
        {
            OperationInstanceId = "opinst-1",
            OperationId = operationId,
            CorrelationId = "corr-1",
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    private sealed class MemoryProgress : IUniversalProgressStore
    {
        private readonly Dictionary<string, IOperationProgress> _items = new(StringComparer.Ordinal);

        public Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _items[operationId] = progress;
            return Task.CompletedTask;
        }

        public Task<ProgressCompareAndSetResult> TrySetProgressAsync(
            string operationId, IOperationProgress progress, ProgressStatus expectedStatus, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(ProgressCompareAndSetResult.Updated);

        public Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult(_items.TryGetValue(operationId, out var progress) ? progress as TProgress : null);

        public Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.TryGetValue(operationId, out var progress) ? progress : null);

        public Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
        {
            _items.Remove(operationId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(_items.Keys.ToArray());

        public Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
            where TProgress : class, IOperationProgress
            => Task.FromResult<IReadOnlyList<TProgress>>([]);
    }
}
