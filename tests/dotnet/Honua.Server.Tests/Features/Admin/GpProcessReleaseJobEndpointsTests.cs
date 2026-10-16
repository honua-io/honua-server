// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.CustomCode;
using Honua.Geoprocessing.ProcessRelease;
using Honua.Geoprocessing.Release;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>Real HTTP submission through the canonical job admission and release pinning gates.</summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Configuration)]
public sealed class GpProcessReleaseJobEndpointsTests : IAsyncLifetime
{
    private const string Arn = "arn:aws:batch:us-west-2:123456789012:job-definition/buffer-features:7";
    private static readonly string Image = "ghcr.io/honua-io/honua-gp-worker@sha256:" + new string('b', 64);
    private readonly ConcurrentDictionary<string, ExecutionJobRecord> _jobs = new(StringComparer.Ordinal);
    private readonly InMemoryGpProcessReleaseStore _releases = new();
    private readonly IExecutionJobDefinitionRegistry _registry = Substitute.For<IExecutionJobDefinitionRegistry>();
    private readonly IBatchComputeBackend _backend = Substitute.For<IBatchComputeBackend>();
    private readonly WebAppFixture _fixture;
    private readonly MemoryCache _tokenCache = new(new MemoryCacheOptions());
    private HttpClient _client = null!;
    private string _firstDigest = string.Empty;
    private bool _denyCustomCode;

