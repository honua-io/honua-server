// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Styling;

/// <summary>
/// Service registration helpers for style suggestion services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the style suggestion service from Core.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddStyleSuggestionCore(this IServiceCollection services)
    {
        services.TryAddScoped<IStyleSuggestionService, StyleSuggestionService>();
        return services;
    }
}
