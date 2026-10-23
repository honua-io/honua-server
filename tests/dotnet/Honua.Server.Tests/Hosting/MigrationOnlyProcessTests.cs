// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Hosting.MigrateOnly;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Honua.Server.Tests.Hosting;

[Protocol(TestProtocols.Infrastructure)]
public sealed class MigrationOnlyProcessTests
{
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task FreshDatabase_InitializesAndRerunsWithoutServing_ThenRejectsSchemaDrift()
    {
        await using var database = new PostgreSqlBuilder()
            .WithImage("postgis/postgis:18-3.6")
            .WithDatabase("honua_migration_profile")
            .WithUsername("postgres")
            .WithPassword("migration-profile-test-secret")
            .WithLabel("honua.test.owner", "honua-server")
            .WithLabel("honua.test.run_id", Environment.GetEnvironmentVariable("HONUA_TEST_RUN_ID") ?? "manual")
            .Build();
        await database.StartAsync();
        // An accidental web-host start cannot succeed on the occupied configured port.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var first = await RunProcessAsync(database.GetConnectionString(), port);
        first.ExitCode.Should().Be(0, "{0}", first.Output);
        first.Receipt.GetProperty("outcome").GetString().Should().Be("completed");
        first.Receipt.GetProperty("schemaVerified").GetBoolean().Should().BeTrue();
        first.Receipt.GetProperty("appliedScripts").GetArrayLength().Should().BeGreaterThan(0);

        var second = await RunProcessAsync(database.GetConnectionString(), port);
        second.ExitCode.Should().Be(0, "{0}", second.Output);
        second.Receipt.GetProperty("appliedScripts").GetArrayLength().Should().Be(0);
        second.Receipt.GetProperty("targetLocatorSha256").GetString().Should().Be(first.Receipt.GetProperty("targetLocatorSha256").GetString());
        second.Receipt.GetProperty("packagedMigrationManifestSha256").GetString().Should().Be(first.Receipt.GetProperty("packagedMigrationManifestSha256").GetString());

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE honua.metadata_v2_current";
        await command.ExecuteNonQueryAsync();

        var drift = await RunProcessAsync(database.GetConnectionString(), port);
        drift.ExitCode.Should().Be(MigrateOnlyOutcome.MigrationFailed.ExitCode, "{0}", drift.Output);
        drift.Receipt.GetProperty("outcome").GetString().Should().Be("failed");
        drift.Receipt.GetProperty("schemaVerified").GetBoolean().Should().BeFalse();
        drift.Receipt.GetProperty("code").GetString().Should().Be("migration-failed");
    }

    private static async Task<(int ExitCode, JsonElement Receipt, string Output)> RunProcessAsync(string connection, int occupiedPort)
    {
        var assembly = Path.Join(AppContext.BaseDirectory, "Honua.Server.dll");
        File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json")).Should().BeTrue();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(assembly);
        foreach (var key in start.Environment.Keys.Where(key =>
            key.StartsWith("HONUA_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("Database__", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("DataSource__", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(key);
        }
        start.Environment["DOTNET_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{occupiedPort}";
        start.Environment["HONUA_MIGRATE_ONLY"] = "true";
        start.Environment["HONUA_MIGRATION_RUN_ID"] = Guid.NewGuid().ToString("D");
        start.Environment["HONUA_DB_DEGRADED_START"] = "true";
        start.Environment["ConnectionStrings__DefaultConnection"] = connection;
        // This must not be resolved or contacted; migration-only needs no Redis or admin key.
        start.Environment["ConnectionStrings__redis"] = "env:HONUA_MIGRATION_TEST_UNSET_REDIS";
        start.Environment.Remove("HONUA_MIGRATION_TEST_UNSET_REDIS");
        start.Environment["DataSource__Provider"] = "postgis";

        using var process = new Process { StartInfo = start };
        process.Start().Should().BeTrue();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            // This test owns the child process; clean it up when its bounded assertion fails.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            throw;
        }
        var standardOutput = await stdout;
        var output = standardOutput + await stderr;
        output.Should().NotContain("Now listening on:").And.NotContain("migration-profile-test-secret").And.NotContain("Unhandled exception");
        var lines = standardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Should().ContainSingle("a process reports one terminal summary; output: {0}", output);
        using var document = JsonDocument.Parse(lines[0]);
        document.RootElement.GetProperty("format").GetString().Should().Be(MigrateOnlyCommand.SummaryFormat);
        return (process.ExitCode, document.RootElement.Clone(), output);
    }
}
