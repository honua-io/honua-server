// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Infrastructure.Migrations;
using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Core.Features.Security.Abstractions;
using Honua.ControlPlane;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace Honua.Server.Hosting.MigrateOnly;

/// <summary>
/// Applies database migrations out of band and exits (honua-server#6470). It is the supported
/// counterpart of serving with <c>HONUA_SKIP_MIGRATIONS=true</c>: a Kubernetes pre-upgrade Job, an
/// ECS one-shot task or a CI step runs the server image with <c>HONUA_MIGRATE_ONLY=true</c>, and the
/// serving replicas then start against a current journal.
/// </summary>
/// <remarks>
/// The run resolves <c>ConnectionStrings:DefaultConnection</c> through the same secret resolver chain
/// as serving startup, runs the same PostGIS preflight and calls the same
/// <see cref="IDatabaseMigrationRunner"/>, so every <c>Database:MigrationSafety:*</c> gate applies
/// unchanged: the unannotated-contract refusal (<c>Enforce</c>), the contract-apply gate and its
/// one-shot <c>HONUA_APPROVE_CONTRACT_MIGRATIONS</c> nonce, and the pre-migration backup hook. It
/// writes exactly one JSON summary line (<see cref="SummaryFormat"/>) to standard output; logs go to
/// standard error. The exit code is <see cref="MigrateOnlyOutcome.ExitCode"/>.
/// </remarks>
internal static class MigrateOnlyCommand
{
    /// <summary>Format identifier of the standard-output summary.</summary>
    internal const string SummaryFormat = "honua.migrate-only/v1";

    private const string ProviderName = "postgres";

