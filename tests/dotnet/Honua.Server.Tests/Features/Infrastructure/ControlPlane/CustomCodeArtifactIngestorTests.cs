// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing.CustomCode;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Collection("Redis")]
public sealed class CustomCodeArtifactIngestorTests(RedisFixture fixture)
{
    [IntegrationTest]
    public async Task CrashAfterProviderAcceptance_RecoversDurableAttemptCapabilityWithoutReminting()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var jobs = new RedisExecutionJobStore(redis, NullLogger<RedisExecutionJobStore>.Instance);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var issuer = new ScopedJobTokenIssuer(cache, NullLogger<ScopedJobTokenIssuer>.Instance);
        var client = new StubAwsBatchJobClient { SubmitException = new InjectedDispatchCrashException() };
        var backend = new AwsBatchComputeBackend(client, Options.Create(new AwsBatchExecutionOptions()),
            NullLogger<AwsBatchComputeBackend>.Instance, issuer);
        var job = AwsBatchComputeBackendTests.CreateJob() with
        {
            Audit = new OperationAuditInfo { CustomCodeOwnerScope = new CustomCodeOwnerScope("owner", "tenant", [], [], []) }
        };
        await jobs.TryCreateAsync(job);
        job = (await jobs.GetAsync(job.OperationId))!;
        var progress = Substitute.For<IUniversalProgressStore>();
        await Assert.ThrowsAsync<InjectedDispatchCrashException>(() => ExecutionJobSubmissionHelper.StartOnRemoteBackendAsync(
            job, backend, jobs, progress, TimeSpan.FromDays(1), null, CancellationToken.None));
        var provisioning = (await jobs.GetAsync(job.OperationId))!;
        provisioning.Status.Should().Be(ExecutionJobStatus.Provisioning);
        provisioning.AttemptCount.Should().Be(0);
        var capability = provisioning.Spec.Parameters["env.HONUA_JOB_TOKEN"];
        client.LastSubmission!.EnvironmentOverrides.Single(entry => entry.Name == "HONUA_JOB_TOKEN").Value.Should().Be(capability);
        (await issuer.ValidateAsync(capability, job.OperationId, CancellationToken.None))!.Attempt.Should().Be(1);
        client.NextListJobsResult = [new AwsBatchJobState { JobId = "accepted-before-crash", Status = "RUNNING" }];
        var reconciler = new ExecutionJobReconciler(jobs, [backend], progress, NullLogger<ExecutionJobReconciler>.Instance);
        await reconciler.ReconcileExecutionJobAsync(job.OperationId);
        var recovered = (await jobs.GetAsync(job.OperationId))!;
        recovered.Status.Should().Be(ExecutionJobStatus.Running);
        recovered.AttemptCount.Should().Be(1);
        recovered.ProviderOperationId.Should().Be("accepted-before-crash");
        recovered.Spec.Parameters["env.HONUA_JOB_TOKEN"].Should().Be(capability);
        (await issuer.ValidateAsync(capability, job.OperationId, CancellationToken.None))!.Attempt.Should().Be(recovered.AttemptCount);
        await issuer.RevokeAsync(capability, CancellationToken.None);
        await jobs.TrySetAsync(recovered with { Status = ExecutionJobStatus.Cancelled, CompletedAt = DateTimeOffset.UtcNow }, TimeSpan.FromMinutes(1));
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RetryGuard_RetainsChargedReceiptsOrRefusesRetry(bool allowed)
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var jobs = new RedisExecutionJobStore(redis, NullLogger<RedisExecutionJobStore>.Instance);
        var client = new StubAwsBatchJobClient { NextDescribeResult = new AwsBatchJobState { JobId = "failed-attempt", Status = "FAILED" } };
        var backend = new AwsBatchComputeBackend(client, NullLogger<AwsBatchComputeBackend>.Instance);
        var job = AwsBatchComputeBackendTests.CreateJob(providerOperationId: "failed-attempt", status: ExecutionJobStatus.Running) with
        {
            AttemptCount = 1,
            RetryPolicy = new JobRetryPolicy { MaxAttempts = 3 },
            ArtifactReferences = ["charged-immutable-receipt"]
        };
        await jobs.TryCreateAsync(job);
        var guard = Substitute.For<IExecutionJobRetryGuard>();
        guard.EvaluateRetry(Arg.Any<ExecutionJobRecord>()).Returns(new ExecutionJobRetryDecision
        {
            Allowed = allowed, ArtifactReferences = allowed ? job.ArtifactReferences : [], RefusalReason = "No reserved journal budget remains."
        });
        var reconciler = new ExecutionJobReconciler(jobs, [backend], Substitute.For<IUniversalProgressStore>(),
            NullLogger<ExecutionJobReconciler>.Instance, retryGuards: [guard]);
        await reconciler.ReconcileExecutionJobAsync(job.OperationId);
        var result = (await jobs.GetAsync(job.OperationId))!;
        result.ArtifactReferences.Should().Equal("charged-immutable-receipt");
        result.Status.Should().Be(allowed ? ExecutionJobStatus.Queued : ExecutionJobStatus.Failed);
        if (!allowed)
        {
            result.ErrorMessage.Should().Be("No reserved journal budget remains.");
        }
        await jobs.TrySetAsync(result with { Status = ExecutionJobStatus.Cancelled }, TimeSpan.FromMinutes(1));
    }

    private sealed class InjectedDispatchCrashException : Exception
    {
    }

    [IntegrationTest]
    public async Task ImmutableJournal_RetryProjectsOnceWithTrustedAttemptAndRejectsChangedBytes()
    {
        await using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.ConnectionString);
        var logs = new RedisExecutionLogStore(redis, NullLogger<RedisExecutionLogStore>.Instance);
        var bytes = Encoding.UTF8.GetBytes("""
            {"v":1,"kind":"phase","seq":0,"attempt":"untrusted","phase":"execute"}
            {"v":1,"kind":"failure","seq":1,"attempt":"untrusted","error":{"type":"ToolError","message":"LONG_FAILURE"}}
            {"v":1,"kind":"terminal","seq":2,"attempt":"untrusted","status":"failed","exitCode":1}
            """.Replace("LONG_FAILURE", new string('x', 10000), StringComparison.Ordinal));
        var job = AwsBatchComputeBackendTests.CreateJob() with { AttemptCount = 3 };
        var artifact = new CustomCodeArtifactDescriptor
        {
            JobId = job.OperationId, Attempt = 3, Name = HarnessEvents.FileName,
            ObjectKey = "server-owned/journal", ContentType = "application/x-ndjson",
            Provider = CloudStorageProvider.AwsS3, SizeBytes = bytes.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), State = "available"
        };
        job = job with { ArtifactReferences = [artifact.Serialize()] };
        var jobs = Substitute.For<IExecutionJobStore>();
        jobs.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(job);
        var storage = Substitute.For<ICloudFileStorage>();
        storage.Provider.Returns(CloudStorageProvider.AwsS3);
        storage.DownloadAsync(artifact.ObjectKey, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(bytes));
        using var services = new ServiceCollection().AddSingleton(jobs).AddSingleton(storage)
            .AddSingleton<IExecutionLogStore>(logs).BuildServiceProvider();
        var ingestor = new CustomCodeArtifactIngestor(services);
        var key = "controlplane:job:log:" + job.OperationId;
        try
        {
            await ingestor.IngestAsync(job, artifact, CancellationToken.None);
            await ingestor.IngestAsync(job, artifact, CancellationToken.None);
            var tail = await logs.TailAsync(job.OperationId, 10);
            tail.TotalCount.Should().Be(3);
            tail.Items.Should().OnlyContain(entry => entry.Metadata![HarnessEvents.AttemptKey] == "3");
            tail.Items[1].Metadata![HarnessEvents.KindKey].Should().Be("failure");
            tail.Items[1].Metadata!["honua.log.truncated"].Should().Be("true");
            tail.Items[^1].Metadata![HarnessEvents.KindKey].Should().Be("terminal");
            tail.Items[^1].Metadata!["honua.event.status"].Should().Be("failed");
            storage.DownloadAsync(artifact.ObjectKey, Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(new byte[bytes.Length]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ingestor.IngestAsync(job, artifact, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => ingestor.IngestAsync(job, artifact with { Attempt = 2 }, CancellationToken.None));
            (await logs.TailAsync(job.OperationId, 10)).TotalCount.Should().Be(3);
        }
        finally
        {
            await redis.GetDatabase().KeyDeleteAsync([key, "{" + key + "}:state"]);
        }
    }
}
