// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Server.Hosting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Hosting;

[Protocol(TestProtocols.Infrastructure)]
public sealed class KeyRingCertificateRefusalExitTests
{
    private static readonly string[] InheritedKeysToRemove =
    [
        "HONUA_SKIP_MIGRATIONS",
        "HONUA_REGISTER_TEST_INFRASTRUCTURE",
        "HONUA_TEST_SCHEMA_HEADERS",
        "HONUA_DB_DEGRADED_START",
        "Operations__SecretChannel__KeyRingCertificatePath",
        "Operations__SecretChannel__KeyRingCertificatePkcs12",
        "Operations__SecretChannel__KeyRingSecret",
    ];

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task SystemDatabaseWithoutKeyRingCertificate_RefusesNamingTheSettingAndExitsWithRefusalCode()
    {
        var output = await RunRefusedBootAsync(certificatePath: null);
        output.Should().Contain("Operations:SecretChannel:KeyRingCertificatePath");
        output.Should().Contain("Operations:SecretChannel:KeyRingCertificatePkcs12");
        output.Should().Contain("Operations:SecretChannel:KeyRingSecret");
    }

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task SystemDatabaseWithMalformedKeyRingCertificate_RefusesWithoutAbortingTheRuntime()
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
        var environment = new Dictionary<string, string>
        {
            ["ConnectionStrings__DefaultConnection"] = "Host=127.0.0.1;Port=5432;Database=honua;Username=honua;Password=unused",
            ["DataSource__Provider"] = "postgis",
            ["HONUA_ADMIN_PASSWORD"] = "KeyRing-Refusal-1a",
            ["Security__ConnectionEncryption__MasterKey"] = "keyring-refusal-connection-encryption-key",
            ["Security__ConnectionEncryption__Salt"] = "a2V5cmluZy1yZWZ1c2FsLXNhbHQ=",
        };
        if (certificatePath is not null)
        {
            environment["Operations__SecretChannel__KeyRingCertificatePath"] = certificatePath;
        }

        var start = ServerProcessLaunch.CreateProductionStartInfo(InheritedKeysToRemove, environment);

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
