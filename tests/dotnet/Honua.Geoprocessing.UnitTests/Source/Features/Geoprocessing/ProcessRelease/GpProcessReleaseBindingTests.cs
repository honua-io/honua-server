// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Geoprocessing.CustomCode;
using Honua.Geoprocessing.ProcessRelease;
using Honua.Geoprocessing.Release;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Geoprocessing.UnitTests.Features.Geoprocessing.ProcessRelease;

/// <summary>
/// Binding activation policy, the in-memory store's compare-and-set under concurrency, and
/// admission-time pinning (honua-server#6221): a release is activated exactly once per expected
/// version, every refusal is named, rollback must target the previous release, and a job that
/// names a process is stamped with the release it was admitted under.
/// </summary>
[global::Xunit.Trait("Tier", "Fast")]
public sealed class GpProcessReleaseBindingTests
{
    private const string Digest64 = "0000000000000000000000000000000000000000000000000000000000000000";
    private static readonly GpProcessReleaseEnvironment AwsEnvironment = new()
    {
        ServerContractVersion = 1,
        SupportedTargetKinds = ["aws-batch"],
        CustomCodeMayUseKubernetes = false,
        ReadyTargets = [new GpProcessBindingTarget
        {
            Kind = "aws-batch",
            Image = "ghcr.io/honua-io/honua-gp-worker@sha256:" + new string('b', 64),
            JobDefinitionArn = "arn:aws:batch:us-west-2:123456789012:job-definition/buffer:7",
        }],
    };

    [Fact]
    public async Task Activate_FirstDeploy_ThenSecond_ThenRollback_WalksTheBindingVersions()
    {
        var store = new InMemoryGpProcessReleaseStore();
        var v1 = await store.PublishAsync(Lock("1.0.0", "a"), "release-bot");
        var v2 = await store.PublishAsync(Lock("2.0.0", "b"), "release-bot");

        var first = await store.ActivateAsync(Activation(v1.Digest, expected: 0), AwsEnvironment);
        first.Activated.Should().BeTrue(string.Join(";", first.Details));
        first.Binding!.BindingVersion.Should().Be(1);
        first.Binding.PreviousLockDigest.Should().BeNull();
        first.Binding.Targets.Should().ContainSingle().Which.JobDefinitionArn.Should().Be("arn:aws:batch:us-west-2:123456789012:job-definition/buffer:7");

        var second = await store.ActivateAsync(Activation(v2.Digest, expected: 1), AwsEnvironment);
        second.Activated.Should().BeTrue();
        second.Binding!.BindingVersion.Should().Be(2);
        second.Binding.PreviousLockDigest.Should().Be(v1.Digest);

        var stale = await store.ActivateAsync(Activation(v2.Digest, expected: 1), AwsEnvironment);
        stale.Activated.Should().BeFalse();
        stale.Reasons.Should().Contain(GpProcessBindingRefusal.StaleBindingVersion);
        stale.Binding!.BindingVersion.Should().Be(2, "a refused activation reports the live binding");

        var wrongRollback = await store.ActivateAsync(Activation(v2.Digest, expected: 2, operation: "rollback"), AwsEnvironment);
        wrongRollback.Reasons.Should().Contain(GpProcessBindingRefusal.RollbackTargetMismatch);

        var rollback = await store.ActivateAsync(Activation(v1.Digest, expected: 2, operation: "rollback"), AwsEnvironment);
        rollback.Activated.Should().BeTrue(string.Join(";", rollback.Details));
        rollback.Binding!.BindingVersion.Should().Be(3);
        rollback.Binding.LockDigest.Should().Be(v1.Digest);
        rollback.Binding.PreviousLockDigest.Should().Be(v2.Digest);
        rollback.Binding.Operation.Should().Be("rollback");
    }

