// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Ai.AnalysisGeneration;
using Honua.Core.Features.AnalysisContent.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Ai.AnalysisContent;

internal static class AnalysisContentServiceCollectionExtensions
{
    public static IServiceCollection AddAnalysisContent(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton(TimeProvider.System);

        // In-memory baseline store for hosts without a durable provider (e.g. DuckDB/MySQL
        // profiles or tests). It must be a process-wide singleton: the concrete store keeps all
        // content in instance fields, so a scoped registration would resolve a fresh empty store
        // per request and silently lose items written by an earlier request. The interface stays
        // scoped (resolving the singleton) to match the durable Postgres store's lifetime, so the
        // Postgres registration cleanly replaces it when configured. Data does not survive a
        // restart; durable persistence requires the Postgres-backed store.
        services.TryAddSingleton<InMemoryAnalysisContentStore>();
        services.TryAddScoped<IAnalysisContentStore>(sp =>
            sp.GetRequiredService<InMemoryAnalysisContentStore>());
        services.TryAddScoped<IAnalysisContentService, AnalysisContentService>();

        // NL -> analysis-package generation. Reuses the workflow-generation provider config + chat
        // plumbing (registered by AddWorkflowGeneration) and grounds in the geoprocessing process
        // catalog (IProcessCatalog, registered by AddGeoprocessing) as the analysis-method vocabulary,
        // gating with the DB-free structural ProcessPlanValidator so generation never requires live
        // layer metadata or the execution engine.
        services.TryAddSingleton<IAnalysisGenerationService, AnalysisGenerationService>();

        return services;
    }
}
