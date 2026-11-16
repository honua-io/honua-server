// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Canonical;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Spec.Grammar;
using Honua.Core.Features.Spec.Operators;
using Honua.Core.Features.Spec.Services;
using Honua.Core.Features.Spec.Validation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Spec;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSpecGrammar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IOperatorCatalog, OperatorCatalog>();
        services.TryAddSingleton<ISpecParser, SpecParser>();
        services.TryAddSingleton<ISpecCanonicalizer, SpecCanonicalizer>();
        services.TryAddSingleton<ISpecValidator, SpecValidator>();
        return services;
    }

    public static IServiceCollection AddSpecCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);

        services
            .AddOptions<SpecCostEstimatorOptions>()
            .Bind(configuration.GetSection("Spec:CostEstimator"));

        services.TryAddSingleton<SpecCostEstimator>(sp =>
            new SpecCostEstimator(
                sp.GetRequiredService<IProcessCatalog>(),
                sp.GetRequiredService<IOptions<SpecCostEstimatorOptions>>().Value));

        services.TryAddSingleton<ISpecPlanner>(sp =>
            new SpecPlanner(sp.GetRequiredService<SpecCostEstimator>()));

        services.TryAddSingleton<IContentHashArtifactCache>(sp =>
            new InMemoryContentHashArtifactCache(sp.GetService<TimeProvider>()));

        services.TryAddSingleton(_ => new SpecApplyTokenRegistry());

        services.TryAddSingleton<ISpecApplyEngine>(sp =>
            new SpecApplyOrchestrator(
                sp.GetRequiredService<ISpecPlanner>(),
                sp.GetRequiredService<ISpecComputeExecutor>(),
                sp.GetRequiredService<IContentHashArtifactCache>(),
                sp.GetRequiredService<SpecApplyTokenRegistry>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<ILogger<SpecApplyOrchestrator>>()));

        services.TryAddSingleton(new ReservedSpecResourceStateStore(SpecResourceKind.Dataset));

        return services;
    }
}
