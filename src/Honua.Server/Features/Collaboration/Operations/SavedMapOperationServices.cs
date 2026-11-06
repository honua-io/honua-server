// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Collaboration.Operations;

internal static class SavedMapOperationServices
{
    public static IServiceCollection AddSavedMapOperationLog(this IServiceCollection services)
    {
        services.TryAddSingleton<ISavedMapOperationConflictPolicy, MvpSavedMapOperationConflictPolicy>();
        services.TryAddSingleton<ISavedMapOperationLogRepository>(static sp =>
            new InMemorySavedMapOperationLogRepository(
                sp.GetRequiredService<ISavedMapOperationConflictPolicy>(),
                sp.GetService<TimeProvider>()));
        // Single serialization point for "assign cursor, then publish" so the live fan-out order
        // always matches the op-log cursor order (honua-server#2999 review).
        services.TryAddSingleton<SavedMapOperationAppendCoordinator>();
        return services;
    }
}
