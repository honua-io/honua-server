// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Registers the Honua Operations Toolset: the grounding catalog (descriptor providers +
/// aggregator), the executors, the policy decision point seam, and the dispatcher.
/// </summary>
internal static class OperationsServiceCollectionExtensions
{
    public static IServiceCollection AddOperationsToolset(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

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

        // Policy seam. Bind the configurable guardrail policy from "Operations:Policy".
        // When it is enabled, the configurable decision point enforces the rule set; otherwise
        // the no-op pass-through default (Community tier) stays in place. TryAdd is used so an
        // explicitly-registered stricter PDP (Pro/Enterprise) registered earlier still wins.
        services
            .AddOptions<OperationPolicyOptions>()
            .Bind(configuration.GetSection(OperationPolicyOptions.SectionName));

        var policyOptions = configuration.GetSection(OperationPolicyOptions.SectionName).Get<OperationPolicyOptions>();
        if (policyOptions?.Enabled == true)
        {
            services.TryAddSingleton<IOperationPolicyDecisionPoint, ConfigurableOperationPolicyDecisionPoint>();
        }
        else
        {
            services.TryAddSingleton<IOperationPolicyDecisionPoint, AllowAllPolicyDecisionPoint>();
        }

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
