// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.HealthCheck;
using Honua.Server.Features.Infrastructure.Helpers;

namespace Honua.Server.Features.Infrastructure.Monitoring;

internal interface IDeployPreflightProbe
{
    Task<DeployPreflightSnapshot> ProbeAsync(CancellationToken cancellationToken = default);
}

internal sealed class DeployPreflightProbe(
    IConfiguration configuration,
    IReadinessCheckService readinessCheckService,
    IDatabaseMigrationRunner migrationRunner,
    MigrationState migrationState,
    DatabaseCompatibilityState databaseCompatibilityState,
    IConnectionSecretResolver? secretResolver = null) : IDeployPreflightProbe
{
    private const string MigrationPlanUnavailableMessage = "Migration planning is temporarily unavailable.";

    public async Task<DeployPreflightSnapshot> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var readiness = await readinessCheckService.CheckReadinessAsync(cancellationToken).ConfigureAwait(false);
        var migration = await BuildMigrationSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var compatibilitySnapshot = BuildDatabaseCompatibilitySnapshot();

        // null = check not applicable (no connection string); treat as compatible
        var databaseCompatible = compatibilitySnapshot is null || compatibilitySnapshot.IsCompatible;

        var readyForCoordinatedDeploy = readiness.IsReady &&
            migration.PlanAvailable &&
            !migration.UpgradeRequired &&
            databaseCompatible;

        return new DeployPreflightSnapshot
        {
            Status = readyForCoordinatedDeploy ? "ready" : "blocked",
            ReadyForCoordinatedDeploy = readyForCoordinatedDeploy,
            Message = BuildPreflightMessage(readiness, migration, compatibilitySnapshot),
            Readiness = new DeployPreflightReadinessSnapshot
            {
                IsReady = readiness.IsReady,
                StatusCode = readiness.StatusCode,
                Message = readiness.Message
            },
            Migration = migration,
            DatabaseCompatibility = compatibilitySnapshot
        };
    }

    private async Task<DeployPreflightMigrationSnapshot> BuildMigrationSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connectionString = await ConnectionStringResolutionHelper.ResolveDefaultConnectionStringAsync(
                configuration,
                secretResolver,
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return new DeployPreflightMigrationSnapshot
                {
                    LifecycleStatus = GetMigrationStatusLabel(migrationState.Status),
                    Message = GetMigrationStatusMessage(migrationState),
                    PlanAvailable = false,
                    UpgradeRequired = false,
                    PlanError = "No database connection string configured."
                };
            }

            DatabaseMigrationPlan plan = await migrationRunner.PlanMigrationsAsync(
                connectionString,
                typeof(Program).Assembly,
                cancellationToken).ConfigureAwait(false);

            return new DeployPreflightMigrationSnapshot
            {
                LifecycleStatus = GetMigrationStatusLabel(migrationState.Status),
                Message = GetMigrationStatusMessage(migrationState),
                PlanAvailable = plan.Successful,
                UpgradeRequired = plan.Successful && plan.UpgradeRequired,
                PendingScripts = plan.PendingScripts,
                ExecutedButNotDiscoveredScripts = plan.ExecutedButNotDiscoveredScripts,
                PlanError = plan.Successful ? null : MigrationPlanUnavailableMessage
            };
        }
        catch (Exception)
        {
            return new DeployPreflightMigrationSnapshot
            {
                LifecycleStatus = GetMigrationStatusLabel(migrationState.Status),
                Message = GetMigrationStatusMessage(migrationState),
                PlanAvailable = false,
                UpgradeRequired = false,
                PlanError = MigrationPlanUnavailableMessage
            };
        }
    }

    private static string BuildPreflightMessage(
        ReadinessResult readiness,
        DeployPreflightMigrationSnapshot migration,
        DeployPreflightDatabaseCompatibilitySnapshot? compatibility)
    {
        if (!readiness.IsReady)
        {
            return readiness.Message;
        }

        if (!migration.PlanAvailable)
        {
            return migration.PlanError ?? "Migration planning is unavailable for this instance.";
        }

        if (migration.UpgradeRequired)
        {
            return "Pending migrations must be reconciled before coordinated deployment.";
        }

        if (compatibility is not null && !compatibility.IsCompatible)
        {
            return compatibility.ErrorMessage ?? "Database compatibility check failed.";
        }

        return "Instance is ready for coordinated deployment.";
    }

    private static string GetMigrationStatusLabel(MigrationLifecycleStatus status) =>
        status switch
        {
            MigrationLifecycleStatus.Running => "running",
            MigrationLifecycleStatus.Succeeded => "succeeded",
            MigrationLifecycleStatus.Skipped => "skipped",
            MigrationLifecycleStatus.Failed => "failed",
            _ => "unknown"
        };

    private static string? GetMigrationStatusMessage(MigrationState migrationState)
    {
        if (!string.IsNullOrWhiteSpace(migrationState.StatusMessage))
        {
            return migrationState.StatusMessage;
        }

        return migrationState.Status switch
        {
            MigrationLifecycleStatus.Running => "Database migrations in progress.",
            MigrationLifecycleStatus.Failed => "Database migrations failed.",
            MigrationLifecycleStatus.Unknown => "Database migrations not completed.",
            _ => null
        };
    }

    private DeployPreflightDatabaseCompatibilitySnapshot? BuildDatabaseCompatibilitySnapshot()
    {
        var result = databaseCompatibilityState.Result;
        if (result is null)
        {
            return null;
        }

        return new DeployPreflightDatabaseCompatibilitySnapshot
        {
            IsCompatible = result.IsCompatible,
            EngineVersion = result.EngineVersion,
            PostGisVersion = result.PostGisVersion,
            PostGisRasterVersion = result.PostGisRasterVersion,
            InstalledExtensions = result.InstalledExtensions,
            Warnings = result.Warnings,
            ErrorMessage = result.ErrorMessage
        };
    }
}

internal sealed class DeployPreflightSnapshot
{
    public required string Status { get; init; }

    public required bool ReadyForCoordinatedDeploy { get; init; }

    public required string Message { get; init; }

    public required DeployPreflightReadinessSnapshot Readiness { get; init; }

    public required DeployPreflightMigrationSnapshot Migration { get; init; }

    public DeployPreflightDatabaseCompatibilitySnapshot? DatabaseCompatibility { get; init; }
}

internal sealed class DeployPreflightReadinessSnapshot
{
    public required bool IsReady { get; init; }

    public required int StatusCode { get; init; }

    public required string Message { get; init; }
}

internal sealed class DeployPreflightMigrationSnapshot
{
    public required string LifecycleStatus { get; init; }

    public string? Message { get; init; }

    public bool PlanAvailable { get; init; }

    public bool UpgradeRequired { get; init; }

    public IReadOnlyList<string> PendingScripts { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ExecutedButNotDiscoveredScripts { get; init; } = Array.Empty<string>();

    public string? PlanError { get; init; }
}

internal sealed class DeployPreflightDatabaseCompatibilitySnapshot
{
    public required bool IsCompatible { get; init; }

    public required string EngineVersion { get; init; }

    public string? PostGisVersion { get; init; }

    public string? PostGisRasterVersion { get; init; }

    public IReadOnlyList<string> InstalledExtensions { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public string? ErrorMessage { get; init; }
}
