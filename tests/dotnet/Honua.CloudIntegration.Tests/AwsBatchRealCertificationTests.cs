// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon;
using Amazon.Batch;
using Amazon.Batch.Model;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Honua.CloudIntegration.Tests;

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
