// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Security.Admission;
using Honua.Infrastructure.Security.Admission;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Security.Admission;

public sealed class AiExecutionContextFactoryTests
{
    private static AiExecutionContextFactory Factory => new(Options.Create(new AiExecutionAdmissionOptions { DeploymentId = "deployment" }));

    [UnitTest]
    public void ForPrincipal_NameOnlyOrForgedCanonicalClaim_DoesNotCreateIdentity()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "operator"), new Claim("honua:canonical_actor", "forged-actor")], "oidc"));
        Factory.ForPrincipal(principal, "studio-chat").Should().BeNull();
    }

    [UnitTest]
    public void ForPrincipal_EqualNamesDifferentValidatedIssuer_RemainDifferentActors()
    {
        var first = Factory.ForPrincipal(Principal("issuer-one", "subject", "token-one"), "studio-chat")!;
        var second = Factory.ForPrincipal(Principal("issuer-two", "subject", "token-one"), "studio-chat")!;
        first.ActorKey.Should().NotBe(second.ActorKey);
        Factory.ForPrincipal(Principal("issuer-one", "subject", "refreshed-token"), "studio-chat")!.ActorKey.Should().Be(first.ActorKey);
    }

    [UnitTest]
    public void ForPrincipal_MissingDeployment_DeniesModelIdentity()
    {
        new AiExecutionContextFactory(Options.Create(new AiExecutionAdmissionOptions()))
            .ForPrincipal(Principal("issuer", "subject", "token"), "studio-chat").Should().BeNull();
    }

    [UnitTest]
    public void ForJob_LegacyOwnerOrDifferentExecutingJob_DeniesIdentity()
    {
        const string owner = "ApiKey:api-key:11111111-1111-1111-1111-111111111111";
        var job = new ExecutionJobRecord
        {
            OperationId = "job-one", Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing, TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local", WorkloadName = "imagery"
            },
            Audit = new OperationAuditInfo
            {
                RequestedBy = owner,
                SubmitterSecurityContext = new JobSecurityContext(null, null, []) { OwnerActorId = owner }
            }
        };
        Factory.ForJob(job, "job-one")!.WorkflowKey.Should().Be(
            AiExecutionContext.Create("deployment", owner, "imagery-inference", "job:job-one").WorkflowKey);
        Factory.ForJob(job, "different-job").Should().BeNull();
        Factory.ForJob(job with { Audit = new OperationAuditInfo { RequestedBy = owner } }, "job-one").Should().BeNull();
        Factory.ForJob(job with { Audit = job.Audit with { RequestedBy = "different-actor" } }, "job-one").Should().BeNull();
    }

    private static ClaimsPrincipal Principal(string issuer, string subject, string token) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.Name, "same display name"), new Claim(ClaimTypes.NameIdentifier, subject),
        new Claim("iss", issuer), new Claim("jti", token)], "oidc"));
}
