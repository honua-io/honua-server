// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Assets;

internal static class AssetsServiceCollectionExtensions
{
    public static IServiceCollection AddAssets(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<AssetFormLookup>();
        services.AddScoped<AssetProfileValidator>();
        services.AddScoped<AssetProfileService>();
        services.AddScoped<AssetCompletionCapabilities>();
        services.AddScoped<Tasks.WorkTaskAssetResolver>();
        services.AddScoped<Tasks.WorkTaskAssetReader>();
        services.AddScoped<Tasks.WorkTaskCompletionWriteback>();
        services.AddScoped<Tasks.WorkTaskCompletionForm>();
        services.AddScoped<Tasks.WorkTaskCompletionEdits>();
        services.AddScoped<Tasks.WorkTaskLocator>();
        services.AddScoped<Tasks.WorkTaskFormBinding>();
        services.AddScoped<Tasks.WorkTaskService>();
        services.AddScoped<Tasks.WorkTaskCompletionService>();
        services.AddScoped<Tasks.WorkTaskCompletionProcessor>();
        services.AddScoped<Tasks.WorkTaskCompletionExecution>();
        services.AddScoped<Tasks.WorkTaskAttachmentAccess>();
        services.AddScoped<Tasks.WorkTaskAttachmentPolicy>();
        services.AddScoped<Tasks.WorkTaskAttachmentContent>();
        services.AddScoped<Tasks.WorkTaskAttachmentService>();
        services.AddScoped<Tasks.WorkTaskAttachmentCleanup>();
        services.AddHostedService<Tasks.WorkTaskAttachmentCleanupWorker>();
        return services;
    }
}
