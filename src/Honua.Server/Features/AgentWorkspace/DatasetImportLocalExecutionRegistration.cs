// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Geoprocessing;
using Honua.Server.Features.Import;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Honua.Core.Features.Import.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.AgentWorkspace;

internal static class DatasetImportLocalExecutionRegistration
{
    // Call before AddServerFeatures so its canonical orchestration and worker
    // registrations see the durable store. This explicit local profile does not
    // mint the paid cache/substrate entitlement or enable remote workloads.
    internal static void AddDatasetImportLocalJobSubstrate(this IServiceCollection services, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("AgentWorkspace:DatasetImport:LocalStorage:Enabled") ||
            !AgentWorkspaceState.IsIdentifier(configuration["AgentWorkspace:DeploymentId"]) ||
            BackgroundImportRunnerDecision.IsFrozenBetweenRequestsHost(configuration) ||
            !services.Any(descriptor => descriptor.ServiceType == typeof(IConnectionMultiplexer))) return;
        services.TryAddSingleton<IExecutionJobStore>(provider => new RedisExecutionJobStore(
            provider.GetRequiredService<IConnectionMultiplexer>(), provider.GetRequiredService<ILogger<RedisExecutionJobStore>>()));
        // The shared worker's GP terminal callback requires its canonical result
        // store even when the admitted job is a file import. Storage composition
        // does not grant permission to submit a different job kind.
        services.TryAddSingleton<IGeoprocessingResultPackageStore>(provider => new RedisGeoprocessingResultPackageStore(
            provider.GetRequiredService<IConnectionMultiplexer>(), provider.GetRequiredService<IOptionsMonitor<GeoprocessingExecutorOptions>>(),
            provider.GetRequiredService<ILogger<RedisGeoprocessingResultPackageStore>>()));
    }

    internal static void AddDatasetImportLocalExecution(this IServiceCollection services)
    {
        if (services.Any(descriptor => descriptor.ServiceType == typeof(LocalDatasetImportSubmissionReadiness))) return;
        services.AddScoped<LocalDatasetImportSubmissionReadiness>();
        services.TryAddScoped<IDatasetImportSubmissionReadiness>(provider => provider.GetRequiredService<LocalDatasetImportSubmissionReadiness>());
        services.TryAddScoped<DatasetImportSubmissionService>();
        services.TryAddScoped<IDatasetImportAdmissionProvider, DatasetImportAdmissionProvider>();
        services.TryAddScoped<IDatasetImportAuthorityDescriptorReader, DatasetImportAuthorityDescriptorReader>();
        services.TryAddScoped<FileImportExecutionLeaseScopeProvider>();
        services.AddScoped<IJobExecutionLeaseScopeProvider>(provider => provider.GetRequiredService<FileImportExecutionLeaseScopeProvider>());
        services.AddScoped<IJobExecutionLeaseObservationScopeProvider>(provider => provider.GetRequiredService<FileImportExecutionLeaseScopeProvider>());
        services.TryAddScoped<ExecutionJobLeasePolicy>();
        services.TryAddScoped<IJobExecutionLeasePolicy>(provider => provider.GetRequiredService<ExecutionJobLeasePolicy>());
        services.TryAddScoped<IJobExecutionLeaseObservationPolicy>(provider => provider.GetRequiredService<ExecutionJobLeasePolicy>());
        services.TryAddScoped<JobExecutionLeaseAuthority>();
        services.TryAddSingleton<IJobExecutionLeaseAuthority, JobExecutionLeaseAuthorityFacade>();
        services.TryAddScoped<IImportExecutionCommitAuthority, FileImportExecutionCommitAuthority>();
    }
}
