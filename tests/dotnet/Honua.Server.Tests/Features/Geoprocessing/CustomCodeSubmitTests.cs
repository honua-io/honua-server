// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Geoprocessing;
using Honua.Geoprocessing.CustomCode;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing;

/// <summary>
/// Server-half proof for the custom-code geoprocessing control path (Phase 1): the
/// submit gate validates the customcode.* contract (SHA-only git ref, https +
/// repo-allowlist, declared_scope ⊆ owner), mints a scoped job-bound token scoped to
/// the declared scope, injects it as env.HONUA_JOB_TOKEN, and revokes it on terminal.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.GPServer)]
[Operation(Operations.Security)]
public sealed class CustomCodeSubmitTests
{
    private const string ValidSha = "0123456789abcdef0123456789abcdef01234567";
    private const string RepoUrl = "https://github.com/honua-io/example.git";

    private readonly IExecutionJobStore _jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
    private readonly IJobQueue _jobQueue = Substitute.For<IJobQueue>();
    private readonly IUniversalProgressStore _progressStore = Substitute.For<IUniversalProgressStore>();
    private readonly IJobCancellationNotifier _cancellationNotifier = Substitute.For<IJobCancellationNotifier>();
    private readonly IOperatorAuthorizationEvaluator _authEvaluator = Substitute.For<IOperatorAuthorizationEvaluator>();
    private readonly IOperatorApprovalEvaluator _approvalEvaluator = Substitute.For<IOperatorApprovalEvaluator>();
    private readonly ScopedJobTokenIssuer _issuer = new(new MemoryCache(new MemoryCacheOptions()), NullLogger<ScopedJobTokenIssuer>.Instance);

