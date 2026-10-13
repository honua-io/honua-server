// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Honua.Migration;
using Honua.Server.Features.Admin.Models;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ProgressStatus = Honua.Core.Features.Infrastructure.Domain.OperationStatus;
using StackExchange.Redis;
using Testcontainers.Redis;
using RedisJobQueue = Honua.ControlPlane.RedisJobQueue;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.OperationsProgress)]
    [Endpoint("GET /api/v1/admin/connections/{id}/layers/jobs/{jobId}")]
    public async Task PollLayerPublish_UnknownJob_ReturnsTypedNotFound()
    {
        using var response = await _client.GetAsync(
            $"/api/v1/admin/connections/{_connectionId:D}/layers/jobs/{Guid.NewGuid():N}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("The requested resource was not found.");
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData("source")]
    [InlineData("managed")]
    [Operation(Operations.ContractTesting)]
    public async Task RemotePublish_AssignedWorker_MaterializesPostGisAndPersistsCompleteProgress(string storageMode)
    {
        await UseServerFeatureStoreConnectionAsync();
        // The shared fixture seeds one row; exceed the one-row async admission threshold.
        await InsertPostGisFeatureAsync("Remote worker feature", 200, 2, 2);
        (await GetSourceRowCountAsync()).Should().Be(2);
        await using var redis = new RedisBuilder("redis:7.2-alpine").Build();
        await redis.StartAsync();
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redis.GetConnectionString());
        var jobs = new RedisExecutionJobStore(multiplexer, NullLogger<RedisExecutionJobStore>.Instance);
        using var acceptingCache = new RedisCache(Options.Create(new RedisCacheOptions { Configuration = redis.GetConnectionString() }));
        using var workerCache = new RedisCache(Options.Create(new RedisCacheOptions { Configuration = redis.GetConnectionString() }));
        var acceptingProgress = new UniversalProgressStore(acceptingCache, NullLogger<UniversalProgressStore>.Instance, multiplexer);
        var workerProgress = new UniversalProgressStore(workerCache, NullLogger<UniversalProgressStore>.Instance, multiplexer);
        var backend = NSubstitute.Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("honua-aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>()).Returns(new BatchComputeSubmissionResult
        {
            Status = ExecutionJobStatus.Queued,
            ProviderOperationId = "batch-launched-worker",
        });
        using var scope = _fixture.Services.CreateScope();
        var scopeFactory = _fixture.GetService<IServiceScopeFactory>();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AWS_LAMBDA_FUNCTION_NAME"] = "api" }).Build());
        services.Configure<DurableJobSubstrateOptions>(options => { options.RedisConfigured = true; options.RedisEntitled = true; });
        services.Configure<ControlPlaneOptions>(options => options.ExecutionWorkloads.Add(new ExecutionWorkloadOptions
        {
            WorkloadId = "managed-publish",
            WorkloadName = "managed-publish",
            Kind = ExecutionJobKind.LayerPublish,
            TargetKind = BatchComputeTargetKind.AwsBatch,
            Backend = backend.BackendName,
            RuntimeProfile = RuntimeProfiles.Managed,
            Parameters = new Dictionary<string, string> { ["batch.job_queue_arn"] = "queue", ["batch.job_definition_arn.s"] = "definition:1" },
        }));
        services.Configure<LayerPublishingOptions>(options => options.AsyncPublishRowThreshold = 1);
        services.AddSingleton<IExecutionJobStore>(jobs);
        services.AddSingleton<IJobQueue>(new RedisJobQueue(multiplexer, jobs, NullLogger<RedisJobQueue>.Instance));
        services.AddSingleton<IUniversalProgressStore>(acceptingProgress);
        services.AddSingleton(scope.ServiceProvider.GetRequiredService<ILayerPublishingService>());
        services.AddSingleton(backend);
        services.AddSingleton(NSubstitute.Substitute.For<IHostApplicationLifetime>());
        var tenant = new RequestTenantContext();
        tenant.Set("public", TenantContextSource.Default);
        services.AddSingleton<ITenantContext>(tenant);
        services.AddSingleton<ISchemaContext>(new SchemaContext { CurrentSchema = _fixture.CurrentSchema });
        services.AddLayerPublishJobs();
        using var acceptingHost = services.BuildServiceProvider();
        using var acceptingScope = acceptingHost.CreateScope();
        var service = acceptingScope.ServiceProvider.GetRequiredService<ILayerPublishJobs>();
        var request = new PublishLayerRequest
        {
            Schema = _schema,
            Table = _tableName,
            LayerName = "Remote publish",
            ServiceName = _serviceName,
            GeometryColumn = "geom",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = _idNamePopulationFields,
            StorageMode = storageMode,
            Enabled = true,
        };
        var connection = await scope.ServiceProvider.GetRequiredService<Honua.Core.Features.Security.Abstractions.ISecureConnectionResolver>()
            .ResolveConnectionStringAsync(_connectionId);
        using var disconnected = new CancellationTokenSource();
        var accepted = await service.SubmitAsync(connection, _connectionId,
            LayerPublishRequestMapper.CreateDomain(request, _connectionId, tenant.TenantId),
            _connectionId.ToString("D"), disconnected.Token);
        accepted.Kind.Should().Be(LayerPublishAdmissionKind.Queued);
        accepted.RowsTotal.Should().Be(2);
        disconnected.Cancel();

        var queued = await workerProgress.GetProgressAsync<LayerPublishJobProgress>(accepted.JobId!);
        queued!.RowsMaterialized.Should().Be(0);
        var assigned = new AssignedExecutionJobQueue(new ExecutionWorkerLaunch
        {
            OperationId = accepted.JobId!,
            JobKind = ExecutionJobKind.LayerPublish,
            ExecutionAttempt = 1,
        }, jobs, NullLogger<AssignedExecutionJobQueue>.Instance);
        using var worker = new JobExecutionService(assigned, jobs,
            [new LayerPublishJobExecutor(scopeFactory, workerProgress, NullLogger<LayerPublishJobExecutor>.Instance)],
            new ExecutionJobCancellationTokens(), [],
            new RedisExecutionLogStore(multiplexer, NullLogger<RedisExecutionLogStore>.Instance),
            NullLogger<JobExecutionService>.Instance)
        {
            RequeueOnShutdown = true,
        };
        await worker.StartAsync(CancellationToken.None);
        try
        {
            (await assigned.Completion.WaitAsync(TimeSpan.FromSeconds(90))).Should().Be(ExecutionWorkerClaimOutcome.Executed);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var completed = (await jobs.GetAsync(accepted.JobId!))!;
        completed.Status.Should().Be(ExecutionJobStatus.Succeeded, completed.ErrorMessage);
        completed.ProviderOperationId.Should().Be("batch-launched-worker");
        completed.AttemptCount.Should().Be(1);
        var progress = (await service.GetAsync(accepted.JobId!, CancellationToken.None))!;
        progress.Status.Should().Be(ProgressStatus.Completed);
        progress.RowsTotal.Should().Be(2);
        progress.RowsMaterialized.Should().Be(2);
        progress.LayerId.Should().BeGreaterThan(0);
        _layerId = progress.LayerId;
        completed.CommittedEffectReferences.Should().Contain($"layer-publish:{_layerId}:2:2");
        (await GetManagedStoreRowCountAsync(_layerId!.Value)).Should().Be(2);
    }
}
