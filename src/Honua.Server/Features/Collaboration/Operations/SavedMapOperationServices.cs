// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Collaboration.Operations;

/// <summary>
/// Dependency injection for the saved-map collaborative edit operation log (#972). Registers the
/// MVP conflict policy and the in-memory durable-seam repository. A durable Postgres-backed
/// repository can override <see cref="ISavedMapOperationLogRepository"/> later without touching
/// the HTTP adapter.
/// </summary>
internal static class SavedMapOperationServices
{
    public static IServiceCollection AddSavedMapOperationLog(this IServiceCollection services)
    {
        services.TryAddSingleton<ISavedMapOperationConflictPolicy, MvpSavedMapOperationConflictPolicy>();
        services.TryAddSingleton<ISavedMapOperationLogRepository>(static sp =>
            new InMemorySavedMapOperationLogRepository(
                sp.GetRequiredService<ISavedMapOperationConflictPolicy>(),
                sp.GetService<TimeProvider>()));
        return services;
    }
}
