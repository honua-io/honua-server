// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Health;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Startup;

internal enum StartupMigrationOwnership
{
    HostManagesMigrations,
    ExternalMigrations
}

internal enum StartupKeyRingMode
{
    Ephemeral,
    Durable
}

internal sealed class StartupPostMigrationOptions
{
    public required StartupMigrationOwnership MigrationOwnership { get; init; }
    public required StartupKeyRingMode KeyRingMode { get; init; }
    public required Assembly MigrationsAssembly { get; init; }
}

internal sealed class StartupDegradedArmRequest
{
    public required string Phase { get; init; }
    public required string FailureClass { get; init; }
    public required StartupMigrationOwnership MigrationOwnership { get; init; }
    public required Assembly MigrationsAssembly { get; init; }
    public string? ReasonCode { get; init; }
    public string? Reason { get; init; }
}

internal sealed class StartupDegradedFailure
{
    public required string Phase { get; init; }
    public required Exception Error { get; init; }
    public required StartupPostMigrationOptions Options { get; init; }
}

internal static class StartupPostMigrationChecks
{
    public static async Task<bool> RunAsync(WebApplication app, StartupPostMigrationOptions options)
    {
        if (app.Services.GetRequiredService<DegradedStartupContext>().IsDegraded)
        {
            return true;
        }

#if HONUA_POSTGIS_ONLY
        if (!await ValidatePostgisConnectionsAsync(app, options).ConfigureAwait(false))
        {
            return false;
        }

        if (app.Services.GetRequiredService<DegradedStartupContext>().IsDegraded)
        {
            return true;
        }
#endif

        if (options.KeyRingMode == StartupKeyRingMode.Durable)
        {
            return await ValidateDurableKeyRingAsync(app, options).ConfigureAwait(false);
        }

        return true;
    }

#if HONUA_POSTGIS_ONLY
    private static async Task<bool> ValidatePostgisConnectionsAsync(
        WebApplication app,
        StartupPostMigrationOptions options)
    {
        try
        {
            await using var startupScope = app.Services.CreateAsyncScope();
            await DataStoreBuildPolicy.ValidatePostgisConnectionsAsync(
                startupScope.ServiceProvider.GetRequiredService<Honua.Core.Features.Security.Abstractions.ISecureConnectionRegistry>(),
                startupScope.ServiceProvider.GetRequiredService<Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider>())
                .ConfigureAwait(false);
            return true;
        }
        catch (Honua.Core.Features.FeatureStore.Domain.DataStoreProviderUnavailableException ex)
        {
            await GatedBootShutdown.StopRefusedHostAsync(app, ex.Message).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not InvalidOperationException)
        {
            var failure = new StartupDegradedFailure
            {
                Phase = "PostGIS connection policy validation",
                Error = ex,
                Options = options
            };
            if (TryEnterDegradedStart(app, failure))
            {
                return true;
            }

            throw;
        }
    }
#endif

    private static async Task<bool> ValidateDurableKeyRingAsync(
        WebApplication app,
        StartupPostMigrationOptions options)
    {
        try
        {
            _ = app.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value;
            return true;
        }
        catch (InvalidOperationException ex) when (!GatedBootShutdown.IsInProcessTestServer(app))
        {
            await GatedBootShutdown.StopRefusedHostAsync(app, ex.Message).ConfigureAwait(false);
            return false;
        }
        catch (PostgresException ex) when (string.Equals(ex.SqlState, "42P01", StringComparison.Ordinal))
        {
            if (TryArmMissingKeyRingTable(app, options))
            {
                return true;
            }

            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not InvalidOperationException)
        {
            var failure = new StartupDegradedFailure { Phase = "key-ring validation", Error = ex, Options = options };
            if (TryEnterDegradedStart(app, failure))
            {
                return true;
            }

            throw;
        }
    }

    private static bool TryArmMissingKeyRingTable(WebApplication app, StartupPostMigrationOptions options)
        => TryArmDegradedStart(app, new StartupDegradedArmRequest
        {
            Phase = "key-ring validation",
            FailureClass = "missing-schema",
            MigrationOwnership = options.MigrationOwnership,
            MigrationsAssembly = options.MigrationsAssembly,
            ReasonCode = ReadinessReasonCodes.SchemaFloor,
            Reason = "key-ring validation: table 'data_protection_keys' is not yet present"
        });

    private static bool TryEnterDegradedStart(WebApplication app, StartupDegradedFailure failure)
    {
        if (!StartupDatabaseResilience.IsTransientConnectivityError(failure.Error))
        {
            return false;
        }

        return TryArmDegradedStart(app, new StartupDegradedArmRequest
        {
            Phase = failure.Phase,
            FailureClass = StartupDatabaseResilience.DescribeFailureClass(failure.Error),
            MigrationOwnership = failure.Options.MigrationOwnership,
            MigrationsAssembly = failure.Options.MigrationsAssembly
        });
    }

    private static bool TryArmDegradedStart(WebApplication app, StartupDegradedArmRequest request)
    {
        var resilienceOptions = app.Services.GetRequiredService<StartupResilienceOptions>();
        if (!resilienceOptions.DegradedStartEnabled)
        {
            return false;
        }

        var warning = request.ReasonCode == ReadinessReasonCodes.SchemaFloor
            ? StartupDatabasePreflight.BuildSchemaFloorWarning(request.FailureClass)
            : StartupDatabaseResilience.BuildDegradedStartWarning(request.Phase, request.FailureClass);
        Honua.Infrastructure.Logging.Log.DatabaseStartupDegraded(app.Logger, warning);

        var migrationsPending = request.MigrationOwnership == StartupMigrationOwnership.HostManagesMigrations;
        var degradedContext = app.Services.GetRequiredService<DegradedStartupContext>();
        degradedContext.MarkDegraded(
            migrationsPending,
            request.MigrationsAssembly,
            request.Reason ?? StartupDatabasePreflight.BuildReason(request.Phase, request.FailureClass),
            schemaGuardPending: !migrationsPending,
            reasonCode: request.ReasonCode);
        return true;
    }
}
