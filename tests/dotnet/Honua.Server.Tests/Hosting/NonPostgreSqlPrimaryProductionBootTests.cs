// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Hosting;

// A Production host whose primary is not PostgreSQL has no system database. It must start and answer its
// liveness probe, and must not resolve the PostgreSQL data source that only the PostGIS primary registers,
// whether or not a PostgreSQL connection string is also set.
[Protocol(TestProtocols.Infrastructure)]
public sealed class NonPostgreSqlPrimaryProductionBootTests
{
    private static readonly TimeSpan StartupDeadline = TimeSpan.FromMinutes(4);

    private static readonly string[] InheritedKeysToRemove =
    [
        "HONUA_SKIP_MIGRATIONS",
        "HONUA_REGISTER_TEST_INFRASTRUCTURE",
        "HONUA_TEST_SCHEMA_HEADERS",
        "HONUA_DB_DEGRADED_START",
        "ConnectionStrings__DefaultConnection",
        "Operations__SecretChannel__KeyRingCertificatePath",
        "Operations__SecretChannel__KeyRingCertificatePkcs12",
        "Operations__SecretChannel__KeyRingSecret",
    ];

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public Task DuckDbPrimaryInProduction_WithoutAConnectionString_Starts()
        => AssertStartsAsync(new Dictionary<string, string>());

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public Task DuckDbPrimaryInProduction_WithAConnectionString_StartsWithoutASystemDatabase()
        => AssertStartsAsync(new Dictionary<string, string>
        {
            ["ConnectionStrings__DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=honua;Username=honua;Password=unused",
        });

    private static async Task AssertStartsAsync(Dictionary<string, string> extraEnvironment)
    {
        var directory = Directory.CreateTempSubdirectory("honua-duckdb-primary-");
        try
        {
            var run = await RunUntilLiveAsync(Path.Join(directory.FullName, "primary.duckdb"), extraEnvironment);
            run.LiveAfter.Should().NotBeNull("the host must answer /healthz/live; output:{0}{1}", Environment.NewLine, run.Output);
            run.Output.Should().NotContain("NpgsqlDataSource");
            run.Output.Should().NotContain("Unhandled exception");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<BootRun> RunUntilLiveAsync(string databasePath, Dictionary<string, string> extraEnvironment)
    {
        var port = FreeLoopbackPort();
        using var process = new Process { StartInfo = CreateStartInfo(databasePath, port, extraEnvironment) };
        var output = new StringBuilder();
        DataReceivedEventHandler collect = (_, line) =>
        {
            lock (output)
            {
                output.AppendLine(line.Data);
            }
        };
        process.OutputDataReceived += collect;
        process.ErrorDataReceived += collect;
        process.Start().Should().BeTrue();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var live = await WaitForLiveAsync(process, port);
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }

        await process.WaitForExitAsync();
        lock (output)
        {
            return new BootRun(live, output.ToString());
        }
    }

    private static async Task<TimeSpan?> WaitForLiveAsync(Process process, int port)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var probe = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}/healthz/live");
        var deadline = Stopwatch.StartNew();
        while (!process.HasExited && deadline.Elapsed < StartupDeadline)
        {
            if (await AnswersOkAsync(client, probe))
            {
                return deadline.Elapsed;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        return null;
    }

    private static async Task<bool> AnswersOkAsync(HttpClient client, Uri probe)
    {
        try
        {
            using var response = await client.GetAsync(probe);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static ProcessStartInfo CreateStartInfo(string databasePath, int port, Dictionary<string, string> extraEnvironment)
    {
        var origin = $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
        var environment = new Dictionary<string, string>(extraEnvironment)
        {
            ["ASPNETCORE_URLS"] = origin,
            ["PUBLIC_BASE_URL"] = origin,
            ["AllowedHosts"] = "*",
            ["Licensing__Mode"] = "Disabled",
            ["FileStorage__Provider"] = "Local",
            ["FileStorage__LocalStorage__BasePath"] = Path.Join(Path.GetDirectoryName(databasePath), "storage"),
            ["DataSource__Provider"] = "duckdb",
            ["DuckDB__DatabasePath"] = databasePath,
            ["DuckDB__ReadOnly"] = "false",
            ["HONUA_ADMIN_PASSWORD"] = "DuckDb-Primary-Boot-1a!",
            ["AuditLog__ChainVerification__Key"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("duckdb-primary-boot-audit-chain-verification-key")),
            ["Security__ConnectionEncryption__MasterKey"] = "duckdb-primary-boot-connection-encryption-key",
            ["Security__ConnectionEncryption__Salt"] = "ZHVja2RiLXByaW1hcnktc2FsdA==",
        };
        return RefusedBootRun.ProductionServer(InheritedKeysToRemove, environment);
    }

    private sealed record BootRun(TimeSpan? LiveAfter, string Output);
}
