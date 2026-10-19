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
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
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
    [Trait("Tier", "Fast")]
    [InlineData(false, false, (int)LayerPublishRunnerMode.InProcess)]
    [InlineData(true, false, (int)LayerPublishRunnerMode.DurableLocal)]
    [InlineData(true, true, (int)LayerPublishRunnerMode.Unavailable)]
    [InlineData(false, true, (int)LayerPublishRunnerMode.Unavailable)]
    public void Decide_SelectsDurableInProcessOrFrozen(bool durable, bool frozen, int expected)
        => Assert.Equal((LayerPublishRunnerMode)expected, LayerPublishRunnerDecision.Decide(durable, frozen));

    [UnitTest]
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
            .SubmitAsync(Connection, Guid.NewGuid(), DomainRequest(), "roads", request.Token);

        Assert.Equal(LayerPublishAdmissionKind.Completed, admission.Kind);
        Assert.Equal(7, admission.Layer!.LayerId);
        Assert.False(observed.CanBeCanceled);
    }

    [UnitTest]
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

        var admission = await service.SubmitAsync(Connection, Guid.NewGuid(), DomainRequest(), "roads", request.Token);
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

    [UnitTest]
    public async Task Submit_AboveThresholdOnFrozenHost_IsUnavailable()
    {
        var publisher = Substitute.For<ILayerPublishingService>();
        publisher.CountSourceRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(11L);
        var queue = Substitute.For<IJobQueue>();

        var admission = await Service(publisher, LayerPublishRunnerMode.Unavailable, threshold: 10, queue: queue)
            .SubmitAsync(Connection, Guid.NewGuid(), DomainRequest(), "roads", CancellationToken.None);

        Assert.Equal(LayerPublishAdmissionKind.Unavailable, admission.Kind);
        await queue.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());
        await publisher.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
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
            .SubmitAsync(Connection, Guid.NewGuid(), DomainRequest(), "roads", CancellationToken.None);

        Assert.Equal(LayerPublishAdmissionKind.Queued, admission.Kind);
        await queue.Received(1).EnqueueAsync(admission.JobId!, Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());
        Assert.Equal(ExecutionJobKind.LayerPublish, created!.Spec.Kind);
        Assert.Equal("local", created.Spec.Backend);
        await publisher.DidNotReceive().PublishLayerAsync(
            Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
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
        Assert.Equal(11, progress.MetadataRevision);
    }

    [UnitTest]
    public async Task Refusal_IsTheTypedCapabilityProblem()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        await LayerPublishRefusals.ToResult(context).ExecuteAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Contains("durable-runner-required", body, StringComparison.Ordinal);
        Assert.Contains("layer-publish-runner", body, StringComparison.Ordinal);
        Assert.Contains("jobs.runner", body, StringComparison.Ordinal);
        var exception = LayerPublishRefusals.Unavailable();
        Assert.IsType<CapabilityUnavailableException>(exception);
        Assert.Equal(LayerPublishRunnerDecision.RunnerDependency, exception.MissingDependency);
    }

    [UnitTest]
    public void SubmissionStatus_Is202OnlyForQueuedServicePublish()
    {
        var queued = Handle(OperationHandleStatus.Queued, ServicePublishOperation.OperationId);
        var completed = Handle(OperationHandleStatus.Completed, ServicePublishOperation.OperationId);
        var other = Handle(OperationHandleStatus.Queued, "service.other");

        Assert.Equal(StatusCodes.Status202Accepted, OperationsEndpoints.SubmissionStatusCode(queued));
        Assert.Equal(StatusCodes.Status200OK, OperationsEndpoints.SubmissionStatusCode(completed));
        Assert.Equal(StatusCodes.Status200OK, OperationsEndpoints.SubmissionStatusCode(other));
    }

    [UnitTest]
    public async Task Executor_QueuedPublish_ReturnsQueuedHandleWithoutPublishingInline()
    {
        var jobs = Substitute.For<ILayerPublishJobs>();
        jobs.SubmitAsync(
                Arg.Any<string>(),
                Arg.Any<Guid>(),
                Arg.Any<LayerPublishRequest>(),
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

    [UnitTest]
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

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Executor_CompletedQueuedPublish_FreshExecutorResolvesPersistedService(bool stopped)
    {
        const string serviceName = "roads/area%2Ftwo";
        var graph = PublishedGraph(serviceName) with { Revision = 12 };
        if (stopped)
        {
            graph = graph with
            {
                Services = graph.Services.Select(service => service with
                {
                    Protocols = [],
                    Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Retired }
                }).ToArray()
            };
        }

        var jobs = CompletedJobs(741);
        var originalHandle = Handle(OperationHandleStatus.Queued, ServicePublishOperation.OperationId) with
        {
            JobId = "abc"
        };
        // Reconstruct the executor/provider twice: neither has the original
        // submitted name or a process-local publication result to fall back on.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var executor = Executor(jobs, new TestMetadataV2GraphProvider(graph));
            var status = await executor.GetStatusAsync(originalHandle);

            Assert.Equal(OperationHandleStatus.Completed, status.Status);
            Assert.Equal(11, status.MetadataRevision);
            Assert.Equal(serviceName, status.ResourceIds["serviceName"]);
            Assert.Equal("741", status.ResourceIds["layerId"]);
            Assert.Equal(serviceName, status.Result!.Details["serviceName"]);
            Assert.Equal("11", status.Result.Details["metadataRevision"]);
            Assert.Equal("honua://published-services/roads%2Farea%252Ftwo", status.Result.Details["serviceUri"]);
            Assert.Equal("Resolved", status.Result.Details["readbackStatus"]);
        }
    }

    [UnitTest]
    public async Task Executor_CompletedQueuedPublish_CaseOnlyPromotionCollisionWithholdsUri()
    {
        var services = Substitute.For<IPublishedServiceStore>();
        services.FindByPublicIdAsync("roads", Arg.Any<CancellationToken>())
            .Returns(new PublishedServiceRecord
            {
                ServiceId = "ROADS",
                IntentId = "another-intent",
                SourceKind = PublishSourceKind.ResultPackage,
                SourceId = "another-package",
                TargetKind = PublishTargetKind.FeatureService,
                Status = PublishedServiceStatus.Active,
                PublishedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

        var status = await Executor(CompletedJobs(741), new TestMetadataV2GraphProvider(PublishedGraph("roads")), services)
            .GetStatusAsync(Handle(OperationHandleStatus.Queued, ServicePublishOperation.OperationId) with { JobId = "abc" });

        Assert.Equal(OperationHandleStatus.Completed, status.Status);
        Assert.Equal(11, status.MetadataRevision);
        Assert.Equal("Unavailable", status.Result!.Details["readbackStatus"]);
        Assert.False(status.Result.Details.ContainsKey("serviceUri"));
        Assert.False(status.Result.Details.ContainsKey("serviceName"));
        Assert.False(status.ResourceIds.ContainsKey("serviceName"));
    }

    [UnitTest]
    public async Task Executor_CompletedQueuedPublish_AmbiguousPromotionKeepsCommittedCompletion()
    {
        var services = Substitute.For<IPublishedServiceStore>();
        services.FindByPublicIdAsync("roads", Arg.Any<CancellationToken>())
            .Returns<Task<PublishedServiceRecord?>>(_ => throw new PublishedServiceIdentityAmbiguousException("roads"));

        var status = await Executor(CompletedJobs(741), new TestMetadataV2GraphProvider(PublishedGraph("roads")), services)
            .GetStatusAsync(Handle(OperationHandleStatus.Queued, ServicePublishOperation.OperationId) with { JobId = "abc" });

        Assert.Equal(OperationHandleStatus.Completed, status.Status);
        Assert.Equal(11, status.MetadataRevision);
        Assert.Equal("Unavailable", status.Result!.Details["readbackStatus"]);
        Assert.False(status.Result.Details.ContainsKey("serviceUri"));
    }

    [UnitTest]
    public async Task Executor_InlinePublish_UsesCommittedRevisionAndWithholdsCollidingUri()
    {
        var publisher = Substitute.For<ILayerPublishingService>();
        publisher.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(Summary());
        var services = Substitute.For<IPublishedServiceStore>();
        services.FindByPublicIdAsync("default", Arg.Any<CancellationToken>())
            .Returns(new PublishedServiceRecord
            {
                ServiceId = "DEFAULT",
                IntentId = "another-intent",
                SourceKind = PublishSourceKind.ResultPackage,
                SourceId = "another-package",
                TargetKind = PublishTargetKind.FeatureService,
                Status = PublishedServiceStatus.Active,
                PublishedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        var graph = PublishedGraph("default") with { Revision = 12 };
        var executor = new ServicePublishExecutor(
            publisher,
            Substitute.For<ISecureConnectionResolver>(),
            new TestMetadataV2GraphProvider(graph),
            TimeProvider.System,
            publishedServices: services);

        var handle = await executor.SubmitAsync(Operation(), Policy(), CancellationToken.None);

        Assert.Equal(OperationHandleStatus.Completed, handle.Status);
        Assert.Equal(11, handle.MetadataRevision);
        Assert.Equal("11", handle.Result!.Details["metadataRevision"]);
        Assert.Equal("Unavailable", handle.Result.Details["readbackStatus"]);
        Assert.False(handle.Result.Details.ContainsKey("serviceName"));
        Assert.False(handle.ResourceIds.ContainsKey("serviceName"));
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("public-index-only")]
    [InlineData("missing-binding")]
    [InlineData("cross-resource-binding")]
    [InlineData("ambiguous-service")]
    [InlineData("foreign-tenant")]
    [InlineData("stale-binding-reference")]
    [InlineData("duplicate-public-index")]
    [InlineData("duplicate-name-other-storage")]
    public async Task Executor_CompletedQueuedPublish_UnresolvedCatalogDoesNotInventUri(string defect)
    {
        var graph = PublishedGraph("roads");
        var layerId = 741;
        switch (defect)
        {
            case "public-index-only":
                layerId = 3;
                break;
            case "missing-binding":
                graph = graph with { StorageBindings = [] };
                break;
            case "cross-resource-binding":
                graph = graph with
                {
                    StorageBindings = graph.StorageBindings.Select(binding => binding with
                    {
                        ResourceId = "another-resource"
                    }).ToArray()
                };
                break;
            case "ambiguous-service":
                var other = PublishedGraph("another-service", "other-service-id", "other-publication");
                graph = graph with
                {
                    Services = [.. graph.Services, .. other.Services],
                    Publications = [.. graph.Publications, .. other.Publications]
                };
                break;
            case "stale-binding-reference":
                graph = graph with
                {
                    Publications = graph.Publications.Select(publication => publication with
                    {
                        StorageBindingId = "missing-explicit-binding"
                    }).ToArray()
                };
                break;
            case "duplicate-public-index":
                graph = graph with
                {
                    Publications = [.. graph.Publications, graph.Publications[0] with
                    {
                        Metadata = graph.Publications[0].Metadata with { Id = "duplicate-publication" }
                    }]
                };
                break;
            case "duplicate-name-other-storage":
                var sameName = new TestMetadataV2GraphBuilder()
                    .AddResource("other-data", "Other")
                    .AddStorageBinding("other-binding", "other-data", "public.other", storageLayerId: 742)
                    .AddService("other-service", "ROADS")
                    .AddPublication("other-publication", "other-service", "other-data", layerIndex: 0,
                        storageBindingId: "other-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer)
                    .Build();
                graph = graph with
                {
                    Resources = [.. graph.Resources, .. sameName.Resources],
                    StorageBindings = [.. graph.StorageBindings, .. sameName.StorageBindings],
                    Services = [.. graph.Services, .. sameName.Services],
                    Publications = [.. graph.Publications, .. sameName.Publications]
                };
                break;
            case "foreign-tenant":
                graph = graph with
                {
                    Services = graph.Services.Select(service => service with
                    {
                        Metadata = service.Metadata with { Tenant = "another-tenant" }
                    }).ToArray()
                };
                break;
        }

        var status = await Executor(CompletedJobs(layerId), new TestMetadataV2GraphProvider(graph))
            .GetStatusAsync(Handle(OperationHandleStatus.Queued, ServicePublishOperation.OperationId) with { JobId = "abc" });

        // A projection failure cannot undo or retry the already committed copy.
        Assert.Equal(OperationHandleStatus.Completed, status.Status);
        Assert.Equal("Unavailable", status.Result!.Details["readbackStatus"]);
        Assert.False(status.Result.Details.ContainsKey("serviceUri"));
        Assert.False(status.Result.Details.ContainsKey("serviceName"));
        Assert.False(status.ResourceIds.ContainsKey("serviceName"));
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(ProgressStatus.Queued, OperationHandleStatus.Queued)]
    [InlineData(ProgressStatus.Processing, OperationHandleStatus.Running)]
    [InlineData(ProgressStatus.Failed, OperationHandleStatus.Failed)]
    [InlineData(ProgressStatus.Cancelled, OperationHandleStatus.Cancelled)]
    public async Task Executor_UncompletedPublish_DoesNotAdvertiseExistingServiceAsItsResult(
        ProgressStatus progressStatus, OperationHandleStatus expectedStatus)
    {
        var jobs = Substitute.For<ILayerPublishJobs>();
        jobs.GetAsync("abc", Arg.Any<CancellationToken>()).Returns(new LayerPublishJobProgress
        {
            OperationId = "abc",
            Status = progressStatus,
            LayerId = 741,
            RowsMaterialized = 1,
            RowsTotal = 12,
            StartedAt = DateTimeOffset.UtcNow
        });
        // The catalog already contains this layer, but that cannot stand in for
        // successful completion of the current publication operation.
        var status = await Executor(jobs, new TestMetadataV2GraphProvider(PublishedGraph("roads")))
            .GetStatusAsync(Handle(OperationHandleStatus.Queued, ServicePublishOperation.OperationId) with { JobId = "abc" });

        Assert.Equal(expectedStatus, status.Status);
        Assert.Null(status.MetadataRevision);
        Assert.Equal($"Layer publish {progressStatus}.", status.Result!.Summary);
        Assert.False(status.Result.Details.ContainsKey("serviceUri"));
        Assert.False(status.Result.Details.ContainsKey("serviceName"));
        Assert.False(status.ResourceIds.ContainsKey("serviceName"));
    }

    private static ILayerPublishJobs CompletedJobs(int layerId)
    {
        var jobs = Substitute.For<ILayerPublishJobs>();
        jobs.GetAsync("abc", Arg.Any<CancellationToken>()).Returns(new LayerPublishJobProgress
        {
            OperationId = "abc",
            Status = ProgressStatus.Completed,
            RowsMaterialized = 12,
            RowsTotal = 12,
            LayerId = layerId,
            MetadataRevision = 11,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        });
        return jobs;
    }

    private static MetadataV2Graph PublishedGraph(
        string serviceName,
        string serviceId = "actual-service-id",
        string publicationId = "published-roads")
        => new TestMetadataV2GraphBuilder()
            .AddResource("road-data", "Roads")
            .AddStorageBinding("road-binding", "road-data", "public.roads", storageLayerId: 741)
            .AddService(serviceId, serviceName)
            .AddPublication(publicationId, serviceId, "road-data", layerIndex: 3,
                storageBindingId: "road-binding", publicationType: MetadataV2PublicationType.EsriFeatureLayer)
            .Build();

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
        store ??= new MemoryProgress();
        var dispatcher = new LayerPublishJobDispatcher(
            new LayerPublishRunnerDecision(mode), store,
            scopes ?? Substitute.For<IServiceScopeFactory>(), lifetime,
            NullLogger<LayerPublishJobDispatcher>.Instance, jobStore, queue);
        return new LayerPublishJobService(
            publisher, options, dispatcher, store, tenant, new SchemaContext(), jobStore);
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

    private static ServicePublishExecutor Executor(
        ILayerPublishJobs jobs,
        IMetadataV2GraphProvider? graph = null,
        IPublishedServiceStore? publishedServices = null)
        => new(
            Substitute.For<ILayerPublishingService>(),
            Substitute.For<ISecureConnectionResolver>(),
            graph ?? Substitute.For<IMetadataV2GraphProvider>(),
            TimeProvider.System,
            notifications: null,
            jobs,
            publishedServices);

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
            MetadataRevision = 11,
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
