// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Hosting.MigrateOnly;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Hosting;

/// <summary>
/// Starts the real server entry point out of process with <c>HONUA_MIGRATE_ONLY=true</c>
/// (honua-server#6470) in the Production environment, with no admin password, encryption key,
/// licence or Redis configured. The process must take the migrate-only branch before the web host
/// is built: no listener, one JSON summary line on standard output, and the typed exit code. The
/// applying path against a real database is proven by <c>LocalSubstrateMigrateOnlyTests</c>.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
public sealed class MigrateOnlyProcessTests
{
    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task MigrateOnly_WithoutConnectionString_ExitsWithTypedCodeAndNeverListens()
    {
        var run = await RunServerAsync(connectionString: null);

        run.ExitCode.Should().Be(MigrateOnlyOutcome.InvalidConfiguration.ExitCode, run.Describe());
        run.Summary.GetProperty("code").GetString().Should().Be("invalid-configuration");
        run.Summary.GetProperty("message").GetString().Should().Contain("ConnectionStrings:DefaultConnection");
        run.StandardError.Should().NotContain("Now listening on:", "migrate-only mode never starts Kestrel");
        run.StandardError.Should().NotContain("Unhandled exception");
    }

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task MigrateOnly_WithUnreachableDatabase_ExitsDatabaseUnavailable()
    {
        var run = await RunServerAsync("Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2");

        run.ExitCode.Should().Be(MigrateOnlyOutcome.DatabaseUnavailable.ExitCode, run.Describe());
        run.Summary.GetProperty("code").GetString().Should().Be("database-unavailable");
        run.Summary.GetProperty("message").GetString().Should().NotContain("Password", "the summary never echoes the connection string");
        run.StandardError.Should().NotContain("Now listening on:");
    }

    private static async Task<ServerRun> RunServerAsync(string? connectionString)
    {
        var serverDirectory = AppContext.BaseDirectory;
        var serverAssembly = Path.Join(serverDirectory, "Honua.Server.dll");
        File.Exists(serverAssembly).Should().BeTrue("the test output carries the server runtime ({0})", serverDirectory);

        var start = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = serverDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(serverAssembly);
        foreach (var key in start.Environment.Keys.Where(IsInheritedHonuaSetting).ToArray())
        {
            start.Environment.Remove(key);
        }

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment[MigrateOnlyMode.SwitchKey] = "true";
        if (connectionString != null)
        {
            start.Environment["ConnectionStrings__DefaultConnection"] = connectionString;
        }

        using var process = new Process { StartInfo = start };
        process.Start().Should().BeTrue();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The migrate-only process did not exit: " + await stderrTask);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines.Should().ContainSingle("standard output carries only the summary. stdout:{0}{1}{0}stderr:{0}{2}",
            Environment.NewLine, stdout, stderr);
        using var document = JsonDocument.Parse(lines[0]);
        return new ServerRun(process.ExitCode, document.RootElement.Clone(), stderr);
    }

    // Inherited test-runner configuration (connection strings, Redis, skip/approval switches) must
    // not reach the child; it runs with only what each case sets.
    private static bool IsInheritedHonuaSetting(string key)
        => key.StartsWith("HONUA_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("ConnectionStrings__", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("Database__", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DataSource__", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("AWS_LAMBDA_", StringComparison.OrdinalIgnoreCase);

    private sealed record ServerRun(int ExitCode, JsonElement Summary, string StandardError)
    {
        public string Describe() => $"summary: {Summary}{Environment.NewLine}stderr tail: {Tail(StandardError)}";

        private static string Tail(string value) => value.Length <= 4000 ? value : value[^4000..];
    }
}