    public CustomCodeSubmitTests()
    {
        _authEvaluator
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Allowed()));
        _approvalEvaluator
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());
        _jobStore.TryCreateAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    // -----------------------------------------------------------------------
    // git_ref: full 40-hex SHA only
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_BranchRef_RejectedAtSubmit()
    {
        var sut = CreateService();
        var metadata = CustomCodeMetadata(gitRef: "main");

        var act = async () => await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), metadata);

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("40-character commit SHA"));
        await _jobStore.DidNotReceive().TryCreateAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_FullSha_Accepted()
    {
        var sut = CreateService();
        var job = await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), CustomCodeMetadata());

        job.Spec.RuntimeProfile.Should().Be(CustomCodeJobContract.RuntimeProfile);
    }

    // -----------------------------------------------------------------------
    // repo_url: https + allowlist policy
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_RepoNotOnAllowlist_Rejected()
    {
        var sut = CreateService(options => options.RepoAllowlist = ["git.internal.example"]);
        var metadata = CustomCodeMetadata(repoUrl: "https://evil.example/x.git");

        var act = async () => await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), metadata);

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("not on the configured repository allowlist"));
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_NonHttpsRepo_Rejected()
    {
        var sut = CreateService(options => options.RepoPolicy = CustomCodeRepoPolicy.Open);
        var metadata = CustomCodeMetadata(repoUrl: "http://github.com/honua-io/example.git");

        var act = async () => await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), metadata);

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("https"));
    }

    // -----------------------------------------------------------------------
    // declared_scope ⊆ owner
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_DeclaredScopeExceedsOwner_Rejected()
    {
        var sut = CreateService();
        // Owner can only read 'parcels'; declaring write exceeds reach.
        var metadata = CustomCodeMetadata(
            declaredScope: """[{"serviceId":"parcels","access":"write"}]""");

        var act = async () => await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), metadata);

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("exceeds the submitter's permissions"));
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_DeclaredScopeWithinOwner_PinsSnapshot()
    {
        var sut = CreateService();
        var metadata = CustomCodeMetadata(
            declaredScope: """[{"serviceId":"parcels","access":"read"}]""");

        var job = await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), metadata);

        job.Audit.CustomCodeOwnerScope.Should().NotBeNull();
        job.Audit.CustomCodeOwnerScope!.DeclaredScope.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new JobResourceScopeEntry("parcels", null, JobResourceAccess.Read));
    }

    // -----------------------------------------------------------------------
    // token mint + injection
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_MintsScopedTokenInjectedAsEnv_BoundToJobAndScope()
    {
        var sut = CreateService();
        var metadata = CustomCodeMetadata(
            declaredScope: """[{"serviceId":"parcels","layerId":"lots","access":"write"}]""");

        // Owner can write parcels/lots so the declared write is reachable.
        var job = await sut.SubmitJobAsync(CustomCodePlan(), null, WriterPrincipal(), metadata);

        job.Spec.Parameters.Should().ContainKey(CustomCodeJobContract.JobTokenEnvParam);
        job.Spec.Parameters.Should().ContainKey(CustomCodeJobContract.BaseUrlEnvParam)
            .WhoseValue.Should().Be("https://api.honua.test");
        // Server-set output prefix is present and per-job.
        job.Spec.Parameters.Should().ContainKey(CustomCodeJobContract.OutputPrefixParam)
            .WhoseValue.Should().Contain(job.OperationId);

        // The injected token validates ONLY inside this job's context and carries the
        // declared scope as the frozen grant (ResourceScope = declared_scope).
        var token = job.Spec.Parameters[CustomCodeJobContract.JobTokenEnvParam];
        var validation = await _issuer.ValidateAsync(token, job.OperationId, CancellationToken.None);
        validation.Should().NotBeNull();
        validation!.JobId.Should().Be(job.OperationId);
        validation.Principal.HasClaim("permission", "write:parcels/lots").Should().BeTrue();

        // A different job id must NOT validate the token (job-binding).
        (await _issuer.ValidateAsync(token, "gp-other", CancellationToken.None)).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_ServerSetsOutputPrefix_OverridingCaller()
    {
        var sut = CreateService();
        var metadata = CustomCodeMetadata();
        metadata[CustomCodeJobContract.OutputPrefixParam] = "s3://attacker/loot";

        var job = await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), metadata);

        job.Spec.Parameters[CustomCodeJobContract.OutputPrefixParam].Should().NotContain("attacker");
        job.Spec.Parameters[CustomCodeJobContract.OutputPrefixParam].Should().Contain(job.OperationId);
    }

    // -----------------------------------------------------------------------
    // routing
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_RoutesToCustomCodeWorkload()
    {
        var workloadRegistry = Substitute.For<IExecutionJobDefinitionRegistry>();
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        workloadRegistry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            // The lean GP workload — must NOT be selected for a custom-code job.
            new ExecutionJobDefinition
            {
                WorkloadId = "gp-remote",
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                WorkloadName = "GP remote",
                RuntimeProfile = "py311"
            },
            new ExecutionJobDefinition
            {
                WorkloadId = "customcode-remote",
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "aws-batch",
                WorkloadName = "Custom-code remote",
                ArtifactReference = "ecr/honua-customcode-py:latest",
                RuntimeProfile = CustomCodeJobContract.RuntimeProfile,
                Parameters = new Dictionary<string, string>
                {
                    ["batch.job_definition_arn"] = "arn:aws:batch:us-east-1:1:job-definition/customcode:1",
                    ["batch.job_queue_arn"] = "arn:aws:batch:us-east-1:1:job-queue/customcode"
                }
            }
        });
        backend.StartAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(new BatchComputeSubmissionResult
            {
                Status = ExecutionJobStatus.Provisioning,
                ProviderOperationId = "job-cc-1",
                Message = "Submitted"
            });

        var sut = CreateService(workloadRegistry: workloadRegistry, backends: [backend]);
        var job = await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), CustomCodeMetadata());

        job.Spec.WorkloadId.Should().Be("customcode-remote");
        job.Spec.RuntimeProfile.Should().Be(CustomCodeJobContract.RuntimeProfile);
        job.Spec.Parameters.Should().ContainKey("batch.job_definition_arn");
        // env injection survives onto the spec the backend submits.
        job.Spec.Parameters.Should().ContainKey(CustomCodeJobContract.JobTokenEnvParam);
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    private GeoprocessingJobService CreateService(
        Action<CustomCodeOptions>? configure = null,
        IExecutionJobDefinitionRegistry? workloadRegistry = null,
        IEnumerable<IBatchComputeBackend>? backends = null)
    {
        var options = new CustomCodeOptions
        {
            RepoPolicy = CustomCodeRepoPolicy.OrgAllowlist,
            RepoAllowlist = ["github.com"],
            ApiBaseUrl = "https://api.honua.test",
            OutputPrefixRoot = "s3://honua-customcode/outputs"
        };
        configure?.Invoke(options);

        return new GeoprocessingJobService(
            _progressStore, [_cancellationNotifier],
            _authEvaluator, _approvalEvaluator,
            new BuiltInProcessCatalog(),
            NullLogger<GeoprocessingJobService>.Instance,
            DefaultExecutorOptions,
            _jobStore, _jobQueue,
            workloadRegistry: workloadRegistry,
            backends: backends,
            scopedJobTokenIssuer: _issuer,
            customCodeOptions: new StaticOptionsMonitor<CustomCodeOptions>(options));
    }

    private static Dictionary<string, string> CustomCodeMetadata(
        string gitRef = ValidSha,
        string repoUrl = RepoUrl,
        string? declaredScope = null)
    {
        var metadata = new Dictionary<string, string>
        {
            [CustomCodeJobContract.RuntimeParam] = "python",
            [CustomCodeJobContract.RepoUrlParam] = repoUrl,
            [CustomCodeJobContract.GitRefParam] = gitRef,
            [CustomCodeJobContract.EntrypointParam] = "pkg.module:run",
            [CustomCodeJobContract.DepsManifestParam] = "requirements.txt",
            [CustomCodeJobContract.ParamsJsonParam] = """{"k":"v"}"""
        };
        if (declaredScope is not null)
        {
            metadata[CustomCodeJobContract.DeclaredScopeParam] = declaredScope;
        }

        return metadata;
    }

    private static AnalysisPlan CustomCodePlan() => new()
    {
        PlanId = "cc-plan-1",
        IntentId = "cc-intent-1",
        Steps = [new AnalysisPlanStep { StepId = "step-1", Kind = AnalysisPlanStepKind.Geoprocess }]
    };

    // Owner can READ parcels (read:parcels) but cannot write it.
    private static ClaimsPrincipal OwnerPrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "alice"),
                new Claim(TenantClaimType, "tenant-A"),
                new Claim("permission", "read:parcels")
            ],
            "Test"));

    // Writer can WRITE parcels (service-wide, via the data-editor:parcels role),
    // which covers the parcels/lots layer. The service-scoped editor role is the
    // reach grammar the Phase-0 issuer freezes the minted token against.
    private static ClaimsPrincipal WriterPrincipal()
        => new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "bob"),
                new Claim(TenantClaimType, "tenant-A"),
                new Claim(ClaimTypes.Role, "data-editor:parcels")
            ],
            "Test"));

    private const string TenantClaimType = "tenant_id";

    private static readonly IOptionsMonitor<GeoprocessingExecutorOptions> DefaultExecutorOptions =
        new StaticOptionsMonitor<GeoprocessingExecutorOptions>(new GeoprocessingExecutorOptions());

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
