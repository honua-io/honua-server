// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Honua.ControlPlane;

/// <summary>
/// Registers the PostgreSQL job substrate (ADR-0081) over the host's system
/// <see cref="NpgsqlDataSource"/>. A host without that data source gets no job store, and its
/// job capabilities report the store as not configured.
/// </summary>
internal static class PostgresJobSubstrateServiceCollectionExtensions
{
    private const string SystemSchemaKey = "Database:Schema";

    public static bool HasSystemDataSource(this IServiceCollection services)
        => services.Any(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource));

    /// <summary>
    /// Registers the job store, with its clock and reconciliation candidates. Returns false, and
    /// registers nothing, when the host has no system data source.
    /// </summary>
    public static bool TryAddPostgresExecutionJobStore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!services.HasSystemDataSource())
        {
            return false;
        }

        services.AddOptions<JobStoreOptions>().Configure(options =>
        {
            options.JobRetention = ReadRetention(configuration, nameof(JobStoreOptions.JobRetention), options.JobRetention);
            options.LogRetention = ReadRetention(configuration, nameof(JobStoreOptions.LogRetention), options.LogRetention);
        });
        services.TryAddSingleton(provider => new JobStoreDatabase(
            provider.GetRequiredService<NpgsqlDataSource>(),
            configuration[SystemSchemaKey]));
        services.TryAddSingleton<PostgresExecutionJobStore>();
        services.TryAddSingleton<IExecutionJobStore>(provider => provider.GetRequiredService<PostgresExecutionJobStore>());
        services.TryAddSingleton<IJobStoreClock>(provider => provider.GetRequiredService<PostgresExecutionJobStore>());
        services.TryAddSingleton<IReconcilableJobSource>(provider => provider.GetRequiredService<PostgresExecutionJobStore>());
        return true;
    }

    private static TimeSpan ReadRetention(IConfiguration configuration, string settingName, TimeSpan defaultValue)
        => TimeSpan.TryParse(
            configuration[$"{JobStoreOptions.SectionName}:{settingName}"], CultureInfo.InvariantCulture, out var configured)
            && configured > TimeSpan.Zero
            ? configured
            : defaultValue;

    /// <summary>
    /// Registers the job queue and the job log store. Returns false, and registers nothing, when
    /// the job store is not registered.
    /// </summary>
    public static bool TryAddPostgresJobQueueAndLogStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(PostgresExecutionJobStore)))
        {
            return false;
        }

        services.TryAddSingleton<PostgresJobQueue>();
        services.TryAddSingleton<IJobQueue>(provider => provider.GetRequiredService<PostgresJobQueue>());
        services.TryAddSingleton<IQueueClaimReconciler>(provider => provider.GetRequiredService<PostgresJobQueue>());
        services.TryAddSingleton<IExecutionLogStore, PostgresExecutionLogStore>();
        return true;
    }
}
