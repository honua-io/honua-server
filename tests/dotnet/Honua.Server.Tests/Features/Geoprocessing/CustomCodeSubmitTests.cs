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
    // param -> env contract (#2191): the customcode.* spec params must reach the
    // harness as env.CUSTOMCODE_* so AwsBatchComputeBackend surfaces them as the
    // CUSTOMCODE_* container env the harness reads. Pins the key map end-to-end.
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_ProjectsParamsToCustomCodeEnvForHarness()
    {
        var sut = CreateService();
        var metadata = CustomCodeMetadata(
            declaredScope: """[{"serviceId":"parcels","access":"read"}]""");

        var job = await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), metadata);

        // Every present customcode.* parameter is projected to env.CUSTOMCODE_* with
        // the same value (output_prefix is server-set, so it tracks the job id).
        job.Spec.Parameters[CustomCodeJobContract.ToEnvParamKey(CustomCodeJobContract.RuntimeEnvName)]
            .Should().Be("python");
        job.Spec.Parameters[CustomCodeJobContract.ToEnvParamKey(CustomCodeJobContract.RepoUrlEnvName)]
            .Should().Be(RepoUrl);
        job.Spec.Parameters[CustomCodeJobContract.ToEnvParamKey(CustomCodeJobContract.GitRefEnvName)]
            .Should().Be(ValidSha);
        job.Spec.Parameters[CustomCodeJobContract.ToEnvParamKey(CustomCodeJobContract.EntrypointEnvName)]
            .Should().Be("pkg.module:run");
        job.Spec.Parameters[CustomCodeJobContract.ToEnvParamKey(CustomCodeJobContract.DepsManifestEnvName)]
            .Should().Be("requirements.txt");
        job.Spec.Parameters[CustomCodeJobContract.ToEnvParamKey(CustomCodeJobContract.ParamsJsonEnvName)]
            .Should().Be("""{"k":"v"}""");
        job.Spec.Parameters[CustomCodeJobContract.ToEnvParamKey(CustomCodeJobContract.OutputPrefixEnvName)]
            .Should().Be(job.Spec.Parameters[CustomCodeJobContract.OutputPrefixParam])
            .And.Contain(job.OperationId);
        job.Spec.Parameters[CustomCodeJobContract.ToEnvParamKey(CustomCodeJobContract.DeclaredScopeEnvName)]
            .Should().Be("""[{"serviceId":"parcels","access":"read"}]""");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_OmitsCustomCodeEnvForAbsentOptionalParams()
    {
        var sut = CreateService();
        var metadata = CustomCodeMetadata();
        // Drop the two optional params; they must not appear as env.CUSTOMCODE_*.
        metadata.Remove(CustomCodeJobContract.DepsManifestParam);
        metadata.Remove(CustomCodeJobContract.ParamsJsonParam);

        var job = await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), metadata);

        job.Spec.Parameters.Should().NotContainKey(
            CustomCodeJobContract.ToEnvParamKey(CustomCodeJobContract.DepsManifestEnvName));
        job.Spec.Parameters.Should().NotContainKey(
            CustomCodeJobContract.ToEnvParamKey(CustomCodeJobContract.ParamsJsonEnvName));
        // Required params still project.
        job.Spec.Parameters.Should().ContainKey(
            CustomCodeJobContract.ToEnvParamKey(CustomCodeJobContract.RepoUrlEnvName));
    }

    [UnitTest]
    [Operation(Operations.ContractTesting)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public void CustomCodeJobContract_ParameterToEnvName_MatchesHarnessKeys()
    {
        // The harness (docker/worker-customcode-python/harness/jobspec.py) reads these
        // exact CUSTOMCODE_* names. Pinning the map here catches parallel-build drift
        // between the server and harness halves of the contract (#2191).
        CustomCodeJobContract.ParameterToEnvName.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["customcode.runtime"] = "CUSTOMCODE_RUNTIME",
            ["customcode.repo_url"] = "CUSTOMCODE_REPO_URL",
            ["customcode.git_ref"] = "CUSTOMCODE_GIT_REF",
            ["customcode.entrypoint"] = "CUSTOMCODE_ENTRYPOINT",
            ["customcode.deps_manifest"] = "CUSTOMCODE_DEPS_MANIFEST",
            ["customcode.params_json"] = "CUSTOMCODE_PARAMS_JSON",
            ["customcode.output_prefix"] = "CUSTOMCODE_OUTPUT_PREFIX",
            ["customcode.declared_scope"] = "CUSTOMCODE_DECLARED_SCOPE",
        });

        // Each projection produces an env. spec key the Batch backend strips to the
        // bare CUSTOMCODE_* name.
        CustomCodeJobContract.ToEnvParamKey(CustomCodeJobContract.RepoUrlEnvName)
            .Should().Be("env.CUSTOMCODE_REPO_URL");
    }

    // -----------------------------------------------------------------------
    // runtime = dotnet (Phase 2)
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_DotnetRuntime_Accepted()
    {
        var sut = CreateService();
        var metadata = DotnetCustomCodeMetadata();

        var job = await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), metadata);

        // dotnet routes through the same custom-code runtime profile as python; the
        // runtime selector flows through verbatim for the iac to resolve to an image.
        job.Spec.RuntimeProfile.Should().Be(CustomCodeJobContract.RuntimeProfile);
        job.Spec.Parameters.Should().ContainKey(CustomCodeJobContract.RuntimeParam)
            .WhoseValue.Should().Be(CustomCodeJobContract.DotnetRuntime);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_DotnetRuntime_RoutesToCustomCodeWorkload()
    {
        var workloadRegistry = Substitute.For<IExecutionJobDefinitionRegistry>();
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("aws-batch");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        workloadRegistry.ListAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
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
                ArtifactReference = "ecr/honua-customcode:latest",
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
                ProviderOperationId = "job-cc-dotnet-1",
                Message = "Submitted"
            });

        var sut = CreateService(workloadRegistry: workloadRegistry, backends: [backend]);
        var job = await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), DotnetCustomCodeMetadata());

        job.Spec.WorkloadId.Should().Be("customcode-remote");
        job.Spec.RuntimeProfile.Should().Be(CustomCodeJobContract.RuntimeProfile);
        job.Spec.Parameters.Should().ContainKey(CustomCodeJobContract.RuntimeParam)
            .WhoseValue.Should().Be(CustomCodeJobContract.DotnetRuntime);
        job.Spec.Parameters.Should().ContainKey(CustomCodeJobContract.JobTokenEnvParam);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_DotnetRuntime_PythonStyleEntrypoint_Rejected()
    {
        var sut = CreateService();
        // A 'module:func' (python) entrypoint is invalid for the dotnet runtime.
        var metadata = DotnetCustomCodeMetadata(entrypoint: "pkg.module:run");

        var act = async () => await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), metadata);

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("Assembly::Namespace.Type"));
        await _jobStore.DidNotReceive().TryCreateAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_UnknownRuntime_Rejected()
    {
        var sut = CreateService();
        var metadata = DotnetCustomCodeMetadata();
        metadata[CustomCodeJobContract.RuntimeParam] = "ruby";

        var act = async () => await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), metadata);

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("runtime must be"));
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_DotnetRuntime_BranchRef_RejectedAtSubmit()
    {
        var sut = CreateService();
        var metadata = DotnetCustomCodeMetadata(gitRef: "main");

        var act = async () => await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), metadata);

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("40-character commit SHA"));
    }

    // -----------------------------------------------------------------------
    // Phase 3: per-tenant repository allowlist
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_RepoOnOrgButNotTenantAllowlist_Rejected()
    {
        // Org allows github.com; tenant-A is additionally constrained to a specific
        // org path, so a bare github.com/honua-io repo for tenant-A is rejected.
        var sut = CreateService(options =>
        {
            options.RepoAllowlist = ["github.com"];
            options.TenantRepoAllowlist["tenant-A"] = ["github.com/trusted-org"];
        });

        var act = async () => await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), CustomCodeMetadata());

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("tenant 'tenant-A'"));
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_RepoOnTenantAllowlist_Accepted()
    {
        var sut = CreateService(options =>
        {
            options.RepoAllowlist = ["github.com"];
            options.TenantRepoAllowlist["tenant-A"] = ["github.com/honua-io"];
        });

        var job = await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), CustomCodeMetadata());

        job.Spec.RuntimeProfile.Should().Be(CustomCodeJobContract.RuntimeProfile);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_TenantWithoutListFallsThroughToOrgAllowlist()
    {
        // A tenant absent from the per-tenant map is constrained by the org list
        // alone — behavior-preserving for tenants the operator has not scoped.
        var sut = CreateService(options =>
        {
            options.RepoAllowlist = ["github.com"];
            options.TenantRepoAllowlist["tenant-OTHER"] = ["github.com/nobody"];
        });

        var job = await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), CustomCodeMetadata());

        job.Spec.RuntimeProfile.Should().Be(CustomCodeJobContract.RuntimeProfile);
    }

    // -----------------------------------------------------------------------
    // Phase 3: signed-only commit-signature policy
    // -----------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_SignedOnly_UnsignedCommit_Rejected()
    {
        var verifier = new StubSignatureVerifier(CommitSignatureResult.Unverifiable("commit is not signed"));
        var sut = CreateService(
            options =>
            {
                options.RepoPolicy = CustomCodeRepoPolicy.SignedOnly;
                options.TrustedSignerKeys = ["ABCD1234"];
            },
            signatureVerifier: verifier);

        var act = async () => await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), CustomCodeMetadata());

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("signed-only policy") && e.Message.Contains("not signed"));
        verifier.LastSha.Should().Be(ValidSha);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_SignedOnly_WronglySignedCommit_Rejected()
    {
        // Valid signature, but by an untrusted key.
        var verifier = new StubSignatureVerifier(new CommitSignatureResult(IsSignatureValid: true, SignerKeyId: "DEADBEEF", Detail: null));
        var sut = CreateService(
            options =>
            {
                options.RepoPolicy = CustomCodeRepoPolicy.SignedOnly;
                options.TrustedSignerKeys = ["ABCD1234"];
            },
            signatureVerifier: verifier);

        var act = async () => await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), CustomCodeMetadata());

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("not on the configured trusted-signer list"));
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_SignedOnly_TrustedSignedCommit_Accepted()
    {
        var verifier = new StubSignatureVerifier(new CommitSignatureResult(IsSignatureValid: true, SignerKeyId: "abcd1234", Detail: null));
        var sut = CreateService(
            options =>
            {
                options.RepoPolicy = CustomCodeRepoPolicy.SignedOnly;
                options.RepoAllowlist = ["github.com"];
                options.TrustedSignerKeys = ["ABCD1234"]; // case-insensitive match
            },
            signatureVerifier: verifier);

        var job = await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), CustomCodeMetadata());

        job.Spec.RuntimeProfile.Should().Be(CustomCodeJobContract.RuntimeProfile);
        verifier.LastRepo!.Host.Should().Be("github.com");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_SignedOnly_NoTrustedKeysConfigured_FailsClosed()
    {
        // Signed-only with an empty trusted-key list rejects everything, regardless of
        // verifier outcome — the verifier is never consulted.
        var verifier = new StubSignatureVerifier(new CommitSignatureResult(IsSignatureValid: true, SignerKeyId: "ABCD1234", Detail: null));
        var sut = CreateService(
            options =>
            {
                options.RepoPolicy = CustomCodeRepoPolicy.SignedOnly;
                options.TrustedSignerKeys = [];
            },
            signatureVerifier: verifier);

        var act = async () => await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), CustomCodeMetadata());

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("no trusted signer keys are configured"));
        verifier.LastSha.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_CustomCode_SignedOnly_DefaultVerifier_FailsClosed()
    {
        // No verifier registered (the DI default is the fail-closed verifier): even
        // with trusted keys configured, every signed-only submission is rejected
        // because the default verifier reports the commit unverifiable.
        var sut = CreateService(options =>
        {
            options.RepoPolicy = CustomCodeRepoPolicy.SignedOnly;
            options.TrustedSignerKeys = ["ABCD1234"];
        });

        var act = async () => await sut.SubmitJobAsync(CustomCodePlan(), null, OwnerPrincipal(), CustomCodeMetadata());

        await act.Should().ThrowAsync<Exception>()
            .Where(e => e.Message.Contains("no commit-signature verifier is configured"));
    }

    // -----------------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------------

    private GeoprocessingJobService CreateService(
        Action<CustomCodeOptions>? configure = null,
        IExecutionJobDefinitionRegistry? workloadRegistry = null,
        IEnumerable<IBatchComputeBackend>? backends = null,
        ICustomCodeCommitSignatureVerifier? signatureVerifier = null)
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
            customCodeOptions: new StaticOptionsMonitor<CustomCodeOptions>(options),
            customCodeSignatureVerifier: signatureVerifier);
    }

    /// <summary>A test verifier that returns a fixed signature outcome.</summary>
    private sealed class StubSignatureVerifier(CommitSignatureResult result) : ICustomCodeCommitSignatureVerifier
    {
        public Uri? LastRepo { get; private set; }
        public string? LastSha { get; private set; }

        public ValueTask<CommitSignatureResult> VerifyAsync(Uri repoUrl, string commitSha, CancellationToken cancellationToken)
        {
            LastRepo = repoUrl;
            LastSha = commitSha;
            return ValueTask.FromResult(result);
        }
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

    private static Dictionary<string, string> DotnetCustomCodeMetadata(
        string gitRef = ValidSha,
        string repoUrl = RepoUrl,
        string entrypoint = "MyTool::My.Namespace.BufferTool",
        string? declaredScope = null)
    {
        var metadata = new Dictionary<string, string>
        {
            [CustomCodeJobContract.RuntimeParam] = CustomCodeJobContract.DotnetRuntime,
            [CustomCodeJobContract.RepoUrlParam] = repoUrl,
            [CustomCodeJobContract.GitRefParam] = gitRef,
            [CustomCodeJobContract.EntrypointParam] = entrypoint,
            [CustomCodeJobContract.DepsManifestParam] = "tool/MyTool.csproj",
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
