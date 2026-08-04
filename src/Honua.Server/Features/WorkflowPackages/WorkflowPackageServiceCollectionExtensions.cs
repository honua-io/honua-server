// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Orchestration.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.WorkflowPackages;

internal static class WorkflowPackageServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowPackages(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<InMemoryWorkflowPackageStore>();
        services.TryAddSingleton<IWorkflowPackageStore>(sp =>
            sp.GetRequiredService<InMemoryWorkflowPackageStore>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowNodeProvider, ProcessCatalogWorkflowNodeProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IWorkflowNodeProvider, AuthoringWorkflowNodeProvider>());
        services.TryAddSingleton<IWorkflowNodeRegistry, WorkflowNodeRegistry>();

        // The author-security-context capture (RBAC options + tenant context + managed-membership
        // source) is composed behind one collaborator so the publish service stays within its
        // dependency-fan-out budget. Wired here to mirror the previous inline capture exactly: the
        // membership source stays unbound in this path (as it was before), so behavior is unchanged.
        services.TryAddScoped(sp => new WorkflowAuthorSecurityContextCapturer(
            sp.GetService<IOptions<RbacOptions>>(),
            sp.GetService<ITenantContext>()));
        services.TryAddScoped(sp => new WorkflowPackageService(
            sp.GetRequiredService<IWorkflowPackageStore>(),
            sp.GetRequiredService<IWorkflowNodeRegistry>(),
            sp.GetRequiredService<IGeoprocessingJobService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<WorkflowPackageService>>(),
            sp.GetService<IWorkflowDefinitionStore>(),
            sp.GetService<WorkflowOrchestrationEngine>(),
            sp.GetService<IMetadataReleaseService>(),
            sp.GetService<WorkflowAuthorSecurityContextCapturer>()));

        return services;
    }
}
