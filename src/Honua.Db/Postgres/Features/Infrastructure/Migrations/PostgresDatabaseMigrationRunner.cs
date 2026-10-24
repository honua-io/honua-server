// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using DbUp;
using DbUp.Engine;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using Honua.Db.Postgres.Features.Attachments;
using DbUp.ScriptProviders;
using Honua.Db.Postgres.Features.Raster;

namespace Honua.Db.Postgres.Features.Infrastructure.Migrations;

internal sealed partial class PostgresDatabaseMigrationRunner : IDatabaseMigrationRunner
{
    internal const long MigrationLockKey = 8_044_282_257_919_950_151;
    private const string SafeMigrationFailureMessage =
        "Database migration planning failed; reconcile the migration journal and schema before retrying.";
    private const string BackupHookSucceededOutcome = "succeeded";
    private const string BackupHookFailedOutcome = "failed";
    private const int BackupHookStderrMaxLength = 500;

    /// <summary>
    /// Opt-in for databases whose migration-owned tables are created by a seed before the first
    /// migration run (test and certification fixtures, #4900). When set and the journal is empty,
    /// the pending migrations adopt complete seed-created tables instead of failing preflight.
    /// It never applies once any migration is journaled, and it is off by default so a production
    /// database that has lost its journal still fails closed rather than replaying every script.
    /// </summary>
    internal const string AdoptSeededSchemaOnFirstMigrationKey = "Database:AdoptSeededSchemaOnFirstMigration";
    private static readonly TimeSpan _migrationLockWaitTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _migrationLockRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _backupCommandTimeout = TimeSpan.FromHours(1);
    private static readonly IComparer<string> _migrationScriptNameComparer = new MigrationScriptNameComparer();
    private static readonly IReadOnlySet<string> _noReaderVerifiedMigrations = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Forward-compatibility declarations beside the DbUp journal (#6477).</summary>
    internal const string ReaderCompatibilityTable = PostgresReaderCompatibilityJournal.Table;

    private readonly MigrationSafetyOptions _safetyOptions;
    private readonly IDatabaseMigrationBackupHookRecorder? _backupHookRecorder;
    private readonly string? _contractApprovalToken;
    private readonly string _metadataSchemaVariable;
    private readonly string _metadataSchema;
    private readonly bool _includeConfiguredSchemaAdoption;
    private readonly bool _adoptSeededSchemaOnFirstMigration;
    private readonly IDatabaseSchemaGuard _schemaGuard;
    private readonly PostgresCoreSchemaMigrationManifest _migrations;

    public PostgresDatabaseMigrationRunner(
        IDatabaseSchemaGuard schemaGuard,
        PostgresCoreSchemaMigrationManifest migrations,
        IOptions<MigrationSafetyOptions>? safetyOptions = null,
        IConfiguration? configuration = null,
        IDatabaseMigrationBackupHookRecorder? backupHookRecorder = null)
    {
        _schemaGuard = schemaGuard ?? throw new ArgumentNullException(nameof(schemaGuard));
        _migrations = migrations ?? throw new ArgumentNullException(nameof(migrations));
        _safetyOptions = safetyOptions?.Value ?? new MigrationSafetyOptions();
        _backupHookRecorder = backupHookRecorder;
        var configuredMetadataSchema = configuration?["Database:Schema"];
        var metadataSchema = string.IsNullOrWhiteSpace(configuredMetadataSchema)
            ? PostgresSchemaConfiguration.DefaultMetadataSchema
            : configuredMetadataSchema.Trim();
        _metadataSchemaVariable = SchemaSearchPath.ValidateAndQuote(metadataSchema);
        _metadataSchema = metadataSchema;
        _includeConfiguredSchemaAdoption = !string.Equals(
            metadataSchema,
            PostgresSchemaConfiguration.DefaultMetadataSchema,
            StringComparison.Ordinal);

        _adoptSeededSchemaOnFirstMigration =
            bool.TryParse(configuration?[AdoptSeededSchemaOnFirstMigrationKey], out var adoptSeededSchema) && adoptSeededSchema;

        // The contract-apply approval is an explicit top-level operator signal
        // (HONUA_APPROVE_CONTRACT_MIGRATIONS=<nonce>), read via the IConfiguration indexer (Abstractions
        // only, no Binder dependency). It intentionally lives outside the bound options record so an
        // operator can grant one-shot approval for a single upgrade without editing config files. The raw
        // token is validated against the pending-script nonce at apply time (#2812), so a stale value can
        // never approve a later, unrelated contract migration.
        var rawApproval = configuration?[MigrationSafetyOptions.ApproveContractMigrationsKey];
        _contractApprovalToken = string.IsNullOrWhiteSpace(rawApproval) ? null : rawApproval.Trim();
    }

    public async Task<DatabaseMigrationPlan> PlanMigrationsAsync(
        string connectionString,
        Assembly migrationsAssembly,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ValidateArguments(connectionString, migrationsAssembly);
            cancellationToken.ThrowIfCancellationRequested();

            var usesCanonicalMigrationRoots = UsesCanonicalMigrationRoots(migrationsAssembly);
            var includeRasterProviderMigrations = false;
            await using var connection = usesCanonicalMigrationRoots ? new NpgsqlConnection(connectionString) : null;
            if (connection is not null)
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                includeRasterProviderMigrations = await HasPostGisRasterAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
            }

            var attachmentSchema = connection is null ? null : await PostgresAttachmentSchema.ResolveAsync(
                connection, _metadataSchema, _migrations.Attachments!, allowPendingCreation: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var upgrader = BuildUpgrader(
                BuildMigrationConnectionString(connectionString),
                migrationsAssembly,
                _metadataSchemaVariable,
                includeRasterProviderMigrations,
                _includeConfiguredSchemaAdoption,
                _migrations.ConfiguredSchemaAdoptionMigration,
                attachmentSchema, _migrations.Attachments);
            var journalIsNonEmpty = JournalIsNonEmpty(upgrader);
            var readerVerified = await VerifyReaderCompatibilityAsync(
                upgrader, migrationsAssembly, usesCanonicalMigrationRoots, connection,
                BuildMigrationConnectionString(connectionString), cancellationToken).ConfigureAwait(false);
            if (connection is not null)
            {
                await VerifyPreMigrationConsistencyAsync(connection, journalIsNonEmpty, readerVerified, cancellationToken)
                    .ConfigureAwait(false);
            }

            var scripts = upgrader.GetScriptsToExecute();
            var pendingScripts = scripts.Select(script => script.Name).ToArray();
            var classifications = ClassifyScripts(scripts);
            var executedButNotDiscoveredScripts = upgrader.GetExecutedButNotDiscoveredScripts().ToArray();
            var bindings = attachmentSchema is null ? Array.Empty<DatabaseMigrationScriptBinding>() :
                AttachmentSchemaScriptProvider.Describe(migrationsAssembly, _metadataSchema, attachmentSchema, _migrations.Attachments!);

            return DatabaseMigrationPlan.Succeeded(
                pendingScripts,
                executedButNotDiscoveredScripts,
                classifications,
                journalIsNonEmpty) with
            {
                ScriptBindings = bindings,
                ContractApprovalNonce = ComputeApprovalNonce(classifications, bindings)
            };
        }
        catch (DatabaseSchemaCompatibilityException ex)
        {
            return DatabaseMigrationPlan.Failed(ex, ex.Message);
        }
        // Intentionally broad: map any planning failure (bad connection string, DbUp/journal errors,
        // etc.) to the typed DatabaseMigrationPlan.Failed result with a sanitized message; the raw
        // exception is carried on the result for the caller to log without leaking it to end users.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return DatabaseMigrationPlan.Failed(ex, SafeMigrationFailureMessage);
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
            .ToArray();

        if (unannotated.Length == 0)
        {
            return null;
        }

        var details = unannotated.Select(
            classification =>
                $"{classification.ScriptName} [{string.Join(", ", classification.BreakingRules)}]");

        return new InvalidOperationException(
            "Refusing to apply potentially backward-incompatible (contract-phase) migration(s) that are " +
            $"not declared rollout-safe: {string.Join(", ", details)}. Each must carry the marker " +
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
        bool journalIsNonEmpty,
        IReadOnlyList<DatabaseMigrationScriptBinding> bindings)
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
        var expectedNonce = ComputeApprovalNonce(classifications, bindings);
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
            "token bound to these exact scripts that will not approve any later contract migration). To apply " +
            "them out of band, run the target image once with HONUA_MIGRATE_ONLY=true and the same approval " +
            "before the serving replicas start with HONUA_SKIP_MIGRATIONS=true; migrate-only applies the same gates.");
    }

    internal static string? ComputeApprovalNonce(
        IReadOnlyList<MigrationScriptClassification> classifications,
        IReadOnlyList<DatabaseMigrationScriptBinding> bindings)
    {
        var pending = classifications
            .Where(c => c.Classification == MigrationSafetyClassification.ContractAnnotated)
            .Select(c => c.ScriptName).ToArray();
        if (pending.Length == 0)
        {
            return null;
        }

        // Existing default-schema and synthetic approvals retain their established
        // identity. A custom attachment projection additionally binds the reviewed
        // physical authority and exact resource/rendering bytes.
        var custom = bindings.Where(binding => binding.ConfiguredSchema != "honua")
            .OrderBy(binding => binding.ScriptName, StringComparer.Ordinal).ToArray();
        var bindingContext = string.Join("\n", custom.Select(binding => string.Join("\n",
            binding.ScriptName, binding.ConfiguredSchema, binding.PhysicalSchema,
            binding.AttachmentsTable, binding.ImportCleanupTable,
            binding.ResourceSha256, binding.RenderedSqlSha256) +
            (binding.PreambleResourceName is null && binding.PreambleResourceSha256 is null
                ? string.Empty
                : string.Join("\n", string.Empty, binding.PreambleResourceName, binding.PreambleResourceSha256))));
        return custom.Length == 0
            ? MigrationSafetyClassifier.ComputeContractApprovalNonce(pending)
            : MigrationSafetyClassifier.ComputeContractApprovalNonce(pending.Concat(
                ["attachment-binding:" + AttachmentSchemaScriptProvider.SqlDigest(bindingContext)]));
    }

    /// <summary>
    /// DbUp reports the migration journal via the same <see cref="UpgradeEngine"/> the runner already
    /// uses: a non-empty executed-scripts list means the database has been migrated before (an upgrade),
    /// while an empty list is a fresh install. On a fresh database the journal table does not yet exist
    /// and DbUp returns an empty list rather than throwing.
    /// </summary>
    private static bool JournalIsNonEmpty(UpgradeEngine upgrader)
        => upgrader.GetExecutedScripts().Count > 0;

    /// <summary>
    /// Preflight journal/physical-schema reconciliation. On a database that has never been migrated,
    /// with <see cref="AdoptSeededSchemaOnFirstMigrationKey"/> set, seed-created migration-owned tables
    /// are accepted as adoption candidates for the pending migrations (#4900); the post-upgrade
    /// <see cref="IDatabaseSchemaGuard.VerifyAsync(System.Data.Common.DbConnection, CancellationToken)"/>
    /// still enforces the complete floor. Every other case keeps the fail-closed consistency check.
    /// </summary>
    private Task VerifyPreMigrationConsistencyAsync(
        NpgsqlConnection connection,
        bool journalIsNonEmpty,
        IReadOnlySet<string> readerVerified,
        CancellationToken cancellationToken)
        => _adoptSeededSchemaOnFirstMigration && !journalIsNonEmpty
            ? _schemaGuard.VerifyFirstMigrationAdoptionAsync(connection, cancellationToken)
            : readerVerified.Count == 0
                ? _schemaGuard.VerifyConsistencyAsync(connection, cancellationToken)
                : _schemaGuard.VerifyConsistencyAsync(connection, readerVerified, cancellationToken);

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

        try
        {
            var usesCanonicalMigrationRoots = UsesCanonicalMigrationRoots(migrationsAssembly);
            var includeRasterProviderMigrations = usesCanonicalMigrationRoots &&
                await HasPostGisRasterAsync(lockConnection, cancellationToken).ConfigureAwait(false);
            var attachmentSchema = usesCanonicalMigrationRoots ? await PostgresAttachmentSchema.ResolveAsync(
                lockConnection, _metadataSchema, _migrations.Attachments!, allowPendingCreation: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false) : null;
            var upgrader = BuildUpgrader(
                migrationConnectionString,
                migrationsAssembly,
                _metadataSchemaVariable,
                includeRasterProviderMigrations,
                _includeConfiguredSchemaAdoption,
                _migrations.ConfiguredSchemaAdoptionMigration,
                attachmentSchema, _migrations.Attachments);
            var journalIsNonEmpty = JournalIsNonEmpty(upgrader);
            var readerVerified = await VerifyReaderCompatibilityAsync(
                upgrader, migrationsAssembly, usesCanonicalMigrationRoots, lockConnection,
                migrationConnectionString, cancellationToken).ConfigureAwait(false);
            if (usesCanonicalMigrationRoots)
            {
                await VerifyPreMigrationConsistencyAsync(lockConnection, journalIsNonEmpty, readerVerified, cancellationToken)
                    .ConfigureAwait(false);
            }

            var classifications = ClassifyScripts(upgrader.GetScriptsToExecute());
            var bindings = attachmentSchema is null ? Array.Empty<DatabaseMigrationScriptBinding>() :
                AttachmentSchemaScriptProvider.Describe(migrationsAssembly, _metadataSchema, attachmentSchema, _migrations.Attachments!);
            var migrationRunId = CreateMigrationRunId();

            // 1. ADR-0060 fail-closed: reject unannotated contract scripts (default Enforce=true).
            if (TryBuildSafetyRejection(classifications) is { } rejection)
            {
                return DatabaseMigrationResult.Failed(rejection, rejection.Message);
            }

            // 2. Journal-scoped contract-apply gate (#2565): block annotated contract scripts on an
            //    existing database unless the operator approved them.
            if (TryBuildContractGateRejection(classifications, journalIsNonEmpty, bindings) is { } gateRejection)
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

            // A backup hook can take time or execute operator-owned work. Re-read
            // the physical authority while still holding the canonical migration
            // lock; never execute a rendered plan against a changed binding.
            if (attachmentSchema is not null)
            {
                var currentAttachmentSchema = await PostgresAttachmentSchema.ResolveAsync(
                    lockConnection, _metadataSchema, _migrations.Attachments!, allowPendingCreation: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var currentBindings = AttachmentSchemaScriptProvider.Describe(
                    migrationsAssembly, _metadataSchema, currentAttachmentSchema, _migrations.Attachments!);
                if (!bindings.SequenceEqual(currentBindings))
                {
                    var error = new InvalidOperationException("Attachment physical binding changed after migration planning; re-plan before applying.");
                    return DatabaseMigrationResult.Failed(error, error.Message);
                }
            }

            var result = upgrader.PerformUpgrade();
            var appliedScripts = result.Scripts.Select(script => script.Name).ToArray();

            if (!result.Successful)
            {
                var error = result.Error ?? new InvalidOperationException("Database migration failed.");
                return DatabaseMigrationResult.Failed(error, SafeMigrationFailureMessage, appliedScripts);
            }

            // Record, beside each journal entry this image ships, the forward-compatibility
            // declaration an older rollback reader re-verifies before accepting it (#6477).
            await RecordReaderCompatibilityDeclarationsAsync(lockConnection, upgrader, cancellationToken)
                .ConfigureAwait(false);

            // A successful DbUp result is not sufficient evidence by itself: guarded migrations
            // may contain conditional/idempotent SQL. Reconcile the journal with required physical
            // objects/effects before reporting readiness or an upgrade receipt.
            if (usesCanonicalMigrationRoots)
            {
                await (readerVerified.Count == 0
                    ? _schemaGuard.VerifyAsync(lockConnection, cancellationToken)
                    : _schemaGuard.VerifyAsync(lockConnection, readerVerified, cancellationToken)).ConfigureAwait(false);
            }

            return DatabaseMigrationResult.Succeeded(appliedScripts);
        }
        catch (Exception ex) when (ex is DatabaseSchemaFloorException or DatabaseSchemaCompatibilityException or AttachmentSchemaBindingException)
        {
            return DatabaseMigrationResult.Failed(ex, ex.Message);
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

    /// <summary>
    /// Refuses a journal this reader cannot qualify. A journal entry this image does not ship is
    /// accepted only through a forward-compatibility declaration the applying server recorded and this
    /// reader re-verifies from the recorded, hash-bound script text with its own classifier (#6477).
    /// Entries with no declaration keep the original refusal. Returns the accepted unknown entries.
    /// </summary>
    private static async Task<IReadOnlySet<string>> VerifyReaderCompatibilityAsync(
        UpgradeEngine upgrader,
        Assembly migrationsAssembly,
        bool canonical,
        NpgsqlConnection? openConnection,
        string connectionString,
        CancellationToken cancellationToken)
    {
        // Include every known script, even conditionally omitted raster/adoption scripts. A
        // journal entry for a known-but-disabled script is not evidence of a newer reader.
        var known = migrationsAssembly.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
        if (canonical)
        {
            known.UnionWith(typeof(PostgresDatabaseMigrationRunner).Assembly.GetManifestResourceNames());
        }

        var unknown = upgrader.GetExecutedScripts().Where(name => !known.Contains(name)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length == 0)
        {
            return _noReaderVerifiedMigrations;
        }

        // The reader's own schema number is its highest application migration; provider-root
        // numbering is independent and never raises it.
        var readerSchema = MigrationReaderCompatibility.ReaderSchemaOf(
            migrationsAssembly.GetManifestResourceNames().Where(IsSqlScript));
        if (openConnection is not null)
        {
            return await PostgresReaderCompatibilityJournal.QualifyAsync(
                openConnection, unknown, readerSchema, cancellationToken).ConfigureAwait(false);
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await PostgresReaderCompatibilityJournal.QualifyAsync(
            connection, unknown, readerSchema, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSqlScript(string name) => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Records a forward-compatibility declaration for every journaled script this image ships and
    /// that has none yet (#6477). Runs under the migration lock after a successful upgrade; it is a
    /// no-op until the declaration table exists (the expand migration that creates it). Existing
    /// declarations are never rewritten: a reader re-verifies each one from its recorded text anyway.
    /// </summary>
    internal static async Task RecordReaderCompatibilityDeclarationsAsync(
        NpgsqlConnection connection,
        UpgradeEngine upgrader,
        CancellationToken cancellationToken)
    {
        if (!await PostgresReaderCompatibilityJournal.TableExistsAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var executed = upgrader.GetExecutedScripts().ToHashSet(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        await using (var existing = new NpgsqlCommand($"SELECT script_name FROM {ReaderCompatibilityTable}", connection))
        await using (var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                declared.Add(reader.GetString(0));
            }
        }

        var pending = upgrader.GetDiscoveredScripts()
            .Where(script => executed.Contains(script.Name) && !declared.Contains(script.Name))
            .Select(script => MigrationReaderCompatibility.Declare(script.Name, script.Contents))
            .ToArray();
        await RecordReaderCompatibilityDeclarationsAsync(connection, pending, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Inserts declarations, leaving any existing declaration for the same script untouched.</summary>
    internal static async Task RecordReaderCompatibilityDeclarationsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<MigrationReaderCompatibilityDeclaration> declarations,
        CancellationToken cancellationToken)
    {
        if (declarations.Count == 0)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var declaration in declarations)
        {
            await using var insert = new NpgsqlCommand(
                $"""
                INSERT INTO {ReaderCompatibilityTable}
                    (script_name, declaration_format, classification, script_sha256, min_reader_schema, script_text)
                VALUES (@name, @format, @classification, @sha256, @min_reader_schema, @text)
                ON CONFLICT (script_name) DO NOTHING
                """,
                connection,
                transaction);
            _ = insert.Parameters.AddWithValue("name", declaration.ScriptName);
            _ = insert.Parameters.AddWithValue("format", declaration.DeclarationFormat);
            _ = insert.Parameters.AddWithValue("classification", declaration.Classification);
            _ = insert.Parameters.AddWithValue("sha256", declaration.ScriptSha256);
            _ = insert.Parameters.AddWithValue("min_reader_schema", declaration.MinReaderSchema);
            _ = insert.Parameters.AddWithValue("text", declaration.ScriptText);
            _ = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static UpgradeEngine BuildUpgrader(
        string connectionString,
        Assembly migrationsAssembly,
        string metadataSchemaVariable,
        bool includeRasterProviderMigrations,
        bool includeConfiguredSchemaAdoption,
        string configuredSchemaAdoptionMigration,
        string? attachmentSchema = null,
        PostgresAttachmentMigrationContract? attachmentContract = null)
    {
        var builder = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .JournalToPostgresqlTable("public", "schema_versions")
            .WithVariable("HonuaSchema", metadataSchemaVariable)
            .WithTransaction()
            .LogToConsole();
        foreach (var (name, body) in RasterSamplingSql.MigrationVariables())
            builder = builder.WithVariable(name, body);

        // Production has two canonical numbered roots: provider schema and server schema.
        // Synthetic migration assemblies used by the safety-gate tests remain intentionally
        // isolated and must not pull PostGIS migrations into their plain-PostgreSQL fixture.
        var migrationAssemblies = includeRasterProviderMigrations
            ? new[] { typeof(PostgresDatabaseMigrationRunner).Assembly, migrationsAssembly }
            : new[] { migrationsAssembly };

        foreach (var assembly in migrationAssemblies)
        {
            // Migration 109 is a contract-phase move that only has meaning for a
            // non-default Database:Schema. Omitting it at discovery time keeps an
            // existing default-schema upgrade out of the nonce gate altogether.
            var provider = new EmbeddedScriptProvider(assembly,
                name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) &&
                    (includeConfiguredSchemaAdoption ||
                     !string.Equals(name, configuredSchemaAdoptionMigration, StringComparison.Ordinal)));
            IScriptProvider selectedProvider = attachmentSchema is null ? provider :
                new AttachmentSchemaScriptProvider(provider, attachmentSchema, migrationsAssembly,
                    attachmentContract ?? throw new InvalidOperationException("Canonical attachment identities were not supplied."));
            builder.WithScripts(selectedProvider);
        }

        return builder
            .WithScriptNameComparer(_migrationScriptNameComparer)
            .Build();
    }

    private bool UsesCanonicalMigrationRoots(Assembly migrationsAssembly)
        => string.Equals(
            migrationsAssembly.GetName().Name,
            _migrations.ApplicationMigrationAssemblyName,
            StringComparison.Ordinal);

    private static async Task<bool> HasPostGisRasterAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_extension
                WHERE extname = 'postgis_raster')
            """;
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    private sealed partial class MigrationScriptNameComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var numberComparison = ReadMigrationNumber(x).CompareTo(ReadMigrationNumber(y));
            if (numberComparison != 0)
            {
                return numberComparison;
            }

            // At the same number, establish the core metadata schema before provider objects
            // that reference it (notably Honua.Postgres migration 001 -> honua.layers).
            var rootComparison = ReadRootOrder(x).CompareTo(ReadRootOrder(y));
            return rootComparison != 0 ? rootComparison : StringComparer.Ordinal.Compare(x, y);
        }

        private static int ReadMigrationNumber(string name)
        {
            var match = MigrationNumberPattern().Match(name);
            return match.Success && int.TryParse(
                match.Groups[1].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number)
                ? number
                : int.MaxValue;
        }

        private static int ReadRootOrder(string name)
            => name.Contains(".Server.Migrations.", StringComparison.Ordinal) ? 0
                : name.Contains(".Postgres.Migrations.", StringComparison.Ordinal) ? 1
                : 0;

        [GeneratedRegex(@"(?:^|\.)0*(\d+)_", RegexOptions.CultureInvariant)]
        private static partial Regex MigrationNumberPattern();
    }

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
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            Debug.WriteLine(caughtException);
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
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            Debug.WriteLine(caughtException);
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
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            Debug.WriteLine(caughtException);
        }
    }
}
