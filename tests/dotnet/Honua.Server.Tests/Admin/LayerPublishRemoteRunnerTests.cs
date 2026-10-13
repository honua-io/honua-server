// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ProgressStatus = Honua.Core.Features.Infrastructure.Domain.OperationStatus;

namespace Honua.Server.Tests.Admin;

public sealed class LayerPublishRemoteRunnerTests
{
    private const string Connection = "Host=private-database;Password=must-not-be-persisted";

    [UnitTest]
    public async Task Submit_LambdaWithConfiguredBatch_UsesRemoteJobAndPreservesCoordinates()
    {
        using var fixture = new Fixture();
        using var scope = fixture.Scope("tenant-a");
        var admission = await SubmitAsync(scope.ServiceProvider);

        Assert.Equal(LayerPublishAdmissionKind.Queued, admission.Kind);
        var job = Assert.IsType<ExecutionJobRecord>(await fixture.Jobs.GetAsync(admission.JobId!));
        Assert.Equal(ExecutionJobKind.LayerPublish, job.Spec.Kind);
        Assert.Equal("honua-aws-batch", job.Spec.Backend);
        Assert.Equal(BatchComputeTargetKind.AwsBatch, job.Spec.TargetKind);
        Assert.Equal("managed-gp", job.Spec.WorkloadId);
        Assert.Equal("managed", job.Spec.RuntimeProfile);
        Assert.Equal("image@sha256:reviewed", job.Spec.Artifact);
        Assert.Equal("queue", job.Spec.Parameters["batch.job_queue_arn"]);
        Assert.Equal("definition:3", job.Spec.Parameters["batch.job_definition_arn.s"]);
        Assert.Equal("provider-job", job.ProviderOperationId);
        Assert.Equal(1, job.AttemptCount);
        Assert.True(LayerPublishJobPayload.TryParse(job.Spec.Parameters, out var payload, out _));
        Assert.Equal("tenant-a", payload!.TenantId);
        Assert.Equal("tenant_schema", payload.CurrentSchema);
        Assert.DoesNotContain("must-not-be-persisted", string.Join(" ", job.Spec.Parameters.Values), StringComparison.Ordinal);
        await fixture.Queue.DidNotReceive().EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>());
        await fixture.Publisher.DidNotReceive().PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Submit_RequestCancelledDuringRemoteDispatch_StillCommitsProviderIdentity()
    {
        using var fixture = new Fixture();
        using var scope = fixture.Scope("tenant-a");
        using var request = new CancellationTokenSource();
        fixture.Backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            request.Cancel();
            Assert.False(call.ArgAt<CancellationToken>(1).IsCancellationRequested);
            return new BatchComputeSubmissionResult { Status = ExecutionJobStatus.Queued, ProviderOperationId = "accepted" };
        });

        var admission = await SubmitAsync(scope.ServiceProvider, request.Token);
        Assert.Equal(LayerPublishAdmissionKind.Queued, admission.Kind);
        Assert.Equal("accepted", (await fixture.Jobs.GetAsync(admission.JobId!))!.ProviderOperationId);
    }

    [UnitTest]
    public async Task Submit_BackendThrows_CompensatesRecordAndProgressWithoutLeakingException()
    {
        using var fixture = new Fixture();
        using var scope = fixture.Scope("tenant-a");
        fixture.Backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<BatchComputeSubmissionResult>(new InvalidOperationException("private provider detail")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => SubmitAsync(scope.ServiceProvider));
        var job = Assert.Single((await fixture.Jobs.QueryAsync(new ExecutionJobQuery())).Items);
        Assert.Equal(ExecutionJobStatus.Failed, job.Status);
        var progress = await scope.ServiceProvider.GetRequiredService<ILayerPublishJobs>().GetAsync(job.OperationId, CancellationToken.None);
        Assert.Equal(ProgressStatus.Failed, progress!.Status);
        Assert.DoesNotContain("private", progress.ErrorMessage!, StringComparison.Ordinal);
    }

    [UnitTheory]
    [InlineData(ExecutionJobStatus.Failed, ProgressStatus.Failed)]
    [InlineData(ExecutionJobStatus.Cancelled, ProgressStatus.Cancelled)]
    public async Task Poll_TerminalRemoteRecord_ReportsTerminalState(ExecutionJobStatus status, ProgressStatus expected)
    {
        using var fixture = new Fixture();
        using var scope = fixture.Scope("tenant-a");
        var admission = await SubmitAsync(scope.ServiceProvider);
        var job = (await fixture.Jobs.GetAsync(admission.JobId!))!;
        await fixture.Jobs.SetAsync(job with { Status = status, CompletedAt = DateTimeOffset.UtcNow, ErrorMessage = "private provider detail" });

        var progress = await scope.ServiceProvider.GetRequiredService<ILayerPublishJobs>().GetAsync(job.OperationId, CancellationToken.None);
        Assert.Equal(expected, progress!.Status);
        Assert.Null(progress.LayerId);
        Assert.DoesNotContain("private", progress.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task Poll_OtherTenantAndAnonymousCannotReadKnownJob()
    {
        using var fixture = new Fixture(frozen: false, remote: false);
        using var owner = fixture.Scope("tenant-a");
        var admission = await SubmitAsync(owner.ServiceProvider);
        Assert.NotNull(await owner.ServiceProvider.GetRequiredService<ILayerPublishJobs>().GetAsync(admission.JobId!, CancellationToken.None));

        foreach (var tenant in new string?[] { "tenant-b", null })
        {
            using var other = fixture.Scope(tenant);
            Assert.Null(await other.ServiceProvider.GetRequiredService<ILayerPublishJobs>().GetAsync(admission.JobId!, CancellationToken.None));
        }
    }

    [UnitTheory]
    [InlineData(false, true, "managed")]
    [InlineData(true, false, "managed")]
    [InlineData(true, true, "native")]
    [InlineData(true, true, "custom-code")]
    public async Task Submit_LambdaWithoutCompatibleDurableRunner_IsRefused(bool durable, bool remote, string profile)
    {
        using var fixture = new Fixture(durable: durable, remote: remote, profile: profile);
        using var scope = fixture.Scope("tenant-a");
        Assert.Equal(LayerPublishAdmissionKind.Unavailable, (await SubmitAsync(scope.ServiceProvider)).Kind);
        Assert.Empty((await fixture.Jobs.QueryAsync(new ExecutionJobQuery())).Items);
        await fixture.Backend.DidNotReceive().StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>());
    }

    private static Task<LayerPublishAdmission> SubmitAsync(IServiceProvider services, CancellationToken cancellationToken = default)
        => services.GetRequiredService<ILayerPublishJobs>().SubmitAsync(
            Connection,
            Guid.Parse("68d396c9-3c83-4b2f-b048-6ea269a920ff"),
            new LayerPublishRequest { Schema = "public", Table = "roads", LayerName = "Roads" },
            new PublishLayerRequest { Schema = "public", Table = "roads", LayerName = "Roads" },
            "roads",
            cancellationToken);

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        internal InMemoryExecutionJobStore Jobs { get; } = new();
        internal IJobQueue Queue { get; } = Substitute.For<IJobQueue>();
        internal IBatchComputeBackend Backend { get; } = Substitute.For<IBatchComputeBackend>();
        internal ILayerPublishingService Publisher { get; } = Substitute.For<ILayerPublishingService>();

        internal Fixture(bool frozen = true, bool durable = true, bool remote = true, string profile = "managed")
        {
            Backend.BackendName.Returns("honua-aws-batch");
            Backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
            Backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>()).Returns(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Queued,
                ProviderOperationId = "provider-job",
            });
            Publisher.CountSourceRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(330000L);
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AWS_LAMBDA_FUNCTION_NAME"] = frozen ? "api" : null,
            }).Build());
            services.Configure<DurableJobSubstrateOptions>(options => { options.RedisConfigured = durable; options.RedisEntitled = durable; });
            services.Configure<LayerPublishingOptions>(options => options.AsyncPublishRowThreshold = 10000);
            services.Configure<ControlPlaneOptions>(options =>
            {
                if (remote)
                {
                    options.ExecutionWorkloads.Add(new ExecutionWorkloadOptions
                    {
                        WorkloadId = "managed-gp", WorkloadName = "managed-gp", Kind = ExecutionJobKind.Geoprocessing,
                        Backend = "honua-aws-batch", TargetKind = BatchComputeTargetKind.AwsBatch,
                        RuntimeProfile = profile, ArtifactReference = "image@sha256:reviewed",
                        Parameters = new Dictionary<string, string> { ["batch.job_queue_arn"] = "queue", ["batch.job_definition_arn.s"] = "definition:3" },
                    });
                }
            });
            services.AddSingleton(Publisher);
            services.AddSingleton(Backend);
            if (durable)
            {
                services.AddSingleton<IExecutionJobStore>(Jobs);
                services.AddSingleton(Queue);
            }
            services.AddSingleton<IUniversalProgressStore>(new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance));
            services.AddSingleton(Substitute.For<IHostApplicationLifetime>());
            services.AddScoped<RequestTenantContext>();
            services.AddScoped<ITenantContext>(provider => provider.GetRequiredService<RequestTenantContext>());
            services.AddScoped<ISchemaContext>(_ => new SchemaContext { CurrentSchema = "tenant_schema" });
            services.AddLayerPublishJobs();
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }

        internal IServiceScope Scope(string? tenant)
        {
            var scope = _provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<RequestTenantContext>().Set(tenant, TenantContextSource.Header);
            return scope;
        }

        public void Dispose() => _provider.Dispose();
    }
}
