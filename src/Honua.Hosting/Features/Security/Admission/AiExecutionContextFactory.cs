// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Security.Admission;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Security.Admission;

/// <summary>Creates invocation identity only from validated principals or durable server-captured job ownership.</summary>
internal sealed class AiExecutionContextFactory(IOptions<AiExecutionAdmissionOptions> options)
{
    private readonly string _deploymentId = options.Value.DeploymentId;

    public AiExecutionContext? ForPrincipal(ClaimsPrincipal principal, string useCase, string? verifiedWorkflow = null)
        => Create(JobOwnershipSecurity.ResolveOwner(principal), useCase, verifiedWorkflow);

    public AiExecutionContext? ForJob(ExecutionJobRecord job, string executingOperationId, string? verifiedRun = null)
    {
        var owner = job.Audit.SubmitterSecurityContext?.OwnerActorId;
        if (!string.Equals(job.OperationId, executingOperationId, StringComparison.Ordinal)
            || !JobOwnershipSecurity.MatchesSubmitter(job.Audit, owner, job.Audit.SubmitterSecurityContext?.TenantId))
            return null;
        return Create(owner, "imagery-inference", verifiedRun ?? "job:" + job.OperationId);
    }

    private AiExecutionContext? Create(string? owner, string useCase, string? verifiedWorkflow)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return null;
        try
        {
            return AiExecutionContext.Create(_deploymentId, owner, useCase, verifiedWorkflow);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
