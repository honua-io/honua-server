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
/// Boots the real server entry point with migrations skipped against a database whose raster
/// baseline table exists and does not match the creation contract, so the provider root is not
/// adopted. The schema-floor refusal must exit with <see cref="GatedBootShutdown.RefusalExitCode"/>
/// and the refusal text, not abort the runtime (honua-server#6496). An in-process host cannot
/// observe the process status, so this test starts <c>Honua.Server.dll</c> out of process.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
public sealed class SchemaFloorRefusalExitTests
{
    private const string PostgisImage = "postgis/postgis:18-3.6";
    private const string TestRunIdEnv = "HONUA_TEST_RUN_ID";

    private static readonly string[] InheritedKeysToRemove =
    [
        "HONUA_APPROVE_CONTRACT_MIGRATIONS",
        "HONUA_SKIP_MIGRATIONS",
        "HONUA_REGISTER_TEST_INFRASTRUCTURE",
        "HONUA_TEST_SCHEMA_HEADERS",
        "HONUA_DB_DEGRADED_START",
        "HONUA_TEST_HOSTED_BLAZOR_ASSETS",
        "ConnectionStrings__redis",
        "ConnectionStrings__Redis",
        "AWS_LAMBDA_FUNCTION_NAME"
    ];

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task SkipMigrations_UnmatchedRasterBaseline_ExitsWithRefusalInsteadOfAborting()
    {
        var serverDirectory = AppContext.BaseDirectory;
        var serverAssembly = Path.Join(serverDirectory, "Honua.Server.dll");
        File.Exists(serverAssembly).Should().BeTrue(
            "the cached test payload contains the server runtime ({0})", serverDirectory);

        await using var container = new PostgreSqlBuilder()
            .WithImage(PostgisImage)
            .WithDatabase("honua_schema_floor")
            .WithUsername("postgres")
            .WithPassword("schema_floor_password")
            .WithLabel("honua.test.owner", "honua-server")
            .WithLabel("honua.test.run_id", Environment.GetEnvironmentVariable(TestRunIdEnv) ?? "manual")
            .Build();
        await container.StartAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Timeout = 60,
            CommandTimeout = 120
        }.ConnectionString;
        await SeedUnmatchedRasterBaselineAsync(connectionString);

        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = serverDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(serverAssembly);
        foreach (var key in InheritedKeysToRemove)
        {
            start.Environment.Remove(key);
        }

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["ConnectionStrings__DefaultConnection"] = connectionString;
        start.Environment["DataSource__Provider"] = "postgis";
        start.Environment["HONUA_SKIP_MIGRATIONS"] = "true";
        // Lambda turns degraded start on. SchemaExistsWithoutJournal must still refuse, not degrade
        // and not abort.
        start.Environment["AWS_LAMBDA_FUNCTION_NAME"] = "honua-schema-floor";
        start.Environment["HONUA_ADMIN_PASSWORD"] = "SchemaFloor-Refusal-1a";
        start.Environment["Security__ConnectionEncryption__MasterKey"] =
            "schema-floor-refusal-connection-encryption-key";
        start.Environment["Security__ConnectionEncryption__Salt"] = "c2NoZW1hLWZsb29yLXJlZnVzYWwtc2FsdA==";

        using var process = new Process { StartInfo = start };
        process.Start().Should().BeTrue();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var exited = true;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            exited = false;
            process.Kill(entireProcessTree: true);
        }

        var output = await stdoutTask.ConfigureAwait(false) + await stderrTask.ConfigureAwait(false);
        var tail = output.Length <= 4000 ? output : output[^4000..];
        exited.Should().BeTrue("a refused boot must exit instead of serving. Output tail:{0}{1}", Environment.NewLine, tail);
        process.ExitCode.Should().Be(
            GatedBootShutdown.RefusalExitCode,
            "an unmatched raster baseline must exit {0}, not abort or signal (observed {1}). Output tail:{2}{3}",
            GatedBootShutdown.RefusalExitCode,
            process.ExitCode,
            Environment.NewLine,
            tail);
        output.Should().Contain("SchemaExistsWithoutJournal");
        output.Should().Contain("Honua.Postgres.Migrations.001_CreateRasterTables.sql");
        output.Should().NotContain("Unhandled exception", "the floor refusal must be handled by the entry point");
        output.Should().NotContain("Now listening on:", "a refused boot must never start serving");
        await AssertJournalUnchangedAsync(connectionString);
    }

    private static async Task SeedUnmatchedRasterBaselineAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            CREATE EXTENSION IF NOT EXISTS postgis;
            CREATE SCHEMA IF NOT EXISTS honua;
            CREATE TABLE honua.raster_data (id integer);
            CREATE TABLE public.schema_versions (
                schemaversionsid serial NOT NULL,
                scriptname character varying(255) NOT NULL,
                applied timestamp without time zone NOT NULL,
                CONSTRAINT schemaversions_pk PRIMARY KEY (schemaversionsid)
            );
            INSERT INTO public.schema_versions (scriptname, applied)
            VALUES ('Honua.Server.Migrations.091_RenormalizeGeocodeReferenceSearchText.sql', NOW());
            """,
            connection);
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertJournalUnchangedAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT scriptname FROM public.schema_versions ORDER BY schemaversionsid;",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var scripts = new List<string>();
        while (await reader.ReadAsync())
        {
            scripts.Add(reader.GetString(0));
        }

        scripts.Should().Equal(["Honua.Server.Migrations.091_RenormalizeGeocodeReferenceSearchText.sql"],
            because: "an unmatched raster baseline must not be journaled as adopted");
    }
}
