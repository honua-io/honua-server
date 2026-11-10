// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Server.Features.Infrastructure.Coordination;
using Honua.Server.Features.Infrastructure.Progress;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;
using Honua.Server.Features.Security.State;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Honua.Server.Startup;

// The stores that every node of a deployment shares through the system database. They resolve the
// data source when first used, so a host whose data source is registered later still composes them.
internal static class PostgresSharedStateRegistration
{
    private const string SystemSchemaKey = "Database:Schema";

    public static IServiceCollection AddPostgresSharedState(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ControlPlaneRetentionOptions>()
            .Bind(configuration.GetSection(ControlPlaneRetentionOptions.SectionName))
            .ValidateDataAnnotations();
        services.AddOptions<JobStoreOptions>();
        services.TryAddSingleton(provider => new JobStoreDatabase(
            provider.GetRequiredService<NpgsqlDataSource>(),
            configuration[SystemSchemaKey]));
        services.TryAddSingleton<PostgresExecutionJobStore>();
        services.TryAddSingleton<ILeaseStore, PostgresLeaseStore>();

        services.TryAddSingleton<PostgresOperationProposalStore>();
        services.TryAddSingleton<PostgresWorkflowOperationStore>();
        services.TryAddSingleton<PostgresOperationInstanceStore>();
        services.TryAddSingleton(provider => new PostgresOperationSecretStore(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<JobStoreDatabase>(),
            provider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()));
        services.TryAddSingleton<PostgresUniversalProgressStore>();
        services.TryAddSingleton<PostgresSecurityTokenStore>();
        services.RemoveAll<ISecurityTokenStore>();
        services.AddSingleton<ISecurityTokenStore>(static provider =>
            provider.GetService<NpgsqlDataSource>() is not null
                ? provider.GetRequiredService<PostgresSecurityTokenStore>()
                : new InMemorySecurityTokenStore());

        if (services.Any(descriptor => descriptor.ServiceType == typeof(IExpiredRowSweep)))
        {
            return services;
        }

        services.AddSingleton<IExpiredRowSweep>(provider => provider.GetRequiredService<PostgresOperationProposalStore>());
        services.AddSingleton<IExpiredRowSweep>(provider => provider.GetRequiredService<PostgresWorkflowOperationStore>());
        services.AddSingleton<IExpiredRowSweep>(provider => provider.GetRequiredService<PostgresOperationInstanceStore>());
        services.AddSingleton<IExpiredRowSweep>(provider => provider.GetRequiredService<PostgresOperationSecretStore>());
        services.AddSingleton<IExpiredRowSweep>(provider => provider.GetRequiredService<PostgresUniversalProgressStore>());
        services.AddSingleton<IExpiredRowSweep>(provider => provider.GetRequiredService<PostgresSecurityTokenStore>());
        return services;
    }
}

// A registry that keeps nothing in the database has nothing for the retention sweep to delete.
internal sealed class NoExpiredRows : IExpiredRowSweep
{
    public static readonly NoExpiredRows Instance = new();

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);
}
