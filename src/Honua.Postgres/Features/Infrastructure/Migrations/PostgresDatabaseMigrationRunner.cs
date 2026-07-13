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

    private const string ContractApprovalTable = "public.honua_contract_migration_approvals";

    private readonly MigrationSafetyOptions _safetyOptions;
    private readonly IDatabaseMigrationBackupHookRecorder? _backupHookRecorder;
    private readonly IActiveNodeVersionInventory _nodeVersionInventory;
    private readonly string? _contractApprovalNonce;

    public PostgresDatabaseMigrationRunner(
        IOptions<MigrationSafetyOptions>? safetyOptions = null,
        IConfiguration? configuration = null,
        IDatabaseMigrationBackupHookRecorder? backupHookRecorder = null,
        IActiveNodeVersionInventory? nodeVersionInventory = null)
    {
        _safetyOptions = safetyOptions?.Value ?? new MigrationSafetyOptions();
        _backupHookRecorder = backupHookRecorder;

        // Live cluster version inventory (#2812). Absent (single-node/dev-compose or no coordination
        // backend) means the mixed-version barrier stays inert and boot proceeds with zero new config.
        _nodeVersionInventory = nodeVersionInventory ?? NullActiveNodeVersionInventory.Instance;

        // The contract-apply approval is an explicit top-level operator signal
        // (HONUA_APPROVE_CONTRACT_MIGRATIONS=<nonce>), read via the IConfiguration indexer
        // (Abstractions only, no Binder dependency). It intentionally lives outside the bound options
        // record so an operator can grant approval for a single upgrade without editing config files.
        // The value is treated as a one-shot nonce: it is recorded once consumed (see
        // ContractApprovalTable) so a value left permanently in the environment cannot silently
        // approve a *later* contract batch (#2812). The legacy literal "true" remains a valid nonce.
        var nonce = configuration?[MigrationSafetyOptions.ApproveContractMigrationsKey]?.Trim();
        _contractApprovalNonce = string.IsNullOrWhiteSpace(nonce) ? null : nonce;
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
        bool journalIsNonEmpty,
        ApprovalEvaluation approval)
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

        if (approval.State == ApprovalState.Fresh)
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

        var approvalHint = approval.State == ApprovalState.AlreadyConsumed
            ? $"The supplied {MigrationSafetyOptions.ApproveContractMigrationsKey} approval has already been " +
              "consumed for an earlier contract batch and is a one-shot nonce; supply a fresh, unused value to " +
              "approve this batch."
            : $"Review them first via 'GET /api/v1/admin/deploy/preflight' and " +
              $"'GET /api/v1/admin/observability/migrations', then set '{MigrationSafetyOptions.ApproveContractMigrationsKey}" +
              "=<a fresh one-shot nonce>' to apply them (or run migrations out-of-band with HONUA_SKIP_MIGRATIONS=true, " +
              "which owns its own safety).";

        return new InvalidOperationException(
            "Migration safety gate blocked pending contract-phase migration(s) on an existing database: " +
            $"{string.Join(", ", pendingAnnotated)}. These reviewed (annotated) contract migrations narrow " +
            "or drop schema and must not ride along an unattended upgrade (Database:MigrationSafety:" +
            $"ContractApplyPolicy=Gate). {approvalHint}");
    }

    /// <summary>
    /// Live node-version barrier (#2812). When a coordinated inventory positively observes another live
    /// serving node running a <em>different</em> binary version than this node (a mixed-version rolling
    /// upgrade in progress), pending contract-phase (schema-narrowing) scripts must not apply — the old
    /// binaries still serving traffic may not understand the narrowed schema. Returns
    /// <see langword="null"/> when no coordinated inventory is available (single-node/dev-compose), when
    /// every live node runs this version, when nothing pending is a contract change, or when the
    /// operator supplied a fresh one-shot approval nonce. The rejection is a non-transient
    /// <see cref="InvalidOperationException"/> so degraded-start recovery does not retry a policy block.
    /// </summary>
    private static InvalidOperationException? TryBuildNodeVersionBarrierRejection(
        IReadOnlyList<MigrationScriptClassification> classifications,
        ActiveNodeVersionSnapshot snapshot,
        ApprovalEvaluation approval)
    {
        if (!snapshot.MixedVersionDetected)
        {
            return null;
        }

        var pendingContract = classifications
            .Where(c => c.IsBreaking)
            .Select(c => c.ScriptName)
            .ToArray();

        if (pendingContract.Length == 0)
        {
            return null;
        }

        if (approval.State == ApprovalState.Fresh)
        {
            return null;
        }

        var approvalHint = approval.State == ApprovalState.AlreadyConsumed
            ? $"The supplied {MigrationSafetyOptions.ApproveContractMigrationsKey} approval has already been " +
              "consumed and is a one-shot nonce; supply a fresh, unused value to override the barrier."
            : "Finish the rolling upgrade so every live node runs this version, then re-run migrations; or, " +
              "to deliberately override during a coordinated contract step, set " +
              $"'{MigrationSafetyOptions.ApproveContractMigrationsKey}=<a fresh one-shot nonce>'.";

        return new InvalidOperationException(
            "Migration node-version barrier blocked pending contract-phase migration(s): " +
            $"{string.Join(", ", pendingContract)}. A live serving node is still running a different binary " +
            $"version (this node: {snapshot.LocalVersion}; other live version(s): " +
            $"{string.Join(", ", snapshot.DivergentVersions)}), so narrowing or dropping schema now could break " +
            $"the older binaries still serving traffic. {approvalHint}");
    }

    /// <summary>
    /// Evaluates the operator's contract-approval nonce against the consumed-nonce ledger so approval is
    /// one-shot: an unused nonce is <see cref="ApprovalState.Fresh"/> (valid this once), a nonce already
    /// recorded from an earlier contract batch is <see cref="ApprovalState.AlreadyConsumed"/> (rejected),
    /// and an absent nonce is <see cref="ApprovalState.NotProvided"/>.
    /// </summary>
    private async Task<ApprovalEvaluation> EvaluateApprovalAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        if (_contractApprovalNonce is null)
        {
            return new ApprovalEvaluation(ApprovalState.NotProvided, null);
        }

        await EnsureContractApprovalTableAsync(connection, cancellationToken).ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            $"SELECT 1 FROM {ContractApprovalTable} WHERE nonce = @nonce;",
            connection);
        _ = command.Parameters.AddWithValue("nonce", _contractApprovalNonce);
        var consumed = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;

        return new ApprovalEvaluation(
            consumed ? ApprovalState.AlreadyConsumed : ApprovalState.Fresh,
            _contractApprovalNonce);
    }

    private static async Task EnsureContractApprovalTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            CREATE TABLE IF NOT EXISTS {ContractApprovalTable} (
                nonce text PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT now(),
                scripts text NOT NULL
            );
            """,
            connection);
        _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a contract-approval nonce as consumed so the same value cannot approve a later batch.
    /// Runs only after a successful apply and under the migration advisory lock, so it is serialized.
    /// Best-effort: an insert failure never changes the already-committed migration outcome.
    /// </summary>
    private static async Task RecordApprovalConsumedAsync(
        NpgsqlConnection connection,
        string nonce,
        IReadOnlyList<string> appliedContractScripts,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureContractApprovalTableAsync(connection, cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"""
                INSERT INTO {ContractApprovalTable} (nonce, scripts)
                VALUES (@nonce, @scripts)
                ON CONFLICT (nonce) DO NOTHING;
                """,
                connection);
            _ = command.Parameters.AddWithValue("nonce", nonce);
            _ = command.Parameters.AddWithValue("scripts", string.Join(", ", appliedContractScripts));
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Consuming the one-shot nonce is bookkeeping; the migration already applied successfully.
        }
    }

    /// <summary>
    /// Reads the live cluster version snapshot, failing open (no coordination) on any inventory error so
    /// a coordination-backend outage never blocks an otherwise-safe migration.
    /// </summary>
    private async Task<ActiveNodeVersionSnapshot> GetNodeVersionSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _nodeVersionInventory.GetSnapshotAsync(cancellationToken).ConfigureAwait(false)
                ?? ActiveNodeVersionSnapshot.NotCoordinated;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ActiveNodeVersionSnapshot.NotCoordinated;
        }
    }

    private bool WouldGateBlockWithoutApproval(
        IReadOnlyList<MigrationScriptClassification> classifications,
        bool journalIsNonEmpty)
        => _safetyOptions.ContractApplyPolicy == ContractApplyPolicy.Gate
            && journalIsNonEmpty
            && classifications.Any(c => c.Classification == MigrationSafetyClassification.ContractAnnotated);

    private static bool WouldBarrierBlockWithoutApproval(
        IReadOnlyList<MigrationScriptClassification> classifications,
        ActiveNodeVersionSnapshot snapshot)
        => snapshot.MixedVersionDetected && classifications.Any(c => c.IsBreaking);

    private enum ApprovalState
    {
        NotProvided,
        Fresh,
        AlreadyConsumed,
    }

    private sealed record ApprovalEvaluation(ApprovalState State, string? Nonce);

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

            // The contract-apply approval nonce, the live cluster version snapshot, and the barrier are
            // only relevant when a pending script narrows or drops schema. Skip all of it (no Redis call,
            // no ledger table) on the common additive (expand-only) upgrade path.
            var hasPendingContract = classifications.Any(c => c.IsBreaking);
            var approval = hasPendingContract
                ? await EvaluateApprovalAsync(lockConnection, cancellationToken).ConfigureAwait(false)
                : new ApprovalEvaluation(ApprovalState.NotProvided, null);
            var versionSnapshot = hasPendingContract
                ? await GetNodeVersionSnapshotAsync(cancellationToken).ConfigureAwait(false)
                : ActiveNodeVersionSnapshot.NotCoordinated;

            // 2. Journal-scoped contract-apply gate (#2565): block annotated contract scripts on an
            //    existing database unless the operator approved them.
            var gateRejection = TryBuildContractGateRejection(classifications, journalIsNonEmpty, approval);
            if (gateRejection is not null)
            {
                return DatabaseMigrationResult.Failed(gateRejection, gateRejection.Message);
            }

            // 3. Live node-version barrier (#2812): block contract-phase scripts while an older binary
            //    version is still serving traffic (mixed-version rolling upgrade), unless approved.
            var barrierRejection =
                TryBuildNodeVersionBarrierRejection(classifications, versionSnapshot, approval);
            if (barrierRejection is not null)
            {
                return DatabaseMigrationResult.Failed(barrierRejection, barrierRejection.Message);
            }

            // 4. Optional pre-migration backup hook: runs only when contract-class scripts are pending
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

            // Consume the one-shot approval nonce only when it was actually required to permit the apply
            // (the gate or the barrier would otherwise have blocked). Leaving the nonce unrecorded when
            // it was not needed keeps an unused value valid for the batch that genuinely needs it.
            var approvalWasRequired =
                WouldGateBlockWithoutApproval(classifications, journalIsNonEmpty)
                || WouldBarrierBlockWithoutApproval(classifications, versionSnapshot);
            if (approval is { State: ApprovalState.Fresh, Nonce: { } consumedNonce } && approvalWasRequired)
            {
                await RecordApprovalConsumedAsync(
                    lockConnection,
                    consumedNonce,
                    classifications.Where(c => c.IsBreaking).Select(c => c.ScriptName).ToArray(),
                    cancellationToken).ConfigureAwait(false);
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
        catch
        {
            // Best-effort cleanup; the process may have already exited.
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
        catch
        {
            // Backup-hook observability must not change migration apply/fail-closed semantics.
        }
    }
}