    [Fact]
    public async Task Activate_ConcurrentAttemptsWithTheSameExpectedVersion_AdmitExactlyOne()
    {
        var store = new InMemoryGpProcessReleaseStore();
        var release = await store.PublishAsync(Lock("1.0.0", "a"), "release-bot");

        var attempts = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => store.ActivateAsync(Activation(release.Digest, expected: 0), AwsEnvironment)))
            .ToArray();
        var decisions = await Task.WhenAll(attempts);

        decisions.Count(d => d.Activated).Should().Be(1);
        decisions.Where(d => !d.Activated).Should().OnlyContain(d => d.Reasons.Contains(GpProcessBindingRefusal.StaleBindingVersion));
        (await store.GetBindingAsync("buffer-features"))!.BindingVersion.Should().Be(1);
    }

    [Fact]
    public void Evaluate_NamesEveryRefusal()
    {
        var release = new GpProcessReleaseRecord
        {
            Lock = Lock("1.0.0", "a") with
            {
                Compatibility = new GpReleaseCompatibility { ServerContractVersion = new GpReleaseContractWindow { Minimum = 3, Maximum = 4 } },
                Targets =
                [
                    new GpReleaseTarget { Kind = "kubernetes-job", WorkerImageDigest = "sha256:" + Digest64 },
                    new GpReleaseTarget { Kind = "aws-batch", WorkerImageDigest = "sha256:" + Digest64 },
                ],
            },
            Digest = "sha256:" + Digest64,
            PublishedBy = "x",
            PublishedAt = DateTimeOffset.UnixEpoch,
        };
        var activation = new GpProcessBindingActivation
        {
            Process = "other-process",
            LockDigest = release.Digest,
            ExpectedBindingVersion = 4,
            Operation = "rollback",
            Actor = "deployer",
            Targets = [],
        };

        var environment = AwsEnvironment with { SupportedTargetKinds = ["aws-batch", "kubernetes-job"] };
        var decision = GpProcessReleaseActivationPolicy.Evaluate(activation, release, current: null, environment, DateTimeOffset.UnixEpoch);

        decision.Activated.Should().BeFalse();
        decision.Reasons.Should().Contain(GpProcessBindingRefusal.StaleBindingVersion)
            .And.Contain(GpProcessBindingRefusal.ProcessMismatch)
            .And.Contain(GpProcessBindingRefusal.IncompatibleServerContract)
            .And.Contain(GpProcessBindingRefusal.TargetUnsupported, "a kubernetes-job target is refused for custom code under ADR-0063")
            .And.Contain(GpProcessBindingRefusal.TargetIdentityMissing)
            .And.Contain(GpProcessBindingRefusal.RollbackTargetMissing);
        decision.Details.Should().Contain(d => d.Contains("ADR-0063", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_UnknownLock_IsLockNotFound()
    {
        var decision = GpProcessReleaseActivationPolicy.Evaluate(
            Activation("sha256:" + Digest64, expected: 0), release: null, current: null, AwsEnvironment, DateTimeOffset.UnixEpoch);

        decision.Activated.Should().BeFalse();
        decision.Reasons.Should().Equal(GpProcessBindingRefusal.LockNotFound);
    }

    [Fact]
    public void ImageForTarget_PinsTheRuntimeRepositoryAtTheTargetDigest()
    {
        var image = GpProcessReleaseActivationPolicy.ImageForTarget(Lock("1.0.0", "a"), "sha256:" + new string('c', 64));

        image.Should().Be("ghcr.io/honua-io/honua-gp-worker@sha256:" + new string('c', 64));
    }

    [Theory]
    [InlineData("arn:aws:batch:us-west-2:123456789012:job-definition/buffer")]
    [InlineData("arn:aws:batch:us-west-2:123456789012:job-definition/buffer:0")]
    [InlineData("arn:aws:batch:us-west-2:123456789012:job-definition/other:7")]
    [InlineData("arn:aws:batch:us-west-2:123456789012:job-definition/buffer:latest")]
    public async Task Activate_RejectsUnversionedOrWrongDefinition(string arn)
    {
        var store = new InMemoryGpProcessReleaseStore();
        var release = await store.PublishAsync(Lock("1.0.0", "a"), "operator");
        var activation = Activation(release.Digest, 0) with
        {
            Targets = [new GpProcessBindingTargetIdentity { Kind = "aws-batch", JobDefinitionArn = arn }],
        };

        var decision = await store.ActivateAsync(activation, AwsEnvironment);

        decision.Activated.Should().BeFalse();
        decision.Reasons.Should().Contain(GpProcessBindingRefusal.TargetIdentityMissing);
        (await store.GetBindingAsync("buffer-features")).Should().BeNull();
    }

    [Fact]
    public async Task Publish_ProtectsStoredReleaseFromCallerCollectionMutations()
    {
        var store = new InMemoryGpProcessReleaseStore();
        var original = Lock("1.0.0", "a");
        var mutableTargets = original.Targets.ToList();
        var release = await store.PublishAsync(original with { Targets = mutableTargets }, "operator");
        mutableTargets.Clear();

        var read = await store.GetReleaseAsync(release.Digest);

        read!.Lock.Targets.Should().ContainSingle();
        read.Lock.Digest.Should().Be(release.Digest);
    }

    [Fact]
    public async Task Activate_WorkerIdentityNotRegistered_LeavesBindingUnchanged()
    {
        var store = new InMemoryGpProcessReleaseStore();
        var release = await store.PublishAsync(Lock("1.0.0", "a"), "operator");

        var decision = await store.ActivateAsync(Activation(release.Digest, 0), AwsEnvironment with { ReadyTargets = [] });

        decision.Activated.Should().BeFalse();
        decision.Reasons.Should().Contain(GpProcessBindingRefusal.TargetNotReady);
        (await store.GetBindingAsync("buffer-features")).Should().BeNull();
    }

    [Fact]
    public async Task Admission_StampsTheActiveReleaseIntoTheSpec()
    {
        var store = new InMemoryGpProcessReleaseStore();
        var release = await store.PublishAsync(Lock("1.4.0", "a"), "release-bot");
        (await store.ActivateAsync(Activation(release.Digest, expected: 0), AwsEnvironment)).Activated.Should().BeTrue();
        var admission = new GpProcessReleaseAdmission(store, NullLogger<GpProcessReleaseAdmission>.Instance);
        var spec = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GpProcessReleaseAdmission.ProcessParam] = "buffer-features",
            [CustomCodeJobContract.ParamsJsonParam] = "{}",
        };

        var binding = await admission.ApplyAsync(spec, CancellationToken.None);

        binding!.BindingVersion.Should().Be(1);
        spec[CustomCodeJobContract.RuntimeParam].Should().Be("python");
        spec[CustomCodeJobContract.RepoUrlParam].Should().Be("https://github.com/honua-io/honua-samples");
        spec[CustomCodeJobContract.GitRefParam].Should().Be(new string('a', 40));
        spec[CustomCodeJobContract.EntrypointParam].Should().Be("buffer_features:execute");
        spec[CustomCodeJobContract.DepsManifestParam].Should().Be("samples/gp-local-dev/uv.lock");
        spec[GpProcessReleaseAdmission.ReleaseLockDigestParam].Should().Be(release.Digest);
        spec[GpProcessReleaseAdmission.ReleaseVersionParam].Should().Be("1.4.0");
        spec[GpProcessReleaseAdmission.ReleaseBindingVersionParam].Should().Be("1");
        spec[GpProcessReleaseAdmission.AwsBatchJobDefinitionParam].Should().Be("arn:aws:batch:us-west-2:123456789012:job-definition/buffer:7");
        spec.Should().NotContainKey(GpProcessReleaseAdmission.KubernetesImageParam, "the binding has no kubernetes-job target");
    }

    [Fact]
    public async Task Admission_RefusesAnUnboundProcessAndContradictingValues()
    {
        var store = new InMemoryGpProcessReleaseStore();
        var admission = new GpProcessReleaseAdmission(store, NullLogger<GpProcessReleaseAdmission>.Instance);

        var unbound = () => admission.ApplyAsync(new Dictionary<string, string> { [GpProcessReleaseAdmission.ProcessParam] = "buffer-features" }, CancellationToken.None);
        await unbound.Should().ThrowAsync<GeoprocessingValidationException>().WithMessage("*no active release binding*");

        var release = await store.PublishAsync(Lock("1.0.0", "a"), "release-bot");
        (await store.ActivateAsync(Activation(release.Digest, expected: 0), AwsEnvironment)).Activated.Should().BeTrue();
        var contradicting = () => admission.ApplyAsync(new Dictionary<string, string>
        {
            [GpProcessReleaseAdmission.ProcessParam] = "buffer-features",
            [CustomCodeJobContract.GitRefParam] = new string('f', 40),
        }, CancellationToken.None);
        await contradicting.Should().ThrowAsync<GeoprocessingValidationException>().WithMessage("*'customcode.git_ref' contradicts the active process release*");

        var untouched = new Dictionary<string, string> { ["process.id"] = "geometry.buffer" };
        (await admission.ApplyAsync(untouched, CancellationToken.None)).Should().BeNull();
        untouched.Should().HaveCount(1);
    }

    [Fact]
    public async Task Admission_ALaterBindingChange_DoesNotTouchAnAdmittedSpec()
    {
        var store = new InMemoryGpProcessReleaseStore();
        var v1 = await store.PublishAsync(Lock("1.0.0", "a"), "release-bot");
        var v2 = await store.PublishAsync(Lock("2.0.0", "b"), "release-bot");
        (await store.ActivateAsync(Activation(v1.Digest, expected: 0), AwsEnvironment)).Activated.Should().BeTrue();
        var admission = new GpProcessReleaseAdmission(store, NullLogger<GpProcessReleaseAdmission>.Instance);
        var queuedSpec = new Dictionary<string, string> { [GpProcessReleaseAdmission.ProcessParam] = "buffer-features" };
        await admission.ApplyAsync(queuedSpec, CancellationToken.None);

        (await store.ActivateAsync(Activation(v2.Digest, expected: 1), AwsEnvironment)).Activated.Should().BeTrue();
        var laterSpec = new Dictionary<string, string> { [GpProcessReleaseAdmission.ProcessParam] = "buffer-features" };
        await admission.ApplyAsync(laterSpec, CancellationToken.None);

        queuedSpec[GpProcessReleaseAdmission.ReleaseLockDigestParam].Should().Be(v1.Digest, "the queued job keeps the release it was admitted with");
        queuedSpec[CustomCodeJobContract.GitRefParam].Should().Be(new string('a', 40));
        laterSpec[GpProcessReleaseAdmission.ReleaseLockDigestParam].Should().Be(v2.Digest, "a pin change affects subsequent admissions only");
        laterSpec[CustomCodeJobContract.GitRefParam].Should().Be(new string('b', 40));
    }

    private static GpProcessBindingActivation Activation(string digest, int expected, string operation = "deploy")
        => new()
        {
            Process = "buffer-features",
            LockDigest = digest,
            ExpectedBindingVersion = expected,
            Operation = operation,
            Actor = "deployer",
            Targets = [new GpProcessBindingTargetIdentity { Kind = "aws-batch", JobDefinitionArn = "arn:aws:batch:us-west-2:123456789012:job-definition/buffer:7" }],
        };

    private static GpProcessReleaseLock Lock(string version, string shaChar)
        => new()
        {
            Process = new GpReleaseProcess
            {
                Name = "buffer-features",
                Version = version,
                Runtime = "python",
                Entrypoint = "buffer_features:execute",
                Contract = new GpReleaseContract { InputSchemaSha256 = "sha256:" + new string('1', 64), OutputSchemaSha256 = "sha256:" + new string('2', 64) },
            },
            Source = new GpReleaseSource { Repository = "honua-io/honua-samples", Sha = new string(shaChar[0], 40), DescriptorPath = "samples/gp-local-dev/honua-gp.yaml" },
            DependencyLock = new GpReleaseFileDigest { Path = "samples/gp-local-dev/uv.lock", Sha256 = "sha256:" + new string('d', 64) },
            RuntimeImage = new GpReleaseRuntimeImage { Reference = "ghcr.io/honua-io/honua-gp-worker@sha256:" + new string('b', 64), Digest = "sha256:" + new string('b', 64) },
            WorkflowVersion = "wf-2026.1-7",
            ConfigurationReferences = [],
            Targets = [new GpReleaseTarget { Kind = "aws-batch", WorkerImageDigest = "sha256:" + new string('b', 64), JobDefinitionName = "buffer" }],
            Compatibility = new GpReleaseCompatibility { ServerContractVersion = new GpReleaseContractWindow { Minimum = 1, Maximum = 2 } },
            Artifacts = [new GpReleaseArtifact { Name = $"buffer-features-{version}.tar.gz", Sha256 = "sha256:" + new string('e', 64), SizeBytes = 1 }],
            ValidationReceipts = [new GpReleaseValidationReceipt { Name = "golden-tests.junit.xml", Sha256 = "sha256:" + new string('9', 64), Status = "pass" }],
            GeneratedAt = "2026-10-15T04:00:00Z",
        };
}
