// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Reflection;
using DbUp;
using DbUp.Engine;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure.Migrations;

internal sealed class PostgresDatabaseMigrationRunner : IDatabaseMigrationRunner
{
    private const long MigrationLockKey = 8_044_282_257_919_950_151;
    private const string SafeMigrationFailureMessage = "Database migration failed.";
    private const string BackupHookSucceededOutcome = "succeeded";
    private const string BackupHookFailedOutcome = "failed";
    private const int BackupHookStderrMaxLength = 500;
    private static readonly TimeSpan _migrationLockWaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _migrationLockRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _backupCommandTimeout = TimeSpan.FromHours(1);

    private readonly MigrationSafetyOptions _safetyOptions;
    private readonly IDatabaseMigrationBackupHookRecorder? _backupHookRecorder;
    private readonly string? _contractApprovalToken;

    public PostgresDatabaseMigrationRunner(
        IOptions<MigrationSafetyOptions>? safetyOptions = null,
        IConfiguration? configuration = null,
        IDatabaseMigrationBackupHookRecorder? backupHookRecorder = null)
    {
        _safetyOptions = safetyOptions?.Value ?? new MigrationSafetyOptions();
        _backupHookRecorder = backupHookRecorder;

        // The contract-apply approval is an explicit top-level operator signal
        // (HONUA_APPROVE_CONTRACT_MIGRATIONS=<nonce>), read via the IConfiguration indexer (Abstractions
        // only, no Binder dependency). It intentionally lives outside the bound options record so an
        // operator can grant one-shot approval for a single upgrade without editing config files. The raw
        // token is validated against the pending-script nonce at apply time (#2812), so a stale value can
        // never approve a later, unrelated contract migration.
        var rawApproval = configuration?[MigrationSafetyOptions.ApproveContractMigrationsKey];
        _contractApprovalToken = string.IsNullOrWhiteSpace(rawApproval) ? null : rawApproval.Trim();
    }

