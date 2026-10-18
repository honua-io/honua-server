// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Singleton Core port for background services. Each call owns its complete scoped
/// authority graph; returned transient principals open independent scopes on later use.
/// This adapter is not registered before integrated lease qualification.
/// </summary>
internal sealed class JobExecutionLeaseAuthorityFacade(IServiceScopeFactory scopes) : IJobExecutionLeaseAuthority
{
    public async Task<JobExecutionLease> IssueAsync(JobExecutionLeaseIssuanceRequest request, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobExecutionLeaseAuthority>()
            .IssueAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobSecurityContext> RevalidateForExecutionAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobExecutionLeaseAuthority>()
            .RevalidateForExecutionAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task RequireCurrentLineageAsync(ExecutionJobRecord job, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<JobExecutionLeaseAuthority>()
            .RequireCurrentLineageAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobExecutionLeaseObservation> ResolveForObservationAsync(string jobId, ClaimsPrincipal observer,
        JobExecutionLeaseObservationKind kind, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobExecutionLeaseAuthority>()
            .ResolveForObservationAsync(jobId, observer, kind, cancellationToken).ConfigureAwait(false);
    }
}
