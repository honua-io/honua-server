// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Honua.ControlPlane;

// A host without a system NpgsqlDataSource gets no job store, and its job capabilities report the runner as unavailable.
internal static class PostgresJobSubstrateServiceCollectionExtensions
{
    internal const string SystemConnectionStringName = "DefaultConnection";

    private const string SystemSchemaKey = "Database:Schema";

    public static bool HasSystemDataSource(this IServiceCollection services)
        => services.Any(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource));

    public static bool HasExecutionJobStore(this IServiceCollection services)
        => services.Any(descriptor => descriptor.ServiceType == typeof(IExecutionJobStore));

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

    public static bool TryAddPostgresJobQueueAndLogStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(JobStoreDatabase)))
        {
            return false;
        }

        services.TryAddSingleton<PostgresJobQueue>();
        services.TryAddSingleton<IJobQueue>(provider => provider.GetRequiredService<PostgresJobQueue>());
        services.TryAddSingleton<IQueueClaimReconciler>(provider => provider.GetRequiredService<PostgresJobQueue>());
        services.TryAddSingleton<IExecutionLogStore, PostgresExecutionLogStore>();
        return true;
    }

    // A job host that serves no requests holds a handful of connections: claim, heartbeat, log and reconciliation statements, one job at a time.
    public static IServiceCollection AddJobHostSystemDataSource(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(SystemConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"A job worker requires ConnectionStrings:{SystemConnectionStringName}: the system PostgreSQL database "
                + "the serving host uses, which holds the job queue.");
        }

        var maxPoolSize = int.TryParse(
            configuration[$"{JobStoreOptions.SectionName}:{nameof(JobStoreOptions.WorkerMaxPoolSize)}"],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var configuredMaxPoolSize) && configuredMaxPoolSize > 0
            ? configuredMaxPoolSize
            : new JobStoreOptions().WorkerMaxPoolSize;

        services.TryAddSingleton(_ =>
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                MaxPoolSize = maxPoolSize,
                MinPoolSize = 0,
            };
            return NpgsqlDataSource.Create(builder.ConnectionString);
        });
        return services;
    }

    private static TimeSpan ReadRetention(IConfiguration configuration, string settingName, TimeSpan defaultValue)
        => TimeSpan.TryParse(
            configuration[$"{JobStoreOptions.SectionName}:{settingName}"], CultureInfo.InvariantCulture, out var configured)
            && configured > TimeSpan.Zero
            ? configured
            : defaultValue;
}
