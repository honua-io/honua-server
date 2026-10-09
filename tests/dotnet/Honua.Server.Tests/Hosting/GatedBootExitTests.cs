// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Server.Hosting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Honua.Server.Tests.Hosting;

/// <summary>
/// Boots the real server entry point against a database whose migration journal is behind an
/// annotated contract migration and the approval token is unset. The safety gate must refuse,
/// the process must exit with <see cref="GatedBootShutdown.RefusalExitCode"/>, and shutdown must
/// not write a crash dump. An in-process host cannot observe the process status or a dump, so
/// this test starts <c>Honua.Server.dll</c> out of process.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
public sealed class GatedBootExitTests
{
    private const string PostgisImage = "postgis/postgis:18-3.6";
    private const string TestRunIdEnv = "HONUA_TEST_RUN_ID";

    /// <summary>
    /// Journal row for a real migration that the core schema guard does not require objects for.
    /// The journal is therefore non-empty (an existing database) while <c>038</c> stays pending.
    /// </summary>
    private const string SeededJournalScript = "Honua.Server.Migrations.002_CreateAttachmentsTable.sql";

    private const string PendingContractScript = "038_DropV1MetadataGraphTables";

    private static readonly string[] InheritedKeysToRemove =
    [
        "HONUA_APPROVE_CONTRACT_MIGRATIONS",
        "HONUA_SKIP_MIGRATIONS",
        "Database__MigrationSafety__ContractApplyPolicy",
        "Database__MigrationSafety__Enforce",
        "HONUA_REGISTER_TEST_INFRASTRUCTURE",
        "HONUA_TEST_SCHEMA_HEADERS",
        "HONUA_DB_DEGRADED_START",
        "HONUA_TEST_HOSTED_BLAZOR_ASSETS",
        "ConnectionStrings__redis",
        "ConnectionStrings__Redis"
    ];

    private static readonly string[] RequiredRuntimeFiles =
    [
        "Honua.Server.dll",
        "Honua.Server.runtimeconfig.json",
        "Honua.Server.deps.json",
        "appsettings.json"
    ];

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task RefusedContractMigration_ExitsWithRefusalCodeAndWritesNoCrashDump()
    {
        var serverAssembly = LocateServerAssembly(AppContext.BaseDirectory);
        var serverDirectory = Path.GetDirectoryName(serverAssembly)!;

        await using var container = new PostgreSqlBuilder()
            .WithImage(PostgisImage)
            .WithDatabase("honua_gated_boot")
            .WithUsername("postgres")
            .WithPassword("gated_boot_password")
            .WithLabel("honua.test.owner", "honua-server")
            .WithLabel("honua.test.run_id", Environment.GetEnvironmentVariable(TestRunIdEnv) ?? "manual")
            .Build();

        await container.StartAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Timeout = 60,
            CommandTimeout = 120
        }.ConnectionString;

        await SeedExistingDatabaseAsync(connectionString);

        File.Exists(Path.Join(serverDirectory, "appsettings.json")).Should().BeTrue(
            "the server process uses its output directory as the content root ({0})",
            serverDirectory);

