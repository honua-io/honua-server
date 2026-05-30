// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Spec;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Ai.Grounding.Spec;

internal static class SpecGroundingServiceCollectionExtensions
{
    public static IServiceCollection AddSpecGrounding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSpecGrammar();
        services.TryAddSingleton<SpecMutationApplier>();
        services.TryAddSingleton<SpecSummarizer>();
        services.TryAddSingleton<SpecGroundingService>();

        return services;
    }
}
