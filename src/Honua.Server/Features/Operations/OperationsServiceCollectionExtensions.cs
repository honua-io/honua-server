// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Operations.Abstractions;
using Honua.Server.Features.Operations.ServicePublish;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Operations;

/// <summary>
/// Registers the Honua Operations Toolset: the operation catalog, the invoker, the
/// pass-through policy decision point (the guardrail seam), and the operations and
/// providers that surface them.
/// </summary>
internal static class OperationsServiceCollectionExtensions
{
    /// <summary>
    /// Adds the operation catalog, invoker, policy decision point, and the
    /// <c>service.publish</c> operation and its provider.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddHonuaOperations(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOperationCatalog, OperationCatalog>();
        services.TryAddScoped<IOperationInvoker, OperationInvoker>();

        // Guardrail seam: Community-tier pass-through decision point. Pro/Enterprise
        // enforcement replaces this registration in a later phase.
        services.TryAddSingleton<IOperationPolicyDecisionPoint, AllowAllPolicyDecisionPoint>();

        // The one operation proven end-to-end in this spike.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOperationProvider, ServicePublishOperationProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IHonuaOperation, ServicePublishOperation>());

        return services;
    }
}
