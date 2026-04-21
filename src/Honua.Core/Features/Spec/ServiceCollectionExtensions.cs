// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Service registration helpers for the declarative Honua spec language.
/// <see cref="AddSpecGrammar"/> wires the grammar, canonical AST, and
/// validator (<see cref="ISpecParser"/>, <see cref="ISpecCanonicalizer"/>,
/// <see cref="ISpecValidator"/>). <see cref="AddSpecCore"/> wires the plan
/// / apply engine: planner, apply engine, content-hash cache, and reserved
/// resource-state stores. Consumers may call one or both from their
/// composition roots.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the spec parser, canonicalizer, validator, and default S1
    /// operator catalog. Idempotent — safe to call from multiple composition
    /// roots.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSpecGrammar(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IOperatorCatalog, OperatorCatalog>();
        services.TryAddSingleton<ISpecParser, SpecParser>();
        services.TryAddSingleton<ISpecCanonicalizer, SpecCanonicalizer>();
        services.TryAddSingleton<ISpecValidator, SpecValidator>();
        return services;
    }

    /// <summary>
    /// Registers the spec planner, apply engine, content-hash cache, and
    /// reserved S1 resource-state stores. Does not register a
    /// <see cref="ISpecComputeExecutor"/>; callers must provide one before
    /// the engine is resolved.
    /// </summary>
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

        services.TryAddSingleton<ISpecCostEstimator>(sp =>
            new SpecCostEstimator(
                sp.GetRequiredService<IProcessCatalog>(),
                sp.GetRequiredService<IOptions<SpecCostEstimatorOptions>>().Value));

        services.TryAddSingleton<ISpecPlanner>(sp =>
            new SpecPlanner(
                sp.GetRequiredService<ISpecCostEstimator>(),
                sp.GetServices<ISpecResourceStateStore>()));

        services.TryAddSingleton<IContentHashArtifactCache>(sp =>
            new InMemoryContentHashArtifactCache(sp.GetService<TimeProvider>()));

        services.TryAddSingleton(_ => new SpecApplyTokenRegistry());

        services.TryAddSingleton<ISpecApplyEngine>(sp =>
            new SpecApplyOrchestrator(
                sp.GetRequiredService<ISpecPlanner>(),
                sp.GetRequiredService<ISpecComputeExecutor>(),
                sp.GetRequiredService<IContentHashArtifactCache>(),
                sp.GetRequiredService<SpecApplyTokenRegistry>(),
                sp.GetServices<ISpecResourceStateStore>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<ILogger<SpecApplyOrchestrator>>()));

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISpecResourceStateStore>(
            new ReservedSpecResourceStateStore(SpecResourceKind.Dataset)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISpecResourceStateStore>(
            new ReservedSpecResourceStateStore(SpecResourceKind.Service)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISpecResourceStateStore>(
            new ReservedSpecResourceStateStore(SpecResourceKind.App)));

        return services;
    }
}
