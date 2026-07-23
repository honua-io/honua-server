// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon;
using Amazon.Batch;
using Amazon.Batch.Model;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Honua.CloudIntegration.Tests;

/// <summary>
/// Real-AWS certification lane (#2164): the first live-account smoke proving the AWS Batch
/// control-plane path Honua's <c>AwsBatchComputeBackend</c> depends on actually works against a
/// REAL account (not LocalStack), and that the OIDC/credential wiring in
/// <c>real-aws-certification.yml</c> is sound. It performs a register → describe → deregister
/// round-trip on a uniquely-named job definition.
///
/// SAFETY: this lane is OFF unless <see cref="RealAwsCertificationFixture.Enabled"/> (it
/// <c>[SkippableFact]</c>-skips otherwise, so it never costs money or fails on
/// forks/PRs without credentials). The job definition is registered ONLY (never submitted), so
/// there is NO compute, NO running job, and therefore ZERO cost. Every created resource carries
/// the per-run <c>honua-certrun-*</c> job-definition prefix — a namespace deliberately DISJOINT from
/// the standing GP pool (<c>honua-cert-cert-*</c>) so the honua-iac OIDC register/deregister grant
/// cannot touch a standing definition — no pre-existing resource is ever read-modified, and teardown
/// (deregister → status INACTIVE) is guaranteed in a <c>finally</c> block.
///
/// Remainder (tracked under #2164, requires a supervised, budgeted run): the full
/// submit-to-SUCCEEDED Batch lifecycle and the ECS/Lambda deploy + rollback certifications need an
/// ephemeral Fargate compute environment + IAM execution role whose teardown is slower and must be
/// supervised, so they are intentionally NOT performed automatically here.
/// </summary>
[Trait(CloudIntegrationTraits.Category, CloudIntegrationTraits.RealAwsCertification)]
public sealed class AwsBatchRealCertificationTests : IClassFixture<RealAwsCertificationFixture>
{
    private readonly RealAwsCertificationFixture _cert;
    private readonly ITestOutputHelper _output;

    public AwsBatchRealCertificationTests(RealAwsCertificationFixture cert, ITestOutputHelper output)
    {
        _cert = cert;
        _output = output;
    }

    [SkippableFact]
    public async Task RegisterThenDeregisterJobDefinition_RoundTripsAgainstLiveBatch()
    {
        Skip.IfNot(
            _cert.Enabled,
            "Real-AWS certification lane disabled (set HONUA_REALAWS_CERT_ENABLED=true with credentials present).");

        using var batch = new AmazonBatchClient(RegionEndpoint.GetBySystemName(_cert.Region));

        // Per-run job definitions live in a DISTINCT namespace (honua-certrun-<runid>-*) that is
        // disjoint from the standing GP pool (honua-cert-cert-*), so the tightened honua-iac OIDC
        // register/deregister grant (scoped to honua-certrun-*) can never touch a standing definition.
        var jobDefinitionName = $"{_cert.JobDefinitionRunPrefix}-jobdef";

        // Register an EC2-type container job definition. It is never submitted, so it incurs no
        // cost and needs no compute environment or execution role. The image string is not pulled
        // at registration time, so a public reference suffices.
        var register = new RegisterJobDefinitionRequest
        {
            JobDefinitionName = jobDefinitionName,
            Type = JobDefinitionType.Container,
            // Tag with the per-run tag + created stamp so the reaper (CertificationResourceReaper)
            // can reclaim this definition if a crash between register and deregister orphans it.
            Tags = new Dictionary<string, string>(_cert.RunTags(), StringComparer.Ordinal),
            ContainerProperties = new ContainerProperties
            {
                Image = "public.ecr.aws/docker/library/busybox:latest",
                Command = ["true"],
                ResourceRequirements =
                [
                    new ResourceRequirement { Type = ResourceType.VCPU, Value = "1" },
                    new ResourceRequirement { Type = ResourceType.MEMORY, Value = "512" }
                ]
            }
        };

        string? registeredArn = null;
        try
        {
            var registered = await batch.RegisterJobDefinitionAsync(register);
            registeredArn = registered.JobDefinitionArn;

            registeredArn.Should().NotBeNullOrWhiteSpace("registering against live AWS Batch must return an ARN");
            registered.JobDefinitionName.Should().Be(jobDefinitionName);
            registeredArn.Should().Contain(
                _cert.JobDefinitionRunPrefix,
                "the created resource must carry the unique honua-certrun-* prefix so it is isolated, "
                + "traceable, and disjoint from the standing GP job-definition pool");

            // The freshly registered definition must be discoverable as ACTIVE.
            var active = await DescribeActiveAsync(batch, jobDefinitionName);
            active.Should().ContainSingle("the just-registered job definition must be ACTIVE")
                .Which.JobDefinitionArn.Should().Be(registeredArn);

            // Tear down: deregister flips the revision to INACTIVE so no ACTIVE resource remains.
            await batch.DeregisterJobDefinitionAsync(new DeregisterJobDefinitionRequest
            {
                JobDefinition = registeredArn
            });
            registeredArn = null;

            var afterDeregister = await DescribeActiveAsync(batch, jobDefinitionName);
            afterDeregister.Should().BeEmpty(
                "after deregistration no ACTIVE job definition must remain — the lane leaves zero standing resources");
        }
        finally
        {
            // Guaranteed teardown if any assertion above threw after registration.
            if (registeredArn is not null)
            {
                try
                {
                    await batch.DeregisterJobDefinitionAsync(new DeregisterJobDefinitionRequest
                    {
                        JobDefinition = registeredArn
                    });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Best-effort: a deregister failure here would leave only an INACTIVE-able
                    // revision under the unique honua-certrun-* prefix, never live infrastructure.
                    // Surface it in CI output so a failed teardown is visible (the tag-based reaper
                    // is the backstop), rather than swallowing it silently.
                    _output.WriteLine(
                        $"[cert] best-effort deregister of '{registeredArn}' failed: {ex.Message}");
                }
            }
        }
    }

    private static async Task<IReadOnlyList<JobDefinition>> DescribeActiveAsync(
        AmazonBatchClient batch,
        string jobDefinitionName)
    {
        var response = await batch.DescribeJobDefinitionsAsync(new DescribeJobDefinitionsRequest
        {
            JobDefinitionName = jobDefinitionName,
            Status = "ACTIVE"
        });

        return response.JobDefinitions ?? [];
    }
}
