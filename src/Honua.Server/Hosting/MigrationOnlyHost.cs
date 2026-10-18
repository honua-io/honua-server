// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.ControlPlane;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Db.Postgres;
using Honua.Db.Postgres.Features.Infrastructure;
using Honua.Db.Postgres.Features.Infrastructure.Migrations;
using Honua.Db.Postgres.Features.Security;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Monitoring;
using Honua.Server.Startup;
using Npgsql;

namespace Honua.Server.Hosting;

/// <summary>Runs the canonical database initialization without constructing a serving host.</summary>
internal static class MigrationOnlyHost
{
    internal const string ModeKey = "HONUA_MIGRATE_ONLY";
    internal const string RunIdKey = "HONUA_MIGRATION_RUN_ID";
    internal const string ReceiptPrefix = "HONUA_MIGRATION_RESULT ";

    // A malformed opt-in must fail in this profile instead of accidentally starting a server.
    internal static bool IsRequested(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration[ModeKey])
            && !string.Equals(configuration[ModeKey]?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    internal static IServiceCollection CreateServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        // Raw provider exceptions can contain endpoint/credential material. This profile emits
        // fixed stage codes and a structured receipt, not raw provider or migration SQL logs.
        services.AddLogging();
        services.AddConnectionSecretResolvers(configuration);
        services.AddPostgreSqlMigrationServices(configuration, ServerCoreSchemaMigrations.Manifest, logToConsole: false);
        return services;
    }