        var dumpDirectory = Path.Join(Path.GetTempPath(), "honua-gated-boot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dumpDirectory);
        var dumpPath = Path.Join(dumpDirectory, "gated-boot.dmp");
        var dumpsBefore = SnapshotCrashDumps(serverDirectory, dumpDirectory);

        var start = new ProcessStartInfo
        {
            FileName = "bash",
            WorkingDirectory = serverDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("ulimit -c unlimited; exec \"$@\"");
        start.ArgumentList.Add("bash");
        start.ArgumentList.Add("dotnet");
        start.ArgumentList.Add(serverAssembly);

        // The approval token and the skip switch stay unset. Inherited values would bypass the gate.
        foreach (var key in InheritedKeysToRemove)
        {
            start.Environment.Remove(key);
        }

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["ConnectionStrings__DefaultConnection"] = connectionString;
        start.Environment["DataSource__Provider"] = "postgis";
        // Production refuses to build a host without these. They are not the migration
        // approval token and they do not skip migrations; they only let boot reach the gate.
        start.Environment["HONUA_ADMIN_PASSWORD"] = "GatedBoot-Refusal-1a";
        start.Environment["Security__ConnectionEncryption__MasterKey"] =
            "gated-boot-refusal-connection-encryption-key";
        start.Environment["Security__ConnectionEncryption__Salt"] = "Z2F0ZWQtYm9vdC1yZWZ1c2FsLXNhbHQ=";
        start.Environment["DOTNET_DbgEnableMiniDump"] = "1";
        start.Environment["DOTNET_DbgMiniDumpName"] = dumpPath;
        start.Environment["COMPlus_DbgEnableMiniDump"] = "1";
        start.Environment["COMPlus_DbgMiniDumpName"] = dumpPath;

        using var process = new Process { StartInfo = start };
        process.Start().Should().BeTrue();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = await WaitForExitAsync(process, TimeSpan.FromMinutes(4));
        var output = await stdoutTask.ConfigureAwait(false) + await stderrTask.ConfigureAwait(false);
        var tail = Tail(output);
        var dumpsCreated = SnapshotCrashDumps(serverDirectory, dumpDirectory)
            .Where(path => !dumpsBefore.Contains(path))
            .ToArray();

        exited.Should().BeTrue(
            "a refused boot must exit instead of serving. Output tail:{0}{1}",
            Environment.NewLine,
            tail);
        process.ExitCode.Should().Be(
            GatedBootShutdown.RefusalExitCode,
            "the gate refusal must exit {0}, not abort or signal (observed {1}; dumps: {2}). Output tail:{3}{4}",
            GatedBootShutdown.RefusalExitCode,
            process.ExitCode,
            dumpsCreated.Length == 0 ? "none" : string.Join(", ", dumpsCreated),
            Environment.NewLine,
            tail);
        output.Should().Contain(GatedBootShutdown.ContractGateRefusalPrefix);
        output.Should().Contain(PendingContractScript);
        dumpsCreated.Should().BeEmpty(
            "refused boot must not write a crash dump. Output tail:{0}{1}",
            Environment.NewLine,
            tail);
    }

    private static async Task SeedExistingDatabaseAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            CREATE EXTENSION IF NOT EXISTS postgis;
            CREATE TABLE public.schema_versions (
                schemaversionsid serial NOT NULL,
                scriptname character varying(255) NOT NULL,
                applied timestamp without time zone NOT NULL,
                CONSTRAINT schemaversions_pk PRIMARY KEY (schemaversionsid)
            );
            INSERT INTO public.schema_versions (scriptname, applied)
            VALUES (@script, NOW());
            """,
            connection);
        command.Parameters.AddWithValue("script", SeededJournalScript);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    [UnitTheory]
    [InlineData("Honua.Server.dll")]
    [InlineData("Honua.Server.runtimeconfig.json")]
    [InlineData("Honua.Server.deps.json")]
    [InlineData("appsettings.json")]
    public void LocateServerAssembly_IncompleteRuntime_DiagnosesMissingFile(string missingFile)
    {
        var runtimeDirectory = Path.Join(Path.GetTempPath(), "honua-boot-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeDirectory);
        try
        {
            foreach (var file in RequiredRuntimeFiles.Where(file => file != missingFile))
            {
                File.WriteAllText(Path.Join(runtimeDirectory, file), "{}");
            }

            Action locate = () => LocateServerAssembly(runtimeDirectory);
            locate.Should().Throw<InvalidOperationException>()
                .WithMessage($"Incomplete server runtime in test output '{runtimeDirectory}': missing '{missingFile}'.");
        }
        finally
        {
            Directory.Delete(runtimeDirectory, recursive: true);
        }
    }

    private static string LocateServerAssembly(string runtimeDirectory)
    {
        // The cached test payload contains the complete server runtime; source bin is not restored.
        foreach (var file in RequiredRuntimeFiles)
        {
            if (!File.Exists(Path.Join(runtimeDirectory, file)))
            {
                throw new InvalidOperationException(
                    $"Incomplete server runtime in test output '{runtimeDirectory}': missing '{file}'.");
            }
        }

        return Path.Join(runtimeDirectory, "Honua.Server.dll");
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return process.WaitForExit(TimeSpan.FromSeconds(15));
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
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill.
        }
    }

    private static HashSet<string> SnapshotCrashDumps(params string[] directories)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(path);
                if (name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("core", StringComparison.Ordinal))
                {
                    found.Add(path);
                }
            }
        }

        return found;
    }

    private static string Tail(string value)
    {
        const int limit = 4000;
        if (value.Length <= limit)
        {
            return value;
        }

        return value[^limit..];
    }
}