    /// <summary>
    /// The process entry point for migrate-only mode. Standard output is reserved for the summary:
    /// <see cref="Console.Out"/> (which DbUp's upgrade log writes to) is redirected to standard error
    /// for the run. SIGTERM and SIGINT stop the run at its next database step instead of killing the
    /// process mid-script, and an unexpected failure still produces a summary and a typed exit code.
    /// </summary>
    /// <param name="configuration">The process configuration.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> RunFromEntryPointAsync(IConfiguration configuration)
    {
        var summaryWriter = Console.Out;
        Console.SetOut(Console.Error);
        using var cancellation = new CancellationTokenSource();
        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });
        using var sigint = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGINT,
            context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });

        try
        {
            return await RunAsync(configuration, summaryWriter, cancellationToken: cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Console.Error.WriteLineAsync("Migrate-only run failed before it could report: " + ex.GetType().Name)
                .ConfigureAwait(false);
            var summary = new MigrateOnlySummary { MetadataSchema = ResolveMetadataSchema(configuration) };
            summary.Finish(MigrateOnlyOutcome.MigrationFailed, "Migrate-only run failed (" + ex.GetType().Name + ").");
            await summaryWriter.WriteLineAsync(summary.ToJson()).ConfigureAwait(false);
            await summaryWriter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            return MigrateOnlyOutcome.MigrationFailed.ExitCode;
        }
        finally
        {
            Console.SetOut(summaryWriter);
        }
    }

    /// <summary>Builds the canonical migration composition and runs it without serving.</summary>
    internal static async Task<int> RunAsync(
        IConfiguration configuration,
        TextWriter summaryWriter,
        Func<string, string?>? environment = null,
        Assembly? migrationsAssembly = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(summaryWriter);
        await using var services = MigrateOnlyComposition.Build(configuration);
        return await RunWithServicesAsync(configuration, summaryWriter, services, environment, migrationsAssembly,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the migrate-only flow and returns the process exit code.
    /// </summary>
    /// <param name="configuration">The process configuration (environment, command line, settings document).</param>
    /// <param name="summaryWriter">Receives the single JSON summary line.</param>
    /// <param name="environment">Environment variable accessor, for host detection.</param>
    /// <param name="migrationsAssembly">The assembly whose embedded scripts are applied; the server's by default.</param>
    /// <param name="cancellationToken">Stops the run before the next database step.</param>
    /// <param name="services">Caller-owned migration services for controlled composition.</param>
    /// <returns>The exit code of the resolved <see cref="MigrateOnlyOutcome"/>.</returns>
    internal static async Task<int> RunWithServicesAsync(
        IConfiguration configuration,
        TextWriter summaryWriter,
        IServiceProvider services,
        Func<string, string?>? environment = null,
        Assembly? migrationsAssembly = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(summaryWriter);
        environment ??= Environment.GetEnvironmentVariable;
        migrationsAssembly ??= typeof(MigrateOnlyCommand).Assembly;

        var summary = new MigrateOnlySummary
        {
            MetadataSchema = ResolveMetadataSchema(configuration),
            BinarySchemaNumber = ResolveSchemaNumber(migrationsAssembly),
            StartedAt = DateTimeOffset.UtcNow,
            RunId = Guid.NewGuid().ToString("D"),
        };

        ArgumentNullException.ThrowIfNull(services);
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Honua.MigrateOnly");
        MigrateOnlyLog.Starting(logger, summary.BinarySchemaNumber);

        try
        {
            await RunCoreAsync(configuration, environment, migrationsAssembly, services, summary, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            summary.Finish(MigrateOnlyOutcome.Cancelled, "The run was stopped before it finished.");
        }

        summary.BackupHooks = services.GetRequiredService<MigrateOnlyBackupHookRecorder>().Results;
        if (summary.Outcome == MigrateOnlyOutcome.Completed)
        {
            MigrateOnlyLog.Completed(logger, summary.AppliedScripts.Count, summary.SchemaNumber);
        }
        else
        {
            MigrateOnlyLog.Stopped(logger, summary.Outcome.Code, summary.Outcome.ExitCode, summary.Message ?? summary.Outcome.Code);
        }

        await summaryWriter.WriteLineAsync(summary.ToJson()).ConfigureAwait(false);
        await summaryWriter.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return summary.Outcome.ExitCode;
    }

    private static async Task RunCoreAsync(
        IConfiguration configuration,
        Func<string, string?> environment,
        Assembly migrationsAssembly,
        IServiceProvider services,
        MigrateOnlySummary summary,
        CancellationToken cancellationToken)
    {
        if (MigrateOnlyMode.Resolve(configuration) == MigrateOnlyRequest.Invalid)
        {
            summary.Finish(MigrateOnlyOutcome.InvalidConfiguration,
                $"{MigrateOnlyMode.SwitchKey} must be 'true' or 'false'.");
            return;
        }

        if (MigrateOnlyMode.IsAwsLambda(environment))
        {
            summary.Finish(MigrateOnlyOutcome.InvalidConfiguration,
                $"{MigrateOnlyMode.SwitchKey}=true is not supported inside AWS Lambda: run the image as a container task or Job instead.");
            return;
        }

        if (ExecutionWorkerLaunchEnvironment.ReservedNames.FirstOrDefault(
            name => !string.IsNullOrWhiteSpace(configuration[name])) is { } workerSetting)
        {
            summary.Finish(MigrateOnlyOutcome.InvalidConfiguration,
                $"{MigrateOnlyMode.SwitchKey}=true cannot be combined with an execution-worker launch ({workerSetting}).");
            return;
        }

        var requestedRunId = configuration[MigrateOnlyEvidence.RunIdKey];
        if (!string.IsNullOrWhiteSpace(requestedRunId))
        {
            if (!Guid.TryParseExact(requestedRunId, "D", out var parsedRunId))
            {
                summary.Finish(MigrateOnlyOutcome.InvalidConfiguration, $"{MigrateOnlyEvidence.RunIdKey} must be a UUID.");
                return;
            }
            summary.RunId = parsedRunId.ToString("D");
        }
        cancellationToken.ThrowIfCancellationRequested();

        var provider = DataProviderNames.Normalize(configuration["DataSource:Provider"]);
        if (provider is not (DataProviderNames.Postgis or DataProviderNames.PostgreSql))
        {
            summary.Finish(MigrateOnlyOutcome.InvalidConfiguration,
                $"DataSource:Provider '{provider}' owns no migratable schema; migrate-only mode applies PostgreSQL migrations only.");
            return;
        }

        string? connectionString;
        try
        {
            connectionString = await ConnectionStringResolutionHelper.ResolveDefaultConnectionStringAsync(
                configuration,
                services.GetRequiredService<IConnectionSecretResolver>(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            summary.Finish(MigrateOnlyOutcome.InvalidConfiguration,
                "ConnectionStrings:DefaultConnection could not be resolved (" + ex.GetType().Name + ").");
            return;
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            summary.Finish(MigrateOnlyOutcome.InvalidConfiguration, "ConnectionStrings:DefaultConnection is not configured.");
            return;
        }

        try
        {
            summary.TargetLocatorSha256 = MigrateOnlyEvidence.ComputeTargetLocatorHash(connectionString, summary.MetadataSchema);
        }
        catch (ArgumentException)
        {
            summary.Finish(MigrateOnlyOutcome.InvalidConfiguration, "The database requires an explicit single-host connection locator.");
            return;
        }
        catch (InvalidOperationException)
        {
            summary.Finish(MigrateOnlyOutcome.InvalidConfiguration, "The database requires an explicit single-host connection locator.");
            return;
        }

        // 1. PostGIS preflight, with the same bounded transient retry as serving startup.
        var checker = services.GetRequiredService<IDatabaseCompatibilityChecker>();
        DatabaseCompatibilityResult compatibility;
        try
        {
            compatibility = await StartupDatabasePreflight.CheckWithBoundedRetryAsync(
                checker,
                connectionString,
                StartupDatabasePreflight.TransientRetryDelays,
                static (delay, token) => Task.Delay(delay, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            summary.Finish(Classify(ex, plan: null, safety: null), DescribeFailure("PostGIS preflight", ex));
            return;
        }

        if (compatibility.IsTransientFailure)
        {
            summary.Finish(MigrateOnlyOutcome.DatabaseUnavailable,
                "PostGIS preflight: " + StartupDatabasePreflight.DescribeCause(compatibility));
            return;
        }

        if (!compatibility.IsCompatible)
        {
            summary.Finish(MigrateOnlyOutcome.DatabaseIncompatible,
                "PostGIS preflight failed: the database is not compatible.");
            return;
        }

        // 2. Plan: names and classifies the pending scripts and, when they are gated, the exact
        //    approval nonce. The run below re-plans under the migration lock and is authoritative.
        var runner = services.GetRequiredService<IDatabaseMigrationRunner>();
        var safety = services.GetRequiredService<IOptions<MigrationSafetyOptions>>().Value;
        var plan = await runner.PlanMigrationsAsync(connectionString, migrationsAssembly, cancellationToken).ConfigureAwait(false);
        summary.Plan = plan;
        if (!plan.Successful)
        {
            var planError = plan.Error ?? new InvalidOperationException(plan.ErrorMessage ?? "Migration planning failed.");
            summary.Finish(Classify(planError, plan, safety), DescribeFailure("migration planning", planError, plan.ErrorMessage));
            return;
        }

        // 3. Apply through the runner, which enforces every MigrationSafety gate before any script.
        DatabaseMigrationResult result;
        try
        {
            result = await runner.RunMigrationsAsync(connectionString, migrationsAssembly, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            result = DatabaseMigrationResult.Failed(ex);
        }

        summary.AppliedScripts = result.AppliedScripts;
        if (!result.Successful)
        {
            var error = result.Error ?? new InvalidOperationException(result.ErrorMessage ?? "Database migration failed.");
            summary.Finish(Classify(error, plan, safety), DescribeFailure("migration", error, result.ErrorMessage));
            return;
        }

        // The runner verifies under its migration lock. Require the shared guard again so a
        // completion summary explicitly attests verification even with a replaced composition.
        try
        {
            await services.GetRequiredService<IDatabaseSchemaGuard>().VerifyAsync(connectionString, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            summary.Finish(Classify(ex, plan, safety), DescribeFailure("schema verification", ex));
            return;
        }
        summary.PackagedMigrationManifestSha256 = MigrateOnlyEvidence.ComputePackagedManifestHash(migrationsAssembly);
        summary.SchemaVerified = true;
        summary.SchemaNumber = summary.BinarySchemaNumber;
        summary.Finish(MigrateOnlyOutcome.Completed, null);
    }

    /// <summary>
    /// Maps a planning or migration failure to its typed outcome. The runner reports gate refusals
    /// as <see cref="InvalidOperationException"/>s, so the gate is recognised by the stable refusal
    /// prefix it shares with <see cref="GatedBootShutdown"/>, and the unannotated-contract and
    /// backup-hook refusals by the plan and the setting that produced them.
    /// </summary>
    internal static MigrateOnlyOutcome Classify(Exception error, DatabaseMigrationPlan? plan, MigrationSafetyOptions? safety)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (error is OperationCanceledException)
        {
            return MigrateOnlyOutcome.Cancelled;
        }

        if (error is DatabaseSchemaCompatibilityException)
        {
            return MigrateOnlyOutcome.SchemaNewerThanBinary;
        }

        if (GatedBootShutdown.IsContractGateRefusal(error))
        {
            return MigrateOnlyOutcome.ContractApprovalRequired;
        }

        if (error is InvalidOperationException &&
            plan is { HasUnannotatedBreakingScripts: true } &&
            safety is { Enforce: true } &&
            error.Message.Contains(MigrationSafetyClassifier.CompatibilityReviewMarkerConvention, StringComparison.Ordinal))
        {
            return MigrateOnlyOutcome.UnannotatedContractMigration;
        }

        if (error is InvalidOperationException &&
            !string.IsNullOrWhiteSpace(safety?.BackupCommand) &&
            error.Message.Contains("(Database:MigrationSafety:BackupCommand)", StringComparison.Ordinal))
        {
            return MigrateOnlyOutcome.BackupFailed;
        }

        if (error is TimeoutException && error.Message.Contains("migration lock", StringComparison.Ordinal))
        {
            return MigrateOnlyOutcome.MigrationLockTimeout;
        }

        if (StartupDatabaseResilience.IsTransientConnectivityError(error))
        {
            return MigrateOnlyOutcome.DatabaseUnavailable;
        }

        return MigrateOnlyOutcome.MigrationFailed;
    }

    // Gate refusals and schema-version refusals carry operator-facing text (script names, the
    // nonce, the setting to change) and no connection details, so they are reported verbatim.
    // Anything else is reduced to its failure class: provider messages can name hosts.
    private static string DescribeFailure(string phase, Exception error, string? runnerMessage = null)
    {
        if (error is DatabaseSchemaCompatibilityException ||
            GatedBootShutdown.IsContractGateRefusal(error) ||
            (error is InvalidOperationException && error.Message.Contains("Database:MigrationSafety", StringComparison.Ordinal)) ||
            (error is TimeoutException && error.Message.Contains("migration lock", StringComparison.Ordinal)))
        {
            return error.Message;
        }

        if (StartupDatabaseResilience.IsTransientConnectivityError(error))
        {
            return phase + ": " + StartupDatabaseResilience.DescribeFailureClass(error);
        }

        return phase + " failed (" + error.GetType().Name + ")" +
            (string.IsNullOrWhiteSpace(runnerMessage) || runnerMessage == error.Message ? "." : ": " + runnerMessage);
    }

    private static string ResolveMetadataSchema(IConfiguration configuration)
    {
        var configured = configuration["Database:Schema"];
        return string.IsNullOrWhiteSpace(configured) ? "honua" : configured.Trim();
    }

    /// <summary>
    /// The highest leading script number among <paramref name="migrationsAssembly"/>'s embedded
    /// <c>.sql</c> scripts: the schema number a current journal has for this binary.
    /// </summary>
    internal static int? ResolveSchemaNumber(Assembly migrationsAssembly)
    {
        int? highest = null;
        foreach (var resource in migrationsAssembly.GetManifestResourceNames())
        {
            if (!resource.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var withoutExtension = resource[..^4];
            var scriptName = withoutExtension[(withoutExtension.LastIndexOf('.') + 1)..];
            var digits = 0;
            while (digits < scriptName.Length && char.IsAsciiDigit(scriptName[digits]))
            {
                digits++;
            }

            if (digits > 0 &&
                int.TryParse(scriptName.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var number) &&
                (highest is null || number > highest))
            {
                highest = number;
            }
        }

        return highest;
    }

    /// <summary>The run's accumulated result, rendered as the standard-output summary.</summary>
    private sealed class MigrateOnlySummary
    {
        public MigrateOnlyOutcome Outcome { get; private set; } = MigrateOnlyOutcome.MigrationFailed;

        public string? Message { get; private set; }

        public string RunId { get; set; } = Guid.NewGuid().ToString("D");
        public DateTimeOffset? StartedAt { get; init; } = DateTimeOffset.UtcNow;
        public string? TargetLocatorSha256 { get; set; }
        public string? PackagedMigrationManifestSha256 { get; set; }

        public required string MetadataSchema { get; init; }

        public int? BinarySchemaNumber { get; init; }

        public int? SchemaNumber { get; set; }

        public bool SchemaVerified { get; set; }

        public DatabaseMigrationPlan? Plan { get; set; }

        public IReadOnlyList<string> AppliedScripts { get; set; } = Array.Empty<string>();

        public IReadOnlyList<DatabaseMigrationBackupHookResult> BackupHooks { get; set; } = Array.Empty<DatabaseMigrationBackupHookResult>();

        public void Finish(MigrateOnlyOutcome outcome, string? message)
        {
            Outcome = outcome;
            Message = message;
        }

        public string ToJson()
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("format", SummaryFormat);
                writer.WriteString("runId", RunId);
                if (StartedAt is { } startedAt) writer.WriteString("startedAt", startedAt);
                writer.WriteString("completedAt", DateTimeOffset.UtcNow);
                if (TargetLocatorSha256 is not null) writer.WriteString("targetLocatorSha256", TargetLocatorSha256);
                if (PackagedMigrationManifestSha256 is not null) writer.WriteString("packagedMigrationManifestSha256", PackagedMigrationManifestSha256);
                writer.WriteString("serverVersion", typeof(MigrateOnlyCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(MigrateOnlyCommand).Assembly.GetName().Version?.ToString() ?? "unknown");
                writer.WriteString("outcome", Outcome.Outcome);
                writer.WriteString("code", Outcome.Code);
                writer.WriteNumber("exitCode", Outcome.ExitCode);
                if (Message is not null)
                {
                    writer.WriteString("message", Message);
                }

                writer.WriteString("provider", ProviderName);
                writer.WriteString("metadataSchema", MetadataSchema);
                WriteNullableNumber(writer, "binarySchemaNumber", BinarySchemaNumber);
                WriteNullableNumber(writer, "schemaNumber", SchemaNumber);
                writer.WriteBoolean("schemaVerified", SchemaVerified);
                if (Plan is { Successful: true } plan)
                {
                    writer.WriteBoolean("journalWasEmpty", !plan.JournalIsNonEmpty);
                }

                writer.WriteStartArray("appliedScripts");
                foreach (var script in AppliedScripts)
                {
                    writer.WriteStringValue(script);
                }

                writer.WriteEndArray();

                writer.WriteStartArray("pendingScripts");
                if (Plan is { Successful: true } planned)
                {
                    var classifications = planned.PendingScriptClassifications
                        .ToDictionary(classification => classification.ScriptName, StringComparer.Ordinal);
                    foreach (var script in planned.PendingScripts)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("script", script);
                        writer.WriteString("classification", classifications.TryGetValue(script, out var classification)
                            ? FormatClassification(classification.Classification)
                            : "unclassified");
                        writer.WriteEndObject();
                    }
                }

                writer.WriteEndArray();

                if (Outcome == MigrateOnlyOutcome.ContractApprovalRequired && Plan?.ContractApprovalNonce is { } nonce)
                {
                    writer.WriteStartObject("contractApproval");
                    writer.WriteString("setting", MigrationSafetyOptions.ApproveContractMigrationsKey);
                    writer.WriteString("nonce", nonce);
                    writer.WriteEndObject();
                }

                writer.WriteStartArray("backupHooks");
                foreach (var hook in BackupHooks)
                {
                    writer.WriteStartObject();
                    writer.WriteString("outcome", hook.Outcome);
                    writer.WriteBoolean("succeeded", hook.Succeeded);
                    WriteNullableNumber(writer, "exitCode", hook.ExitCode);
                    writer.WriteNumber("durationMilliseconds", hook.DurationMilliseconds);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        private static void WriteNullableNumber(Utf8JsonWriter writer, string name, int? value)
        {
            if (value is { } number)
            {
                writer.WriteNumber(name, number);
            }
            else
            {
                writer.WriteNull(name);
            }
        }

        private static string FormatClassification(MigrationSafetyClassification classification) => classification switch
        {
            MigrationSafetyClassification.Expand => "expand",
            MigrationSafetyClassification.ContractAnnotated => "contract-annotated",
            MigrationSafetyClassification.ContractUnannotated => "contract-unannotated",
            _ => "unclassified",
        };
    }
}

/// <summary>Source-generated log messages for migrate-only runs.</summary>
internal static partial class MigrateOnlyLog
{
    [LoggerMessage(EventId = 6470, Level = LogLevel.Information,
        Message = "Migrate-only run starting; this binary's schema number is {BinarySchemaNumber}. No HTTP listener or background service is started.")]
    public static partial void Starting(ILogger logger, int? binarySchemaNumber);

    [LoggerMessage(EventId = 6471, Level = LogLevel.Information,
        Message = "Migrate-only run completed: applied {AppliedCount} script(s); schema number {SchemaNumber}.")]
    public static partial void Completed(ILogger logger, int appliedCount, int? schemaNumber);

    [LoggerMessage(EventId = 6472, Level = LogLevel.Error,
        Message = "Migrate-only run stopped ({Code}, exit {ExitCode}): {Reason}")]
    public static partial void Stopped(ILogger logger, string code, int exitCode, string reason);
}
