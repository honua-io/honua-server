// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbUp;
using DbUp.Engine;
using Honua.Core.Configuration;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Postgres.Features.Infrastructure.Migrations;

internal sealed class PostgresDatabaseMigrationRunner : IDatabaseMigrationRunner
{
    private const long MigrationLockKey = 8_044_282_257_919_950_151;
    private const string SafeMigrationFailureMessage = "Database migration failed.";
    private static readonly TimeSpan _migrationLockWaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _migrationLockRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _backupCommandTimeout = TimeSpan.FromHours(1);
    private static readonly Regex _backupHookUriUserInfoPattern = new(
        "\\b([A-Za-z][A-Za-z0-9+.-]*://)([^/@\\s]+)@",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex _backupHookSensitiveQueryPattern = new(
        "([?&](?:password|pwd|secret|token|api[-_]?key|access[-_]?key|client[-_]?secret)=)[^&#\\s]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex _backupHookSensitiveKeyValuePattern = new(
        "\\b([A-Za-z0-9_.-]*(?:password|pwd|secret|token|api[-_]?key|access[-_]?key|client[-_]?secret|connection[-_]?string|connstr)[A-Za-z0-9_.-]*)\\b\\s*([:=])\\s*([^;\\s]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex _backupHookSensitiveOptionPattern = new(
        "(--(?:password|pwd|secret|token|api[-_]?key|access[-_]?key|client[-_]?secret)\\s+)([^\\s]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex _backupHookAuthorizationPattern = new(
        "\\bauthorization\\b\\s*([:=])\\s*([^;\\s]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex _backupHookBearerPattern = new(
        "\\bbearer\\s+[A-Za-z0-9-._~+/]+=*",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly MigrationSafetyOptions _safetyOptions;
    private readonly bool _contractMigrationsApproved;
    private readonly IMigrationBackupHookOutcomeStore? _backupHookOutcomeStore;
    private readonly IServiceScopeFactory? _serviceScopeFactory;

    public PostgresDatabaseMigrationRunner(
        IOptions<MigrationSafetyOptions>? safetyOptions = null,
        IConfiguration? configuration = null,
        IMigrationBackupHookOutcomeStore? backupHookOutcomeStore = null,
        IServiceScopeFactory? serviceScopeFactory = null)
    {
        _safetyOptions = safetyOptions?.Value ?? new MigrationSafetyOptions();
        _backupHookOutcomeStore = backupHookOutcomeStore;
        _serviceScopeFactory = serviceScopeFactory;

        // The contract-apply approval is an explicit top-level operator signal
        // (HONUA_APPROVE_CONTRACT_MIGRATIONS=true), read via the IConfiguration indexer (Abstractions
        // only, no Binder dependency). It intentionally lives outside the bound options record so an
        // operator can grant one-shot approval for a single upgrade without editing config files.
        _contractMigrationsApproved =
            bool.TryParse(configuration?[MigrationSafetyOptions.ApproveContractMigrationsKey], out var approved)
            && approved;
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
            var executedButNotDiscoveredScripts = upgrader.GetExecutedButNotDiscoveredScripts().ToArray();

            return Task.FromResult(
                DatabaseMigrationPlan.Succeeded(pendingScripts, executedButNotDiscoveredScripts, classifications));
        }
        catch (Exception ex)
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
    /// Journal-scoped contract-apply gate (#2565). When
    /// <see cref="MigrationSafetyOptions.ContractApplyPolicy"/> is
    /// <see cref="ContractApplyPolicy.Gate"/> and the migration journal is <em>non-empty</em> (an
    /// existing database, not a fresh install), pending annotated contract-phase scripts require the
    /// operator's explicit <c>HONUA_APPROVE_CONTRACT_MIGRATIONS=true</c> approval. Returns
    /// <see langword="null"/> under Auto policy, on a fresh install, when approved, or when nothing
    /// pending is an annotated contract change. The resulting rejection is an
    /// <see cref="InvalidOperationException"/> — a non-transient, terminal failure — so degraded-start
    /// recovery (#1632) gives up rather than retrying a policy block with backoff.
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

        if (_contractMigrationsApproved)
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

        return new InvalidOperationException(
            "Migration safety gate blocked pending contract-phase migration(s) on an existing database: " +
            $"{string.Join(", ", pendingAnnotated)}. These reviewed (annotated) contract migrations narrow " +
            "or drop schema and must not ride along an unattended upgrade (Database:MigrationSafety:" +
            "ContractApplyPolicy=Gate). Review them first via 'GET /api/v1/admin/deploy/preflight' and " +
            $"'GET /api/v1/admin/observability/migrations', then set '{MigrationSafetyOptions.ApproveContractMigrationsKey}" +
            "=true' to apply them (or run migrations out-of-band with HONUA_SKIP_MIGRATIONS=true, which owns " +
            "its own safety).");
    }

    /// <summary>
    /// DbUp reports the migration journal via the same <see cref="UpgradeEngine"/> the runner already
    /// uses: a non-empty executed-scripts list means the database has been migrated before (an upgrade),
    /// while an empty list is a fresh install. On a fresh database the journal table does not yet exist
    /// and DbUp returns an empty list rather than throwing.
    /// </summary>
    private static bool JournalIsNonEmpty(UpgradeEngine upgrader)
        => upgrader.GetExecutedScripts().Count > 0;

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
            if (await TryRunBackupHookAsync(classifications, journalIsNonEmpty, cancellationToken)
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
        catch
        {
            // The advisory lock is connection-scoped and will be released when the connection is disposed.
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

        var pendingScripts = classifications
            .Where(c => c.IsBreaking)
            .Select(c => c.ScriptName)
            .ToArray();
        var migrationRunId = Guid.NewGuid().ToString("N");
        var pendingSetFingerprint = ComputePendingSetFingerprint(pendingScripts);
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        async Task<Exception?> CompleteAsync(
            bool succeeded,
            int? exitCode,
            string? sanitizedStderr,
            Exception? failure)
        {
            stopwatch.Stop();
            var outcome = new MigrationBackupHookOutcome
            {
                MigrationRunId = migrationRunId,
                PendingSetFingerprint = pendingSetFingerprint,
                PendingScripts = pendingScripts,
                Succeeded = succeeded,
                DurationMs = Math.Max(0, (long)stopwatch.Elapsed.TotalMilliseconds),
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                ExitCode = exitCode,
                TruncatedStderr = sanitizedStderr
            };

            await RecordBackupHookOutcomeAsync(outcome, CancellationToken.None).ConfigureAwait(false);
            return failure;
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

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return await CompleteAsync(
                    succeeded: false,
                    exitCode: null,
                    sanitizedStderr: null,
                    failure: new InvalidOperationException(
                    "The pre-migration backup command (Database:MigrationSafety:BackupCommand) failed to " +
                    "start; the migration run was aborted before applying any contract-phase script."))
                    .ConfigureAwait(false);
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
                return await CompleteAsync(
                    succeeded: false,
                    exitCode: null,
                    sanitizedStderr: null,
                    failure: new TimeoutException(
                    $"The pre-migration backup command (Database:MigrationSafety:BackupCommand) exceeded " +
                    $"{_backupCommandTimeout.TotalMinutes:F0} minute(s) and was terminated; the migration run " +
                    "was aborted before applying any contract-phase script."))
                    .ConfigureAwait(false);
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var sanitizedStderr = SanitizeBackupHookStderr(stderr);
                var detail = string.IsNullOrWhiteSpace(sanitizedStderr)
                    ? string.Empty
                    : $" Details: {sanitizedStderr}";
                return await CompleteAsync(
                    succeeded: false,
                    exitCode: process.ExitCode,
                    sanitizedStderr: sanitizedStderr,
                    failure: new InvalidOperationException(
                    "The pre-migration backup command (Database:MigrationSafety:BackupCommand) exited with " +
                    $"code {process.ExitCode}; the migration run was aborted (fail closed) before applying any " +
                    $"contract-phase script.{detail}"))
                    .ConfigureAwait(false);
            }

            return await CompleteAsync(
                    succeeded: true,
                    exitCode: process.ExitCode,
                    sanitizedStderr: null,
                    failure: null)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await CompleteAsync(
                succeeded: false,
                exitCode: null,
                sanitizedStderr: null,
                failure: new InvalidOperationException(
                "The pre-migration backup command (Database:MigrationSafety:BackupCommand) could not be run; " +
                "the migration run was aborted (fail closed) before applying any contract-phase script.",
                ex))
                .ConfigureAwait(false);
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
        catch
        {
            // Best-effort cleanup; the process may have already exited.
        }
    }

    private async Task RecordBackupHookOutcomeAsync(
        MigrationBackupHookOutcome outcome,
        CancellationToken cancellationToken)
    {
        _backupHookOutcomeStore?.Record(outcome);

        if (_serviceScopeFactory is null)
        {
            return;
        }

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var auditLog = scope.ServiceProvider.GetService<IAuditLog>();
            if (auditLog is null)
            {
                return;
            }

            await auditLog.RecordAsync(
                    new AuditEvent
                    {
                        Timestamp = outcome.CompletedAt,
                        EventType = AuditEventType.AdminAction,
                        Actor = "honua-server",
                        ActorType = AuditActorType.System,
                        ResourceType = "database_migration_run",
                        ResourceId = outcome.MigrationRunId,
                        Action = "migration.backup_hook",
                        Outcome = outcome.Succeeded ? AuditOutcome.Success : AuditOutcome.Failure,
                        CorrelationId = outcome.MigrationRunId,
                        Details = BuildBackupHookOutcomeDetailsJson(outcome)
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort audit publication must not change migration fail-closed behavior.
        }
    }

    private static string ComputePendingSetFingerprint(IReadOnlyList<string> pendingScripts)
    {
        var material = string.Join('\n', pendingScripts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildBackupHookOutcomeDetailsJson(MigrationBackupHookOutcome outcome)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("migrationRunId", outcome.MigrationRunId);
            writer.WriteString("pendingSetFingerprint", outcome.PendingSetFingerprint);
            writer.WriteStartArray("pendingScripts");
            foreach (var script in outcome.PendingScripts)
            {
                writer.WriteStringValue(script);
            }

            writer.WriteEndArray();
            writer.WriteBoolean("succeeded", outcome.Succeeded);
            writer.WriteNumber("durationMs", outcome.DurationMs);
            writer.WriteString("startedAt", outcome.StartedAt);
            writer.WriteString("completedAt", outcome.CompletedAt);
            if (outcome.ExitCode.HasValue)
            {
                writer.WriteNumber("exitCode", outcome.ExitCode.Value);
            }
            else
            {
                writer.WriteNull("exitCode");
            }

            if (outcome.TruncatedStderr is { Length: > 0 } stderr)
            {
                writer.WriteString("truncatedStderr", stderr);
            }
            else
            {
                writer.WriteNull("truncatedStderr");
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? SanitizeBackupHookStderr(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return null;
        }

        var sanitized = stderr.Trim();
        sanitized = _backupHookUriUserInfoPattern.Replace(sanitized, "$1***@");
        sanitized = _backupHookSensitiveQueryPattern.Replace(sanitized, "$1***");
        sanitized = _backupHookSensitiveKeyValuePattern.Replace(sanitized, "$1$2***");
        sanitized = _backupHookSensitiveOptionPattern.Replace(sanitized, "$1***");
        sanitized = _backupHookAuthorizationPattern.Replace(sanitized, "Authorization$1***");
        sanitized = _backupHookBearerPattern.Replace(sanitized, "Bearer ***");
        return Truncate(sanitized, 500);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