    public GpProcessReleaseJobEndpointsTests()
    {
        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_jobs.GetValueOrDefault(call.ArgAt<string>(0))));
        jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => _jobs.TryAdd(call.ArgAt<ExecutionJobRecord>(0).OperationId, call.ArgAt<ExecutionJobRecord>(0)));
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var job = call.ArgAt<ExecutionJobRecord>(0);
                _jobs[job.OperationId] = job with { Version = job.Version + 1 };
                return true;
            });
        jobStore.SetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var job = call.ArgAt<ExecutionJobRecord>(0);
                _jobs[job.OperationId] = job with { Version = job.Version + 1 };
                return Task.CompletedTask;
            });
        _registry.ListAsync(Arg.Any<CancellationToken>()).Returns(Definitions());
        _backend.BackendName.Returns("aws-batch");
        _backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        _backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeSubmissionResult { Status = ExecutionJobStatus.Provisioning, ProviderOperationId = "batch-job" });
        var authorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        authorization.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_denyCustomCode && call.ArgAt<OperatorAuthorizationRequest>(1).Operation == OperatorOperation.ExecuteCustomCode
                ? AccessDecision.Forbidden("Custom-code permission is required.") : AccessDecision.Allowed()));
        var approval = Substitute.For<IOperatorApprovalEvaluator>();
        approval.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>()).Returns(ApprovalRequirement.NotRequired());
        var issuer = new ScopedJobTokenIssuer(_tokenCache, NullLogger<ScopedJobTokenIssuer>.Instance);
        var storage = Substitute.For<ICloudFileStorage>();
        storage.Provider.Returns(CloudStorageProvider.AwsS3);
        var service = new GeoprocessingJobService(
            Substitute.For<IUniversalProgressStore>(), [Substitute.For<IJobCancellationNotifier>()],
            authorization, approval, new BuiltInProcessCatalog(), NullLogger<GeoprocessingJobService>.Instance,
            new StaticOptionsMonitor<GeoprocessingExecutorOptions>(new GeoprocessingExecutorOptions()),
            jobStore, Substitute.For<IJobQueue>(), workloadRegistry: _registry, backends: [_backend],
            scopedJobTokenIssuer: issuer, customCodeArtifactStorage: storage,
            customCodeOptions: new StaticOptionsMonitor<CustomCodeOptions>(new CustomCodeOptions
            {
                RepoPolicy = CustomCodeRepoPolicy.OrgAllowlist,
                RepoAllowlist = ["github.com"],
                ApiBaseUrl = "https://api.honua.test",
                OutputPrefixRoot = "s3://honua-customcode/outputs",
            }),
            releaseAdmission: new GpProcessReleaseAdmission(_releases, NullLogger<GpProcessReleaseAdmission>.Instance));
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IGeoprocessingJobService>();
                services.AddSingleton<IGeoprocessingJobService>(service);
                services.RemoveAll<ICloudFileStorage>();
                services.AddSingleton(storage);
            });
    }

    public async Task InitializeAsync()
    {
        _firstDigest = await ActivateAsync("1.0.0", 'a', 0);
        await _fixture.InitializeAsync();
        _client = _fixture.CreateAdminClient();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        _tokenCache.Dispose();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/gp/process-releases/bindings/{process}/jobs")]
    public async Task SubmitAndReplay_PinMatchingWorkerAndOriginalReleaseAcrossBindingChange()
    {
        _client.DefaultRequestHeaders.Add("Idempotency-Key", "deployment-smoke");
        using var submitted = await _client.PostAsJsonAsync("/api/v1/admin/gp/process-releases/bindings/buffer-features/jobs",
            new { @params = new { distance = 10 } });
        submitted.StatusCode.Should().Be(HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync());
        var body = await submitted.Content.ReadFromJsonAsync<JsonElement>();
        var jobId = body.GetProperty("jobId").GetString()!;
        submitted.Headers.Location!.OriginalString.Should().Be("/api/v1/admin/jobs/" + jobId);
        body.GetProperty("diagnosticsUrl").GetString().Should().Be("/api/v1/admin/jobs/" + jobId + "/diagnostics");
        body.GetProperty("artifactsUrl").GetString().Should().Be("/api/v1/admin/jobs/" + jobId + "/artifacts");
        var job = _jobs[jobId];
        job.Spec.WorkloadId.Should().Be("release-worker");
        job.Spec.Artifact.Should().Be(Image);
        job.Spec.Parameters["batch.queue"].Should().Be("release-queue");
        job.Spec.Parameters[CustomCodeJobContract.GitRefParam].Should().Be(new string('a', 40));
        job.Spec.Parameters[GpProcessReleaseAdmission.ReleaseLockDigestParam].Should().Be(_firstDigest);
        job.Spec.Parameters[GpProcessReleaseAdmission.AwsBatchJobDefinitionParam].Should().Be(Arn);
        job.Spec.Parameters[CustomCodeJobContract.OutputPrefixParam].Should().Contain(jobId);

        await ActivateAsync("2.0.0", 'c', 1);
        // Replay must survive retirement of the original workload, retain its original pin,
        // and treat the omitted scope on admission identically to an explicit empty scope.
        _registry.ListAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<ExecutionJobDefinition>());
        using var replay = await _client.PostAsJsonAsync("/api/v1/admin/gp/process-releases/bindings/buffer-features/jobs",
            new { @params = new { distance = 10 }, declaredScope = Array.Empty<object>() });
        replay.StatusCode.Should().Be(HttpStatusCode.Accepted, await replay.Content.ReadAsStringAsync());
        (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString().Should().Be(jobId);
        _jobs[jobId].Spec.Parameters[GpProcessReleaseAdmission.ReleaseLockDigestParam].Should().Be(_firstDigest);
        await _backend.Received(1).StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>());

        using var conflict = await _client.PostAsJsonAsync("/api/v1/admin/gp/process-releases/bindings/buffer-features/jobs",
            new { @params = new { distance = 20 }, declaredScope = Array.Empty<object>() });
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _jobs.Should().ContainSingle();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/gp/process-releases/bindings/{process}/jobs")]
    public async Task Submit_RequiresAdminAuthenticationAndSeparateCustomCodePermission()
    {
        using var anonymous = _fixture.CreateClient();
        using var unauthenticated = await anonymous.PostAsJsonAsync("/api/v1/admin/gp/process-releases/bindings/buffer-features/jobs", new { @params = new { } });
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _denyCustomCode = true;
        using var forbidden = await _client.PostAsJsonAsync("/api/v1/admin/gp/process-releases/bindings/buffer-features/jobs", new { @params = new { } });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, await forbidden.Content.ReadAsStringAsync());
        _jobs.Should().BeEmpty();
        await _backend.DidNotReceive().StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/gp/process-releases/bindings/{process}/jobs")]
    public async Task Submit_UnboundProcess_IsRefusedBeforeCreatingJob()
    {
        using var response = await _client.PostAsJsonAsync("/api/v1/admin/gp/process-releases/bindings/missing-process/jobs", new { @params = new { } });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("no active release binding");
        _jobs.Should().BeEmpty();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/gp/process-releases/bindings/{process}/jobs")]
    public async Task Submit_RejectsInvalidScopeAndServerControlFields()
    {
        foreach (var json in new[]
        {
            """{"params":[],"declaredScope":[]}""",
            """{"params":{},"declaredScope":null}""",
            """{"params":{},"declaredScope":[{"serviceId":"parcels","access":"admin"}]}""",
            """{"params":{},"declaredScope":[null]}""",
            """{"params":{},"outputPrefix":"s3://other/job"}""",
            """{"params":{},"sourceSha":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""",
            """{"params":{},"workerImage":"attacker:latest"}""",
        })
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await _client.PostAsync("/api/v1/admin/gp/process-releases/bindings/buffer-features/jobs", content);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, json);
        }

        _jobs.Should().BeEmpty();
    }

    private async Task<string> ActivateAsync(string version, char sha, int expectedVersion)
    {
        var document = new GpProcessReleaseLock
        {
            Process = new GpReleaseProcess
            {
                Name = "buffer-features",
                Version = version,
                Runtime = "python",
                Entrypoint = "buffer_features:execute",
                Contract = new GpReleaseContract { InputSchemaSha256 = "sha256:" + new string('1', 64), OutputSchemaSha256 = "sha256:" + new string('2', 64) },
            },
            Source = new GpReleaseSource { Repository = "honua-io/honua-samples", Sha = new string(sha, 40), DescriptorPath = "honua-gp.yaml" },
            DependencyLock = new GpReleaseFileDigest { Path = "requirements.lock", Sha256 = "sha256:" + new string('d', 64) },
            RuntimeImage = new GpReleaseRuntimeImage { Reference = Image, Digest = "sha256:" + new string('b', 64) },
            WorkflowVersion = "wf-2026.1-7",
            ConfigurationReferences = [],
            Targets = [new GpReleaseTarget { Kind = "aws-batch", WorkerImageDigest = "sha256:" + new string('b', 64), JobDefinitionName = "buffer-features" }],
            Compatibility = new GpReleaseCompatibility { ServerContractVersion = new GpReleaseContractWindow { Minimum = 1, Maximum = 2 } },
            Artifacts = [new GpReleaseArtifact { Name = "process.tar.gz", Sha256 = "sha256:" + new string('e', 64), SizeBytes = 1 }],
            ValidationReceipts = [new GpReleaseValidationReceipt { Name = "golden.xml", Sha256 = "sha256:" + new string('9', 64), Status = "pass" }],
            GeneratedAt = "2026-10-16T04:00:00Z",
        };
        var release = await _releases.PublishAsync(document, "operator");
        var activation = await _releases.ActivateAsync(new GpProcessBindingActivation
        {
            Process = "buffer-features",
            LockDigest = release.Digest,
            ExpectedBindingVersion = expectedVersion,
            Actor = "operator",
            Operation = "deploy",
            Targets = [new GpProcessBindingTargetIdentity { Kind = "aws-batch", JobDefinitionArn = Arn }],
        }, new GpProcessReleaseEnvironment
        {
            ServerContractVersion = 1,
            SupportedTargetKinds = ["aws-batch"],
            CustomCodeMayUseKubernetes = false,
            ReadyTargets = [new GpProcessBindingTarget { Kind = "aws-batch", Image = Image, JobDefinitionArn = Arn }],
        });
        activation.Activated.Should().BeTrue();
        return release.Digest;
    }

    private static ExecutionJobDefinition[] Definitions()
        => new[] { ("unrelated", "worker:latest", "wrong-queue"), ("release-worker", Image, "release-queue") }
            .Select(item => new ExecutionJobDefinition
            {
                WorkloadId = item.Item1,
                WorkloadName = item.Item1,
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                RuntimeProfile = "custom-code",
                ArtifactReference = item.Item2,
                Parameters = new Dictionary<string, string> { ["batch.queue"] = item.Item3, [GpProcessReleaseAdmission.AwsBatchJobDefinitionParam] = Arn },
            }).ToArray();

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