    internal static async Task<int> RunProcessAsync(IConfiguration configuration)
    {
        using var cancellation = new CancellationTokenSource();
        using var terminate = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
            ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            })
            : null;
        ConsoleCancelEventHandler interrupt = (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += interrupt;
        MigrationRunReceipt receipt;
        try
        {
            // Dispose every connection/resolver before reporting process completion. No hosted
            // service is registered, instantiated or started, and no HTTP listener is built.
            await using var services = CreateServices(configuration).BuildServiceProvider();
            receipt = await RunAsync(configuration, services, cancellation.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            receipt = MigrationRunReceipt.Failed("migration-host-failed");
        }
        finally
        {
            Console.CancelKeyPress -= interrupt;
        }

        Console.Out.WriteLine(ReceiptPrefix + JsonSerializer.Serialize(receipt, MigrationRunJsonContext.Default.MigrationRunReceipt));
        return receipt.Outcome == "completed" ? 0 : 1;
    }

    internal static async Task<MigrationRunReceipt> RunAsync(
        IConfiguration configuration, IServiceProvider services, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("D");
        var stage = "migration-configuration-invalid";
        try
        {
            if (!bool.TryParse(configuration[ModeKey], out var enabled) || !enabled
                || configuration.GetValue<bool>("HONUA_SKIP_MIGRATIONS")
                || ExecutionWorkerLaunchEnvironment.ReservedNames.Any(name => !string.IsNullOrWhiteSpace(configuration[name]))
                || DataProviderNames.Normalize(configuration["DataSource:Provider"]) is not (DataProviderNames.Postgis or DataProviderNames.PostgreSql))
            {
                return MigrationRunReceipt.Failed(stage, runId, startedAt);
            }

            if (!string.IsNullOrWhiteSpace(configuration[RunIdKey]))
            {
                if (!Guid.TryParseExact(configuration[RunIdKey], "D", out var parsedRunId))
                {
                    return MigrationRunReceipt.Failed(stage, runId, startedAt);
                }
                runId = parsedRunId.ToString("D");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var checker = services.GetRequiredService<IDatabaseCompatibilityChecker>();
            var runner = services.GetRequiredService<IDatabaseMigrationRunner>();
            var guard = services.GetRequiredService<IDatabaseSchemaGuard>();
            stage = "migration-connection-unavailable";
            var connectionString = await ConnectionStringResolutionHelper.ResolveDefaultConnectionStringAsync(
                configuration, services.GetRequiredService<IConnectionSecretResolver>(), cancellationToken);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return MigrationRunReceipt.Failed(stage, runId, startedAt);
            }

            var schema = PostgresSchemaConfiguration.FromConfiguration(configuration).MetadataSchema;
            var locatorHash = ComputeTargetLocatorHash(connectionString, schema);
            stage = "migration-preflight-failed";
            var compatibility = await StartupDatabasePreflight.CheckWithBoundedRetryAsync(
                checker, connectionString, StartupDatabasePreflight.TransientRetryDelays,
                static (delay, token) => Task.Delay(delay, token), cancellationToken);
            if (!compatibility.IsCompatible)
            {
                return MigrationRunReceipt.Failed(stage, runId, startedAt);
            }

            stage = "migration-apply-failed";
            var result = await runner.RunMigrationsAsync(connectionString, typeof(Program).Assembly, cancellationToken);
            if (!result.Successful)
            {
                return MigrationRunReceipt.Failed(stage, runId, startedAt);
            }

            // Canonical runner also verifies under its migration lock. Requiring the guard here
            // makes the completion contract explicit even if a future composition replaces it.
            stage = "migration-schema-verification-failed";
            await guard.VerifyAsync(connectionString, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new MigrationRunReceipt
            {
                Outcome = "completed",
                RunId = runId,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                TargetLocatorSha256 = locatorHash,
                MetadataSchema = schema,
                SchemaVerified = true,
                AppliedScriptNames = result.AppliedScripts.ToArray(),
                PackagedMigrationManifestSha256 = ComputePackagedManifestHash(),
                ServerVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
            };
        }
        catch (OperationCanceledException)
        {
            return MigrationRunReceipt.Failed("migration-cancelled", runId, startedAt) with { Outcome = "cancelled" };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return MigrationRunReceipt.Failed(stage, runId, startedAt);
        }
    }

    /// <summary>Hashes a connection locator, not credentials or proof of physical database identity.</summary>
    internal static string ComputeTargetLocatorHash(string connectionString, string schema)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(connection.Host) || connection.Host.Contains(',')
            || string.IsNullOrWhiteSpace(connection.Database))
        {
            throw new InvalidOperationException("Migration requires an explicit single database locator.");
        }
        // JSON provides unambiguous component boundaries; passwords/users/options are excluded.
        var components = new[]
        {
            "honua.database-locator/v1", connection.Host.Trim().TrimEnd('.').ToLowerInvariant(),
            connection.Port.ToString(CultureInfo.InvariantCulture), connection.Database, schema,
        };
        return Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(components, MigrationRunJsonContext.Default.StringArray)));
    }

    private static string ComputePackagedManifestHash()
    {
        var assemblies = new[] { typeof(Program).Assembly, typeof(PostgresDatabaseMigrationRunner).Assembly };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in assemblies.SelectMany(assembly => assembly.GetManifestResourceNames()
            .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name => (Assembly: assembly, Name: name))).OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            using var stream = entry.Assembly.GetManifestResourceStream(entry.Name)!;
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Name + "\n" + Convert.ToHexStringLower(SHA256.HashData(stream)) + "\n"));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

internal sealed record MigrationRunReceipt
{
    public string SchemaVersion => "honua.migration-run/v1";
    public string Provider => "postgres";
    public required string Outcome { get; init; }
    public string? RunId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public string? ErrorCode { get; init; }
    public string? TargetLocatorSha256 { get; init; }
    public string? MetadataSchema { get; init; }
    public bool SchemaVerified { get; init; }
    public string[]? AppliedScriptNames { get; init; }
    public int? AppliedScriptCount => AppliedScriptNames?.Length;
    public string? PackagedMigrationManifestSha256 { get; init; }
    public string? ServerVersion { get; init; }

    internal static MigrationRunReceipt Failed(string code, string? runId = null, DateTimeOffset? startedAt = null)
        => new() { Outcome = "failed", ErrorCode = code, RunId = runId, StartedAt = startedAt, CompletedAt = DateTimeOffset.UtcNow };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MigrationRunReceipt))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class MigrationRunJsonContext : JsonSerializerContext;
