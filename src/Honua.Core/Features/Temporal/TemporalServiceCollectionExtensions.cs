// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Abstractions;
using Honua.Core.Features.Temporal.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Temporal;

/// <summary>
/// Service registration for the slice-1 temporal history feature (honua-server#1166).
/// </summary>
public static class TemporalServiceCollectionExtensions
{
    /// <summary>
    /// Registers the temporal history service. Scoped to match the lifetime of the change tracker
    /// and feature reader it composes.
    /// </summary>
    public static IServiceCollection AddTemporalHistory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<ITemporalHistoryService, TemporalHistoryService>();
        return services;
    }
}
