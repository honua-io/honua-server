// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Federation.Abstractions;
using Honua.Core.Features.Federation.Services;
using Honua.Core.Features.Infrastructure.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Federation;

public static class ServiceCollectionExtensions
{
    // The executor is a singleton because it keeps per-source circuit-breaker state; the planner is stateless.
    public static IServiceCollection AddFederationCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IFederationQueryPlanner, FederationQueryPlanner>();
        services.TryAddSingleton<FederationMetrics>();
        services.TryAddSingleton<IFederatedQueryExecutor>(static sp => new FederatedQueryExecutor(
            sp.GetRequiredService<IFederationQueryPlanner>(),
            sp.GetServices<IFederatedSourceConnector>(),
            sp.GetService<ResiliencePolicyOptions>() ?? ResiliencePolicyOptions.Default,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FederatedQueryExecutor>>(),
            sp.GetRequiredService<FederationMetrics>()));
        return services;
    }
}
