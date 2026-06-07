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

        // Slices 2-5: checkpoint resolution, masking policy, and governed-rollback runner. The
        // provider-backed ITemporalHistoryStore is registered by the active data provider (Postgres
        // registers the change-log reader; read-only providers register a no-op).
        // Universal fallback history store (no-op). The Postgres provider registers the change-log reader
        // after this call, which wins for resolution; read-only providers keep this no-op.
        services.TryAddScoped<ITemporalHistoryStore, NoOpTemporalHistoryStore>();
        services.TryAddScoped<ITemporalCheckpointResolver, TemporalCheckpointResolver>();
        services.TryAddSingleton<ITemporalMaskingPolicy, PassthroughTemporalMaskingPolicy>();
        services.TryAddSingleton<ITemporalCorrectiveJobSink, InProcessTemporalCorrectiveJobSink>();
        services.TryAddScoped<ITemporalRollbackRunner, TemporalRollbackRunner>();
        return services;
    }
}
