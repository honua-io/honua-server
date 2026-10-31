// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane.Executors;
using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.ControlPlane;

/// <summary>Registers the rollback restore gate in front of the deploy executor.</summary>
internal static class PreMigrationRestoreRegistration
{
    /// <summary>
    /// Registers restore collaborators. Program registers the guarded deploy executor explicitly.
    /// Forward deploys still reach
    /// <see cref="DeployOperationExecutor"/>. Rollback is assessed by <see cref="PreMigrationRestoreCoordinator"/>.
    /// </summary>
    public static IServiceCollection AddPreMigrationRestore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RestoreCellOptions>()
            .Bind(configuration.GetSection(RestoreCellOptions.SectionName));
        services.AddSingleton<DeployOperationExecutor>();
        services.AddSingleton<ISchemaJournal, PostgresSchemaJournal>();
        services.AddSingleton<IRevisionSchemaCatalog, ConfiguredRevisionSchemaCatalog>();
        services.AddSingleton<IPreMigrationRestoreAudit, FilePreMigrationRestoreAudit>();
        services.AddSingleton<IComposeRollbackTransport, ComposeRollbackSocketClient>();
        services.AddSingleton<IServingCustomerProbe, HttpServingCustomerProbe>();
        services.AddSingleton<IRestoredSchemaProbe, JournalRestoredSchemaProbe>();
        services.AddSingleton<PreMigrationRestoreAssessmentService>();
        services.AddSingleton<PreMigrationRestoreExecutionService>();
        services.AddSingleton<PreMigrationRestoreCoordinator>();
#if !HONUA_EXCLUDE_AWS
        services.AddSingleton<IRdsSnapshotRestore, AwsRdsSnapshotRestoreAdapter>();
        services.AddSingleton<IConnectionSecretRetarget, AwsSecretHostRetarget>();
        services.AddSingleton<IRestoredConnectionOpener, AwsRestoredConnectionOpener>();
#else
        services.AddSingleton<IRdsSnapshotRestore, UnconfiguredRdsSnapshotRestore>();
        services.AddSingleton<IConnectionSecretRetarget, UnconfiguredConnectionSecretRetarget>();
        services.AddSingleton<IRestoredConnectionOpener, AppDatabaseConnectionOpener>();
#endif
        return services;
    }
}
