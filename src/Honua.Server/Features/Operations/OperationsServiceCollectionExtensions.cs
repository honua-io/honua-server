// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Registers the Honua Operations Toolset: the grounding catalog (descriptor providers +
/// aggregator), the executors, the policy decision point seam, and the dispatcher.
/// </summary>
internal static class OperationsServiceCollectionExtensions
{
    public static IServiceCollection AddOperationsToolset(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<OperationHandleStore>();

        // Grounding catalog: descriptor providers aggregated by the catalog.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IOperationDescriptorProvider, ServerOperationDescriptorProvider>());
        services.TryAddSingleton<IOperationCatalog>(sp =>
            new OperationCatalog(
                sp.GetServices<IOperationDescriptorProvider>(),
                sp.GetRequiredService<TimeProvider>()));

        // Executors: concrete work, registered as an enumerable for the dispatcher.
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IOperationExecutor, ServicePublishExecutor>());

        // Policy seam: no-op pass-through default (Community tier). Pro/Enterprise swap this.
        services.TryAddSingleton<IOperationPolicyDecisionPoint, AllowAllPolicyDecisionPoint>();

        // Dispatcher: resolves descriptor + executor, runs policy, executes on Allow.
        services.TryAddScoped<IOperationInvoker>(sp =>
            new OperationDispatcher(
                sp.GetRequiredService<IOperationCatalog>(),
                sp.GetServices<IOperationExecutor>(),
                sp.GetRequiredService<IOperationPolicyDecisionPoint>(),
                sp.GetRequiredService<TimeProvider>()));

        return services;
    }
}
