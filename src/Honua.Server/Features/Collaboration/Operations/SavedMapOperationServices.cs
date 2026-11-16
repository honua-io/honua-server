// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Collaboration.Operations;
using Honua.Server.Features.Collaboration.Sessions;
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
        services.TryAddSingleton(static sp => new SavedMapOperationAppendCoordinator(
            sp.GetRequiredService<ISavedMapOperationLogRepository>(),
            sp.GetRequiredService<InMemoryCollaborationSessionService>(),
            sp.GetRequiredService<SavedMapCollaborationTopology>(),
            sp.GetRequiredService<CollaborationSessionRealtimeServices>().Provider));
        return services;
    }
}
