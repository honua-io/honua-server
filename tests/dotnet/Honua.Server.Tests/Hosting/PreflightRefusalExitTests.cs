// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Server.Hosting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Hosting;

/// <summary>
/// Boots the real server entry point with a database connection string the driver cannot parse
/// (a generated password that begins with <c>=</c> reads as <c>Password==...</c>, honua-server#6495).
/// The PostGIS preflight must refuse with a message that names the problem, without echoing the
/// credential, and the process must exit with <see cref="GatedBootShutdown.RefusalExitCode"/>
/// instead of an abort or segmentation fault (exit 134 / 139), which is what an unhandled
/// startup exception produces. An in-process host cannot observe the process status, so the
/// test starts <c>Honua.Server.dll</c> out of process.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
public sealed class PreflightRefusalExitTests
{
    private const string SecretPart = "s3cr3t-part-of-the-password";

    private static readonly string[] InheritedKeysToRemove =
    [
        "HONUA_SKIP_MIGRATIONS",
        "HONUA_REGISTER_TEST_INFRASTRUCTURE",
        "HONUA_TEST_SCHEMA_HEADERS",
        "HONUA_DB_DEGRADED_START",
        "HONUA_TEST_HOSTED_BLAZOR_ASSETS",
        "ConnectionStrings__redis",
        "ConnectionStrings__Redis"
    ];

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task MalformedConnectionString_RefusesWithClearMessageAndExitsWithRefusalCode()
    {
        var serverDirectory = AppContext.BaseDirectory;
        var serverAssembly = Path.Join(serverDirectory, "Honua.Server.dll");
        File.Exists(serverAssembly).Should().BeTrue("the cached test payload contains the server runtime ({0})", serverDirectory);

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
        // "Password==..." is what Terraform renders for a generated password that starts with '='.
        start.Environment["ConnectionStrings__DefaultConnection"] =
            "Host=127.0.0.1;Port=5432;Database=honua;Username=honua;Password==" + SecretPart + ";SSL Mode=Require";
        start.Environment["DataSource__Provider"] = "postgis";
        start.Environment["HONUA_ADMIN_PASSWORD"] = "Preflight-Refusal-1a";
        start.Environment["Security__ConnectionEncryption__MasterKey"] =
            "preflight-refusal-connection-encryption-key";
        start.Environment["Security__ConnectionEncryption__Salt"] = "cHJlZmxpZ2h0LXJlZnVzYWwtc2FsdA==";

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
            "a malformed connection string must exit {0}, not abort or signal (observed {1}). Output tail:{2}{3}",
            GatedBootShutdown.RefusalExitCode,
            process.ExitCode,
            Environment.NewLine,
            tail);
        output.Should().Contain("malformed-connection-string");
        output.Should().NotContain(SecretPart, "the refusal must not echo the credential");
        output.Should().NotContain("Unhandled exception", "the refusal must be handled by the entry point");
        output.Should().NotContain("Now listening on:", "a refused boot must never start serving");
    }
}
