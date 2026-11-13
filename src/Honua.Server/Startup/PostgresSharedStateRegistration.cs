// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Server.Features.Infrastructure.Coordination;
using Honua.Server.Features.Infrastructure.Progress;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;
using Honua.Server.Features.Infrastructure.SecurityState;
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
        services.AddPostgresConfigurationStores();
        services.TryAddSingleton(provider => new JobStoreDatabase(
            provider.GetRequiredService<NpgsqlDataSource>(),
            configuration[SystemSchemaKey]));
        services.TryAddSingleton<PostgresExecutionJobStore>();
        services.TryAddSingleton<PostgresLeaseStore>();
        // A host with no system database runs one node and has no lease store (deliberately null),
        // so callers that ask with GetService fall back to in-process coordination.
        services.TryAddSingleton<ILeaseStore>(provider =>
            provider.GetService<NpgsqlDataSource>() is not null ? provider.GetRequiredService<PostgresLeaseStore>() : null!);

        services.TryAddSingleton(provider => new ControlPlaneStorage(
            provider.GetRequiredService<JobStoreDatabase>(),
            provider.GetRequiredService<ILeaseStore>()));
        services.TryAddSingleton<WorkflowOperationRetention>();
        services.TryAddSingleton<PostgresOperationProposalStore>();
        services.TryAddSingleton<PostgresWorkflowOperationStore>();
        services.TryAddSingleton<PostgresOperationInstanceStore>();
        services.TryAddSingleton(provider => new PostgresOperationSecretStore(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<JobStoreDatabase>(),
            provider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()));
        services.TryAddSingleton<PostgresUniversalProgressStore>();
        services.TryAddSingleton(provider => new PostgresTokenReplayStore(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<JobStoreDatabase>()));
        services.TryAddSingleton(provider => new PostgresSecurityTokenStore(
            provider.GetRequiredService<JobStoreDatabase>(),
            provider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>()));
        services.AddSharedOrLocal<ISecurityTokenStore, PostgresSecurityTokenStore>(static () => new InMemorySecurityTokenStore());
        services.AddSharedOrLocal<IIdempotencyReservationStore, PostgresIdempotencyReservationStore>(
            static () => new InMemoryIdempotencyReservationStore());
        services.AddSharedOrLocal<ITokenReplayStore, PostgresTokenReplayStore>(static () => new InMemoryTokenReplayStore());

        services.AddRetentionSweep<PostgresOperationProposalStore>();
        services.AddRetentionSweep<PostgresWorkflowOperationStore>();
        services.AddRetentionSweep<PostgresOperationInstanceStore>();
        services.AddRetentionSweep<PostgresOperationSecretStore>();
        services.AddRetentionSweep<PostgresUniversalProgressStore>();
        services.AddRetentionSweep<PostgresSecurityTokenStore>();
        services.AddRetentionSweep<PostgresTokenReplayStore>();
        services.AddRetentionSweep<PostgresIdempotencyReservationStore>();
        return services;
    }

    // The lease store needs the system database; a host without one has no shared leases.
    public static ILeaseStore? SharedLeaseStore(this IServiceProvider provider)
        => provider.GetService<NpgsqlDataSource>() is null ? null : provider.GetService<ILeaseStore>();

    // The shared store on a host with a system database, a process-local one otherwise.
    private static void AddSharedOrLocal<TService, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TShared>(
        this IServiceCollection services, Func<TService> local)
        where TService : class
        where TShared : class, TService
    {
        services.TryAddSingleton<TShared>();
        services.RemoveAll<TService>();
        services.AddSingleton<TService>(provider =>
            provider.GetService<NpgsqlDataSource>() is not null ? provider.GetRequiredService<TShared>() : local());
    }

    // Registered once per store however often the shared state is added.
    private static void AddRetentionSweep<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(
        this IServiceCollection services)
        where TStore : class, IExpiredRowSweep
        => services.TryAddEnumerable(ServiceDescriptor.Singleton<IExpiredRowSweep, TStore>(
            provider => provider.GetRequiredService<TStore>()));
}

// A registry that keeps nothing in the database has nothing for the retention sweep to delete.
internal sealed class NoExpiredRows : IExpiredRowSweep
{
    public static readonly NoExpiredRows Instance = new();

    public Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);
}