    public Task<DatabaseMigrationPlan> PlanMigrationsAsync(
        string connectionString,
        Assembly migrationsAssembly,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateArguments(connectionString, migrationsAssembly);
            cancellationToken.ThrowIfCancellationRequested();

            var upgrader = BuildUpgrader(BuildMigrationConnectionString(connectionString), migrationsAssembly);
            var scripts = upgrader.GetScriptsToExecute();
            var pendingScripts = scripts.Select(script => script.Name).ToArray();
            var classifications = ClassifyScripts(scripts);
            var journalIsNonEmpty = JournalIsNonEmpty(upgrader);
            var executedButNotDiscoveredScripts = upgrader.GetExecutedButNotDiscoveredScripts().ToArray();

            return Task.FromResult(
                DatabaseMigrationPlan.Succeeded(
                    pendingScripts,
                    executedButNotDiscoveredScripts,
                    classifications,
                    journalIsNonEmpty));
        }
        // Intentionally broad: map any planning failure (bad connection string, DbUp/journal errors,
        // etc.) to the typed DatabaseMigrationPlan.Failed result with a sanitized message; the raw
        // exception is carried on the result for the caller to log without leaking it to end users.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Task.FromResult(DatabaseMigrationPlan.Failed(ex, SafeMigrationFailureMessage));
        }
    }

    private static MigrationScriptClassification[] ClassifyScripts(IEnumerable<SqlScript> scripts)
        => scripts
            .Select(script => MigrationSafetyClassifier.Classify(script.Name, script.Contents))
            .ToArray();

    /// <summary>
    /// Fails closed on any pending contract-phase migration that lacks the compatibility-review
    /// marker (ADR-0060 expand/contract gate). Returns <see langword="null"/> when enforcement is
    /// disabled or every pending contract script is annotated.
    /// </summary>
    private InvalidOperationException? TryBuildSafetyRejection(
        IReadOnlyList<MigrationScriptClassification> classifications)
    {
        if (!_safetyOptions.Enforce)
        {
            return null;
        }

        var unannotated = classifications
            .Where(c => c.Classification == MigrationSafetyClassification.ContractUnannotated)
            .Select(c => c.ScriptName)
            .ToArray();

        if (unannotated.Length == 0)
        {
            return null;
        }

        return new InvalidOperationException(
            "Refusing to apply potentially backward-incompatible (contract-phase) migration(s) that are " +
            $"not declared rollout-safe: {string.Join(", ", unannotated)}. Each must carry the marker " +
            $"'{MigrationSafetyClassifier.CompatibilityReviewMarkerConvention}' so its expand/contract " +
            "safety is reviewed, or set 'Database:MigrationSafety:Enforce=false' to override.");
    }

    /// <summary>
    /// Journal-scoped contract-apply gate (#2565, hardened for #2812). When
    /// <see cref="MigrationSafetyOptions.ContractApplyPolicy"/> is
    /// <see cref="ContractApplyPolicy.Gate"/> (the default) and the migration journal is <em>non-empty</em>
    /// (an existing database, not a fresh install), pending annotated contract-phase scripts require the
    /// operator's explicit <c>HONUA_APPROVE_CONTRACT_MIGRATIONS=&lt;nonce&gt;</c> approval, where the nonce is
    /// bound to the exact pending scripts. Returns <see langword="null"/> under Auto policy, on a fresh
    /// install, when the supplied token matches the pending-script nonce, or when nothing pending is an
    /// annotated contract change. The token is one-shot by construction: once the approved scripts are
    /// applied they leave the pending set, so a stale value cannot approve a later contract migration. The
    /// resulting rejection is an <see cref="InvalidOperationException"/> — a non-transient, terminal
    /// failure — so degraded-start recovery (#1632) gives up rather than retrying a policy block with backoff.
    /// </summary>
    private InvalidOperationException? TryBuildContractGateRejection(
        IReadOnlyList<MigrationScriptClassification> classifications,
        bool journalIsNonEmpty)
    {
        if (_safetyOptions.ContractApplyPolicy != ContractApplyPolicy.Gate)
        {
            return null;
        }

        // Fresh installs (empty journal) always provision fully — quickstart stays zero-config and the
        // annotated baseline scripts are never gated.
        if (!journalIsNonEmpty)
        {
            return null;
        }

        var pendingAnnotated = classifications
            .Where(c => c.Classification == MigrationSafetyClassification.ContractAnnotated)
            .Select(c => c.ScriptName)
            .ToArray();

        if (pendingAnnotated.Length == 0)
        {
            return null;
        }

        // One-shot nonce (#2812): the approval token must match the digest of THIS pending contract-script
        // set. A blanket/stale value (e.g. a left-behind "true") never matches, so approving one upgrade
        // does not silently approve the next one.
        var expectedNonce = MigrationSafetyClassifier.ComputeContractApprovalNonce(pendingAnnotated);
        if (_contractApprovalToken != null &&
            string.Equals(_contractApprovalToken, expectedNonce, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new InvalidOperationException(
            "Migration safety gate blocked pending contract-phase migration(s) on an existing database: " +
            $"{string.Join(", ", pendingAnnotated)}. These reviewed (annotated) contract migrations narrow " +
            "or drop schema and must not ride along an unattended upgrade while older nodes are still " +
            "serving (Database:MigrationSafety:ContractApplyPolicy=Gate). Review them first via " +
            "'GET /api/v1/admin/deploy/preflight' and 'GET /api/v1/admin/observability/migrations', then set " +
            $"'{MigrationSafetyOptions.ApproveContractMigrationsKey}={expectedNonce}' to apply them (a one-shot " +
            "token bound to these exact scripts that will not approve any later contract migration), or run " +
            "migrations out-of-band with HONUA_SKIP_MIGRATIONS=true, which owns its own safety.");
    }

    /// <summary>
    /// DbUp reports the migration journal via the same <see cref="UpgradeEngine"/> the runner already
    /// uses: a non-empty executed-scripts list means the database has been migrated before (an upgrade),
    /// while an empty list is a fresh install. On a fresh database the journal table does not yet exist
    /// and DbUp returns an empty list rather than throwing.
    /// </summary>
    private static bool JournalIsNonEmpty(UpgradeEngine upgrader)
        => upgrader.GetExecutedScripts().Count > 0;

    private static string CreateMigrationRunId()
        => "schema-migration-" + Guid.NewGuid().ToString("N");

    public async Task<DatabaseMigrationResult> RunMigrationsAsync(
        string connectionString,
        Assembly migrationsAssembly,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(connectionString, migrationsAssembly);
        cancellationToken.ThrowIfCancellationRequested();

        var migrationConnectionString = BuildMigrationConnectionString(connectionString);

        await using var lockConnection = new NpgsqlConnection(migrationConnectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var lockAcquired = await TryAcquireMigrationLockAsync(lockConnection, cancellationToken).ConfigureAwait(false);
        if (!lockAcquired)
        {
            var error = new TimeoutException(
                $"Timed out waiting {_migrationLockWaitTimeout.TotalMinutes:F0} minute(s) for database migration lock.");
            return DatabaseMigrationResult.Failed(error, error.Message);
        }

        var upgrader = BuildUpgrader(migrationConnectionString, migrationsAssembly);

        try
        {
            var classifications = ClassifyScripts(upgrader.GetScriptsToExecute());
            var journalIsNonEmpty = JournalIsNonEmpty(upgrader);
            var migrationRunId = CreateMigrationRunId();

            // 1. ADR-0060 fail-closed: reject unannotated contract scripts (default Enforce=true).
            if (TryBuildSafetyRejection(classifications) is { } rejection)
            {
                return DatabaseMigrationResult.Failed(rejection, rejection.Message);
            }

            // 2. Journal-scoped contract-apply gate (#2565): block annotated contract scripts on an
            //    existing database unless the operator approved them.
            if (TryBuildContractGateRejection(classifications, journalIsNonEmpty) is { } gateRejection)
            {
                return DatabaseMigrationResult.Failed(gateRejection, gateRejection.Message);
            }

            // 3. Optional pre-migration backup hook: runs only when contract-class scripts are pending
            //    on an existing database, immediately before they are applied. A hook failure fails the
            //    run closed (nothing is applied).
            if (await TryRunBackupHookAsync(classifications, journalIsNonEmpty, migrationRunId, cancellationToken)
                    .ConfigureAwait(false) is { } backupFailure)
            {
                return DatabaseMigrationResult.Failed(backupFailure, backupFailure.Message);
            }

            var result = upgrader.PerformUpgrade();
            var appliedScripts = result.Scripts.Select(script => script.Name).ToArray();

            if (!result.Successful)
            {
                var error = result.Error ?? new InvalidOperationException("Database migration failed.");
                return DatabaseMigrationResult.Failed(error, SafeMigrationFailureMessage, appliedScripts);
            }

            return DatabaseMigrationResult.Succeeded(appliedScripts);
        }
        finally
        {
            await ReleaseMigrationLockAsync(lockConnection).ConfigureAwait(false);
        }
    }

    private static void ValidateArguments(string connectionString, Assembly migrationsAssembly)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string is required.", nameof(connectionString));
        }

        ArgumentNullException.ThrowIfNull(migrationsAssembly);
    }

    private static string BuildMigrationConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            SearchPath = "public",
        }.ConnectionString;

    private static UpgradeEngine BuildUpgrader(string connectionString, Assembly migrationsAssembly) =>
        DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .JournalToPostgresqlTable("public", "schema_versions")
            .WithScriptsEmbeddedInAssembly(migrationsAssembly)
            .WithTransaction()
            .LogToConsole()
            .Build();

    private static async Task<bool> TryAcquireMigrationLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(_migrationLockWaitTimeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@lock_key);", connection);
            _ = command.Parameters.AddWithValue("lock_key", MigrationLockKey);

            var acquired = (bool?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false;
            if (acquired)
            {
                return true;
            }

            await Task.Delay(_migrationLockRetryDelay, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task ReleaseMigrationLockAsync(NpgsqlConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@lock_key);", connection);
            _ = command.Parameters.AddWithValue("lock_key", MigrationLockKey);
            _ = await command.ExecuteScalarAsync().ConfigureAwait(false);
        }
        // Intentionally generic: best-effort unlock; the advisory lock is connection-scoped and
        // will be released when the connection is disposed regardless of this call's outcome.
        // codeql[cs/empty-catch-block] -- best-effort cleanup intentionally ignores this failure.
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
        }
    }

    /// <summary>
    /// Runs the optional pre-migration backup hook (<see cref="MigrationSafetyOptions.BackupCommand"/>)
    /// via the platform shell, but only when contract-class scripts are pending on an existing database
    /// (non-empty journal). A non-zero exit — or a failure launching the process — is returned as an
    /// exception so the caller fails the migration run closed before any script is applied. Returns
    /// <see langword="null"/> when no hook is configured or the run conditions are not met.
    /// </summary>
    /// <remarks>
    /// <see cref="MigrationSafetyOptions.BackupCommand"/> is bound once from configuration sources and is
    /// never writable through an admin API or the database, so shelling out to it is not an
    /// injection/RCE surface (see the options remarks).
    /// </remarks>
    private async Task<Exception?> TryRunBackupHookAsync(
        IReadOnlyList<MigrationScriptClassification> classifications,
        bool journalIsNonEmpty,
        string migrationRunId,
        CancellationToken cancellationToken)
    {
        var command = _safetyOptions.BackupCommand;
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        // Back up only when a contract-class (potentially destructive) script is about to apply on an
        // existing database. Fresh installs and additive-only (expand) upgrades never trigger the hook.
        if (!journalIsNonEmpty || !classifications.Any(c => c.IsBreaking))
        {
            return null;
        }

        var isWindows = OperatingSystem.IsWindows();
        var startInfo = new ProcessStartInfo
        {
            FileName = isWindows ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (isWindows)
        {
            // cmd.exe does not understand the backslash-escaped quotes ArgumentList emits, so pass the
            // command line raw using the canonical `/d /s /c "<command>"` form: /s makes cmd strip only
            // the first and last quote, preserving quoted paths inside the operator's command.
            startInfo.Arguments = $"/d /s /c \"{command}\"";
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var pendingContractScripts = GetPendingContractScripts(classifications);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                await RecordBackupHookOutcomeAsync(
                    BuildBackupHookResult(
                        BackupHookFailedOutcome,
                        succeeded: false,
                        startedAt,
                        stopwatch,
                        pendingContractScripts,
                        migrationRunId,
                        exitCode: null,
                        stderr: null),
                    cancellationToken).ConfigureAwait(false);

                return new InvalidOperationException(
                    "The pre-migration backup command (Database:MigrationSafety:BackupCommand) failed to " +
                    "start; the migration run was aborted before applying any contract-phase script.");
            }

            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_backupCommandTimeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                await RecordBackupHookOutcomeAsync(
                    BuildBackupHookResult(
                        BackupHookFailedOutcome,
                        succeeded: false,
                        startedAt,
                        stopwatch,
                        pendingContractScripts,
                        migrationRunId,
                        exitCode: null,
                        stderr: null),
                    CancellationToken.None).ConfigureAwait(false);

                return new TimeoutException(
                    $"The pre-migration backup command (Database:MigrationSafety:BackupCommand) exceeded " +
                    $"{_backupCommandTimeout.TotalMinutes:F0} minute(s) and was terminated; the migration run " +
                    "was aborted before applying any contract-phase script.");
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? string.Empty
                    : $" Details: {Truncate(stderr.Trim(), BackupHookStderrMaxLength)}";
                await RecordBackupHookOutcomeAsync(
                    BuildBackupHookResult(
                        BackupHookFailedOutcome,
                        succeeded: false,
                        startedAt,
                        stopwatch,
                        pendingContractScripts,
                        migrationRunId,
                        process.ExitCode,
                        string.IsNullOrWhiteSpace(stderr)
                            ? null
                            : Truncate(stderr.Trim(), BackupHookStderrMaxLength)),
                    cancellationToken).ConfigureAwait(false);

                return new InvalidOperationException(
                    "The pre-migration backup command (Database:MigrationSafety:BackupCommand) exited with " +
                    $"code {process.ExitCode}; the migration run was aborted (fail closed) before applying any " +
                    $"contract-phase script.{detail}");
            }

            await RecordBackupHookOutcomeAsync(
                BuildBackupHookResult(
                    BackupHookSucceededOutcome,
                    succeeded: true,
                    startedAt,
                    stopwatch,
                    pendingContractScripts,
                    migrationRunId,
                    process.ExitCode,
                    stderr: null),
                cancellationToken).ConfigureAwait(false);

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordBackupHookOutcomeAsync(
                BuildBackupHookResult(
                    BackupHookFailedOutcome,
                    succeeded: false,
                    startedAt,
                    stopwatch,
                    pendingContractScripts,
                    migrationRunId,
                    exitCode: null,
                    stderr: null),
                cancellationToken).ConfigureAwait(false);

            return new InvalidOperationException(
                "The pre-migration backup command (Database:MigrationSafety:BackupCommand) could not be run; " +
                "the migration run was aborted (fail closed) before applying any contract-phase script.",
                ex);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        // Intentionally generic: best-effort cleanup of a timed-out backup-hook process; the process
        // may have already exited between the HasExited check and Kill.
        // codeql[cs/empty-catch-block] -- best-effort cleanup intentionally ignores this failure.
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";

    private static string[] GetPendingContractScripts(IReadOnlyList<MigrationScriptClassification> classifications)
        => classifications
            .Where(classification => classification.IsBreaking)
            .Select(classification => classification.ScriptName)
            .ToArray();

    private static DatabaseMigrationBackupHookResult BuildBackupHookResult(
        string outcome,
        bool succeeded,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        IReadOnlyList<string> pendingContractScripts,
        string migrationRunId,
        int? exitCode,
        string? stderr)
    {
        stopwatch.Stop();
        return new DatabaseMigrationBackupHookResult
        {
            Outcome = outcome,
            Succeeded = succeeded,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMilliseconds = Math.Max(0, stopwatch.ElapsedMilliseconds),
            ExitCode = exitCode,
            Stderr = stderr,
            PendingContractScripts = pendingContractScripts,
            MigrationRunId = migrationRunId,
            CorrelationId = migrationRunId
        };
    }

    private async Task RecordBackupHookOutcomeAsync(
        DatabaseMigrationBackupHookResult result,
        CancellationToken cancellationToken)
    {
        if (_backupHookRecorder is null)
        {
            return;
        }

        try
        {
            await _backupHookRecorder.RecordAsync(result, cancellationToken).ConfigureAwait(false);
        }
        // Intentionally generic: backup-hook observability recording must not change migration
        // apply/fail-closed semantics, so any recorder failure here is swallowed.
        // codeql[cs/empty-catch-block] -- best-effort cleanup intentionally ignores this failure.
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
        }
    }
}
