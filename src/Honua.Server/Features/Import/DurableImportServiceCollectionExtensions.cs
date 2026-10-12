// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.Import;

/// <summary>Composition for durable background file imports (honua-server#5916).</summary>
internal static class DurableImportServiceCollectionExtensions
{
    /// <summary>
    /// Routes background file imports to the durable job runner when one is configured
    /// (honua-server#5916). The file-import executor is registered unconditionally so any worker
    /// (the server's <c>JobExecutionService</c> loop or a batch-launched execution worker) can run
    /// <see cref="Honua.Core.Features.ControlPlane.Domain.ExecutionJobKind.FileImport"/> jobs. The
    /// provider's <see cref="IImportJobService"/> (the in-process runner) is wrapped by
    /// <see cref="DurableImportJobService"/>, which applies the runner
    /// decision resolved once per process.
    /// </summary>
    internal static IServiceCollection AddDurableBackgroundImports(this IServiceCollection services)
    {
        if (services.Any(static d => d.ServiceType == typeof(BackgroundImportRunnerDecision)))
        {
            return services;
        }

        services.TryAddEnumerable(
            Microsoft.Extensions.DependencyInjection.ServiceDescriptor.Singleton<
                Honua.Core.Features.ControlPlane.Abstractions.IJobExecutor,
                Honua.Import.FileImport.Durable.FileImportJobExecutor>());

        // Wrap the provider's in-process runner (registered by AddPostgreSqlServices as a factory).
        var inner = services.LastOrDefault(static d => d.ServiceType == typeof(IImportJobService));
        var wrap = inner is not null && (inner.ImplementationFactory is not null || inner.ImplementationInstance is not null);

        // Decided when first resolved, not here: a composition (or test host) may register its
        // import job service after this point. Without a job service background imports are
        // refused and import.background says so; an unwrapped service stays in-process.
        services.AddSingleton(sp =>
        {
            if (sp.GetService<IServiceProviderIsService>() is { } isService && !isService.IsService(typeof(IImportJobService)))
            {
                return new BackgroundImportRunnerDecision(
                    BackgroundImportRunnerMode.Unavailable,
                    unavailableReason: BackgroundImportRunnerDecision.ImportServiceMissingReason);
            }

            return wrap
                ? BackgroundImportRunnerDecision.Resolve(sp)
                : new BackgroundImportRunnerDecision(BackgroundImportRunnerMode.InProcess);
        });
        services.TryAddSingleton<IBackgroundImportRunnerInfo>(
            static sp => sp.GetRequiredService<BackgroundImportRunnerDecision>());

        if (!wrap)
        {
            return services;
        }

        services.RemoveAll<IImportJobService>();
        services.AddSingleton<IImportJobService>(sp => new DurableImportJobService(
            (IImportJobService)(inner!.ImplementationInstance ?? inner.ImplementationFactory!(sp)),
            sp.GetRequiredService<BackgroundImportRunnerDecision>(),
            sp.GetRequiredService<IUniversalProgressStore>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetServices<Honua.Core.Features.ControlPlane.Abstractions.IBatchComputeBackend>(),
            sp.GetRequiredService<ILogger<DurableImportJobService>>(),
            sp.GetService<Honua.Core.Features.ControlPlane.Abstractions.IExecutionJobStore>(),
            sp.GetService<Honua.Core.Features.ControlPlane.Abstractions.IJobQueue>(),
            sp.GetService<IInstanceIdentity>()));
        return services;
    }
}
