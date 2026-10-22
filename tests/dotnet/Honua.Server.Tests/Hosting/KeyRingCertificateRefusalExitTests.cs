// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Server.Hosting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Testcontainers.Redis;

namespace Honua.Server.Tests.Hosting;

/// <summary>
/// Boots the real server entry point in Production with a reachable Redis and no operation
/// key-ring certificate (the Lambda + Redis topology of honua-server#6500). Redis switches the
/// durable operation secret channel on, which needs the certificate. The refusal must name the
/// setting and exit with <see cref="StartupRefusal.ExitCode"/>, not abort the runtime
/// ("signal: aborted" on Lambda, 134 / 139 locally).
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
public sealed class KeyRingCertificateRefusalExitTests
{
    private static readonly string[] InheritedKeysToRemove =
    [
        "HONUA_SKIP_MIGRATIONS",
        "HONUA_REGISTER_TEST_INFRASTRUCTURE",
        "HONUA_TEST_SCHEMA_HEADERS",
        "HONUA_DB_DEGRADED_START",
        "HONUA_TEST_HOSTED_BLAZOR_ASSETS",
        "Operations__SecretChannel__KeyRingCertificatePath",
        "Operations__SecretChannel__KeyRingCertificatePkcs12",
    ];

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task RedisWithoutKeyRingCertificate_RefusesNamingTheSettingAndExitsWithRefusalCode()
    {
        var output = await RunRefusedBootAsync(certificatePath: null);
        output.Should().Contain("Operations:SecretChannel:KeyRingCertificatePath");
        output.Should().Contain("Operations:SecretChannel:KeyRingCertificatePkcs12");
    }

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task RedisWithMalformedKeyRingCertificate_RefusesWithoutAbortingTheRuntime()
    {
        var path = Path.Join(Path.GetTempPath(), "honua-bad-keyring-" + Guid.NewGuid().ToString("N") + ".pfx");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4, 5]);
        try
        {
            var output = await RunRefusedBootAsync(path);
            output.Should().Contain("PKCS#12");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> RunRefusedBootAsync(string? certificatePath)
    {
        var serverDirectory = AppContext.BaseDirectory;
        var serverAssembly = Path.Join(serverDirectory, "Honua.Server.dll");
        File.Exists(serverAssembly).Should().BeTrue("the cached test payload contains the server runtime ({0})", serverDirectory);

        await using var redis = new RedisBuilder("redis:7.2-alpine").Build();
        await redis.StartAsync();

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
        start.Environment["ConnectionStrings__DefaultConnection"] =
            "Host=127.0.0.1;Port=5432;Database=honua;Username=honua;Password=unused";
        start.Environment["ConnectionStrings__redis"] = redis.GetConnectionString();
        start.Environment["DataSource__Provider"] = "postgis";
        if (certificatePath is not null)
        {
            start.Environment["Operations__SecretChannel__KeyRingCertificatePath"] = certificatePath;
        }

        start.Environment["HONUA_ADMIN_PASSWORD"] = "KeyRing-Refusal-1a";
        start.Environment["Security__ConnectionEncryption__MasterKey"] = "keyring-refusal-connection-encryption-key";
        start.Environment["Security__ConnectionEncryption__Salt"] = "a2V5cmluZy1yZWZ1c2FsLXNhbHQ=";

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
            StartupRefusal.ExitCode,
            "a missing key-ring certificate must exit {0}, not abort (observed {1}). Output tail:{2}{3}",
            StartupRefusal.ExitCode,
            process.ExitCode,
            Environment.NewLine,
            tail);
        output.Should().NotContain("Unhandled exception");
        output.Should().NotContain("Now listening on:");
        return output;
    }
}
