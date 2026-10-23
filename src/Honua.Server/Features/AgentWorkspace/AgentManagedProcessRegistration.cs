// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.AgentWorkspace;

internal static class AgentManagedProcessRegistration
{
    internal static void AddAgentManagedProcessAdmission(this IServiceCollection services)
    {
        services.TryAddScoped<AgentManagedProcessAdmission>();
        services.TryAddScoped<IAgentManagedProcessAdmission>(provider => provider.GetRequiredService<AgentManagedProcessAdmission>());
        // Ordinary workspace/Studio hosts need not compose an execution store.
        // Missing infrastructure remains fail-closed at actual job admission.
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IExecutionJobStore))) return;
        services.AddScoped<IJobExecutionLeaseScopeProvider>(provider => provider.GetRequiredService<AgentManagedProcessAdmission>());
        services.AddScoped<IJobExecutionLeaseObservationScopeProvider>(provider => provider.GetRequiredService<AgentManagedProcessAdmission>());
        services.TryAddScoped<ExecutionJobLeasePolicy>();
        services.TryAddScoped<IJobExecutionLeasePolicy>(provider => provider.GetRequiredService<ExecutionJobLeasePolicy>());
        services.TryAddScoped<IJobExecutionLeaseObservationPolicy>(provider => provider.GetRequiredService<ExecutionJobLeasePolicy>());
        services.TryAddScoped<JobExecutionLeaseAuthority>();
        services.TryAddSingleton<IJobExecutionLeaseAuthority, JobExecutionLeaseAuthorityFacade>();
    }
}
