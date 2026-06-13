// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Helpers;
using Microsoft.Extensions.Hosting;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Background loop that restores database access after the host started in degraded mode because the
/// database was unavailable at boot (issue #1632). It retries the PostGIS preflight check and pending
/// migrations with bounded exponential backoff, marking <see cref="MigrationState"/> ready once the
/// database is reachable so <c>/healthz/ready</c> flips to ready. The loop is inert (no-op) when the
/// host started normally, so it adds nothing to the steady-state path.
/// </summary>
internal sealed class DatabaseRecoveryBackgroundService : BackgroundService
{
    private readonly DegradedStartupContext _context;
    private readonly MigrationState _migrationState;
    private readonly DatabaseCompatibilityState _compatibilityState;
    private readonly IConfiguration _configuration;
    private readonly StartupResilienceOptions _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<DatabaseRecoveryBackgroundService> _logger;

    public DatabaseRecoveryBackgroundService(
        DegradedStartupContext context,
        MigrationState migrationState,
        DatabaseCompatibilityState compatibilityState,
        IConfiguration configuration,
        StartupResilienceOptions options,
        IServiceProvider services,
        ILogger<DatabaseRecoveryBackgroundService> logger)
    {
        _context = context;
        _migrationState = migrationState;
        _compatibilityState = compatibilityState;
        _configuration = configuration;
        _options = options;
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_context.IsDegraded)
        {
            // Normal startup: nothing to recover.
            return;
        }

        MonitoringLog.DatabaseRecoveryStarting(_logger);

        var attempt = 0;
        var jitter = Random.Shared;

        while (!stoppingToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await TryRecoverAsync(stoppingToken);
                MonitoringLog.DatabaseRecoverySucceeded(_logger, attempt);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!StartupDatabaseResilience.IsTransientConnectivityError(ex))
                {
                    // A non-transient failure surfaced during recovery (e.g. the database is now
                    // reachable but a migration script is invalid). Stop retrying; this needs an
                    // operator, not another connection attempt.
                    MonitoringLog.DatabaseRecoveryGaveUp(_logger, attempt, ex.Message, ex);
                    return;
                }

                if (_options.MaxRetryAttempts > 0 && attempt >= _options.MaxRetryAttempts)
                {
                    MonitoringLog.DatabaseRecoveryGaveUp(_logger, attempt, ex.Message, ex);
                    return;
                }

                var delay = StartupDatabaseResilience.GetRetryDelay(
                    attempt, _options.RetryBaseDelay, _options.RetryMaxDelay, jitter);
                MonitoringLog.DatabaseRecoveryAttemptFailed(
                    _logger, attempt, delay.TotalSeconds, ex.Message, ex);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task TryRecoverAsync(CancellationToken cancellationToken)
    {
        var secretResolver = _services.GetService(typeof(IConnectionSecretResolver)) as IConnectionSecretResolver;
        var connectionString = await ConnectionStringResolutionHelper.ResolveDefaultConnectionStringAsync(
            _configuration, secretResolver, cancellationToken);

        if (string.IsNullOrEmpty(connectionString))
        {
            // No connection string: there is nothing to recover to. Treat as terminal.
            throw new InvalidOperationException("No database connection string is configured.");
        }

        // PostGIS preflight: confirms the database is reachable AND compatible.
        if (_services.GetService(typeof(IDatabaseCompatibilityChecker)) is IDatabaseCompatibilityChecker checker)
        {
            var compatibility = await checker.CheckCompatibilityAsync(connectionString, cancellationToken);
            _compatibilityState.SetResult(compatibility);
            if (!compatibility.IsCompatible)
            {
                // Reaching the server but failing compatibility is a non-transient condition.
                throw new InvalidOperationException(
                    compatibility.ErrorMessage ?? "Database compatibility check failed.");
            }
        }

        // Rerun migrations if they were pending at degraded start.
        if (_context.MigrationsPending
            && _options.RetryMigrationsInBackground
            && _services.GetService(typeof(IDatabaseMigrationRunner)) is IDatabaseMigrationRunner runner)
        {
            var assembly = _context.MigrationsAssembly ?? Assembly.GetEntryAssembly();
            if (assembly is not null)
            {
                _migrationState.MarkRunning("Re-applying database migrations after recovery.");
                var result = await runner.RunMigrationsAsync(connectionString, assembly, cancellationToken);
                if (!result.Successful)
                {
                    var error = result.Error
                        ?? new InvalidOperationException(result.ErrorMessage ?? "Database migration failed.");
                    throw error;
                }
            }
        }

        _migrationState.MarkSucceeded("Database recovered; migrations are up to date.");
    }
}
