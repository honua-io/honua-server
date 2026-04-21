// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Canonical;
using Honua.Core.Features.Spec.Grammar;
using Honua.Core.Features.Spec.Operators;
using Honua.Core.Features.Spec.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Spec;

/// <summary>
/// Service registration helpers for the declarative Honua spec language
/// (grammar, canonical AST, validator). Consumers register once and resolve
/// <see cref="ISpecParser"/>, <see cref="ISpecCanonicalizer"/>, and
/// <see cref="ISpecValidator"/> from the container.
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
}
