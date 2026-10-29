// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Health;
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
    private readonly SemaphoreSlim _attemptGate = new(1, 1);
    private int _attempts; // guarded by _attemptGate

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
        RecoversOnReadinessProbe = !string.IsNullOrWhiteSpace(
            configuration[StartupDatabasePreflight.AwsLambdaFunctionNameKey]);
        if (RecoversOnReadinessProbe)
        {
            // Readiness already depends on the degraded-start context. Registering here keeps the
            // probe attempt on that context instead of adding this service as another collaborator.
            context.RegisterReadinessProbeRecovery(async (budget, token) =>
            {
                await TryRecoverOnReadinessProbeAsync(budget, token);
            });
        }
    }

    /// <summary>
    /// Whether a readiness probe should drive a recovery attempt itself instead of only reporting
    /// the background loop's state: true on AWS Lambda, where this loop runs only while an
    /// invocation is executing.
    /// </summary>
    /// <remarks>
    /// While degraded, <c>/healthz/ready</c> answers 503 in milliseconds without touching the
    /// database, so each invocation ends before a background connect through RDS Proxy (TCP, TLS,
    /// SCRAM) can finish; the environment is frozen between probes and the connect's wall-clock
    /// timeout expires while it is frozen. The instance then never leaves degraded mode even after
    /// the migration task ran (honua-release run 40746787170: a pre-migration cold start served
    /// schema-floor for 15 minutes while every background connect timed out). Running one bounded
    /// attempt inside the probe keeps the invocation alive for the whole handshake.
    /// </remarks>
    internal bool RecoversOnReadinessProbe { get; }

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
            Exception? failure;
            try
            {
                failure = await RunAttemptAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            if (failure is null)
            {
                return;
            }

            if (_context.RecoveryFailure is not null)
            {
                // A non-transient failure: RunAttemptAsync already recorded it.
                MonitoringLog.DatabaseRecoveryGaveUp(_logger, attempt, failure.Message, failure);
                return;
            }

            if (_options.MaxRetryAttempts > 0 && attempt >= _options.MaxRetryAttempts)
            {
                _context.MarkRecoveryAbandoned(DescribeFailure(failure));
                MonitoringLog.DatabaseRecoveryGaveUp(_logger, attempt, failure.Message, failure);
                return;
            }

            var delay = StartupDatabaseResilience.GetRetryDelay(
                attempt, _options.RetryBaseDelay, _options.RetryMaxDelay, jitter);
            MonitoringLog.DatabaseRecoveryAttemptFailed(
                _logger, attempt, delay.TotalSeconds, failure.Message, failure);

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

    /// <summary>
    /// Runs one recovery attempt inside a readiness probe, bounded by <paramref name="budget"/>, when
    /// <see cref="RecoversOnReadinessProbe"/> holds and the host is still awaiting the database.
    /// Never throws for a failed attempt: the probe reads the outcome from the degraded-start state.
    /// </summary>
    /// <param name="budget">Upper bound on the attempt, including waiting for an attempt in flight.</param>
    /// <param name="cancellationToken">The probe's cancellation token.</param>
    /// <returns>True when the host is no longer awaiting the database.</returns>
    internal async Task<bool> TryRecoverOnReadinessProbeAsync(TimeSpan budget, CancellationToken cancellationToken)
    {
        if (!RecoversOnReadinessProbe || !_context.IsAwaitingDatabase || _context.RecoveryFailure is not null)
        {
            return !_context.IsAwaitingDatabase;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(budget);
        try
        {
            await RunAttemptAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The budget ran out (or an attempt in flight held the gate): the next probe retries.
        }

        return !_context.IsAwaitingDatabase;
    }

    /// <summary>
    /// Runs one single-flight recovery attempt, marks the host recovered on success, and classifies
    /// a failure. Returns null when the host is no longer awaiting the database.
    /// </summary>
    private async Task<Exception?> RunAttemptAsync(CancellationToken cancellationToken)
    {
        await _attemptGate.WaitAsync(cancellationToken);
        try
        {
            if (!_context.IsAwaitingDatabase)
            {
                return null;
            }

            var attempt = ++_attempts;
            try
            {
                await TryRecoverAsync(cancellationToken);
                _context.MarkRecovered();
                MonitoringLog.DatabaseRecoverySucceeded(_logger, attempt);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Intentional: this is the top-level boundary of one recovery attempt; any exception
            // type (connection, auth, migration, or otherwise) must be classified via
            // IsTransientConnectivityError below rather than crash the background service, so
            // retries/backoff and the terminal "give up" path both stay reachable.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (IsSchemaBehindBinary(ex, out var schemaFloorReason))
                {
                    // The database is reachable but a required migration is not applied yet (a cold
                    // start before the migration task ran): keep waiting, and say so on readiness.
                    _context.UpdateReason(ReadinessReasonCodes.SchemaFloor, schemaFloorReason);
                }
                else if (!IsTransient(ex) && !cancellationToken.IsCancellationRequested)
                {
                    // (A failure surfaced by a cancelled attempt, such as a probe whose budget ran
                    // out mid-connect, is never classified as terminal.)
                    // A non-transient failure surfaced during recovery (e.g. the database is now
                    // reachable but a migration script is invalid). Stop retrying; this needs an
                    // operator, not another connection attempt.
                    _context.MarkRecoveryAbandoned(DescribeFailure(ex));
                }

                return ex;
            }
        }
        finally
        {
            _attemptGate.Release();
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _attemptGate.Dispose();
        base.Dispose();
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
            if (compatibility.IsTransientFailure)
            {
                // Still unreachable (slot exhaustion, timeout): keep retrying.
                throw new TransientRecoveryFailureException(
                    compatibility.FailureClass ?? "database unavailable",
                    compatibility.Error);
            }

            if (!compatibility.IsCompatible)
            {
                // Reaching the server but failing compatibility is a non-transient condition.
                throw new IncompatibleDatabaseException(
                    compatibility.ErrorMessage ?? "Database compatibility check failed.");
            }
        }

        // Migrations skipped by configuration: degraded start deferred the journal/schema guard,
        // it did not waive it.
        if (_context.SchemaGuardPending || (_context.MigrationsPending && !_options.RetryMigrationsInBackground))
        {
            var schemaGuard = _services.GetService(typeof(IDatabaseSchemaGuard)) as IDatabaseSchemaGuard
                ?? throw new InvalidOperationException("Database recovery requires the deferred schema guard.");
            if (_context.SchemaGuardPending)
            {
                var adoptedBaseline = schemaGuard.TryAdoptMatchingRasterBaselineAsync(connectionString, cancellationToken);
                if (adoptedBaseline is not null)
                {
                    var adoptedScript = await adoptedBaseline.ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(adoptedScript))
                    {
                        MonitoringLog.RasterProviderBaselineAdopted(_logger, adoptedScript);
                    }
                }
            }

            await schemaGuard.VerifyAsync(connectionString, cancellationToken);
            _migrationState.MarkSkipped("Migrations skipped by configuration; journal/schema consistency verified after recovery.");
            return;
        }

        // Rerun migrations if they were pending at degraded start.
        if (_context.MigrationsPending)
        {
            var runner = _services.GetService(typeof(IDatabaseMigrationRunner)) as IDatabaseMigrationRunner
                ?? throw new InvalidOperationException("Database recovery requires the deferred migration runner.");
            var assembly = _context.MigrationsAssembly ?? Assembly.GetEntryAssembly()
                ?? throw new InvalidOperationException("Database recovery requires the migration assembly.");
            _migrationState.MarkRunning("Re-applying database migrations after recovery.");
            var result = await runner.RunMigrationsAsync(connectionString, assembly, cancellationToken);
            if (!result.Successful)
            {
                var error = result.Error
                    ?? new InvalidOperationException(result.ErrorMessage ?? "Database migration failed.");
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                return; // unreachable; satisfies the compiler
            }
        }

        _migrationState.MarkSucceeded("Database recovered; migrations are up to date.");
    }

    /// <summary>
    /// Whether <paramref name="exception"/> says the schema is behind this binary: a required
    /// numbered migration is not journaled as applied. A schema that diverges from its journal, or a
    /// database newer than this binary (<see cref="DatabaseSchemaCompatibilityException"/>), is not.
    /// </summary>
    internal static bool IsSchemaBehindBinary(Exception exception, out string reason)
    {
        if (exception is DatabaseSchemaFloorException
            {
                FailureKind: DatabaseSchemaFloorFailureKind.MigrationNotApplied,
            } floor)
        {
            reason = BuildSchemaFloorReason(floor.MigrationScript);
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>Sanitized schema-floor reason (the migration script name only).</summary>
    internal static string BuildSchemaFloorReason(string migrationScript)
        => "schema guard: migration '" + migrationScript + "' not applied";

    private static bool IsTransient(Exception exception)
        => exception is TransientRecoveryFailureException
            || StartupDatabaseResilience.IsTransientConnectivityError(exception);

    private static string DescribeFailure(Exception exception)
        => exception switch
        {
            TransientRecoveryFailureException transient => transient.FailureClass,
            IncompatibleDatabaseException incompatible => incompatible.Message,
            _ => StartupDatabaseResilience.DescribeFailureClass(exception),
        };

    /// <summary>The compatibility probe still could not reach the database.</summary>
    private sealed class TransientRecoveryFailureException(string failureClass, Exception? innerException)
        : Exception("Database still unavailable: " + failureClass + ".", innerException)
    {
        public string FailureClass { get; } = failureClass;
    }

    /// <summary>The database is reachable but failed the compatibility check.</summary>
    private sealed class IncompatibleDatabaseException(string message) : InvalidOperationException(message);
}
