// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Server.Hosting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Hosting;

// A dev-grant license outside Development and Test must leave with the refusal exit code,
// not abort the runtime (exit 139 on the AOT image).
[Protocol(TestProtocols.Infrastructure)]
public sealed class DevGrantRefusalExitTests
{
    private static readonly string[] InheritedKeysToRemove =
    [
        "HONUA_SKIP_MIGRATIONS",
        "HONUA_MIGRATE_ONLY",
        "HONUA_REGISTER_TEST_INFRASTRUCTURE",
        "HONUA_TEST_SCHEMA_HEADERS",
        "HONUA_DB_DEGRADED_START",
        "HONUA_DEV_AUTH",
        "AWS_LAMBDA_FUNCTION_NAME",
        "Licensing__DevGrantEdition",
        "Operations__SecretChannel__KeyRingSecret",
    ];

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task DevGrantInProduction_RefusesWithRefusalExitCodeAndTheSetting()
    {
        var start = ServerStart();
        var keyRingPath = TestKeyRingCertificate.AddToEnvironment(start.Environment, "DevGrant-Refusal-KeyRing");
        RefusedBootRun run;
        try
        {
            run = await RefusedBootRun.RunAsync(start, TimeSpan.FromMinutes(4));
        }
        finally
        {
            File.Delete(keyRingPath);
        }

        run.Exited.Should().BeTrue("a refused boot must exit instead of serving. Output tail:{0}{1}", Environment.NewLine, run.Tail);
        run.ExitCode.Should().Be(
            StartupRefusal.ExitCode,
            "a dev-grant license in Production must exit {0}, not abort (observed {1}). Output tail:{2}{3}",
            StartupRefusal.ExitCode,
            run.ExitCode,
            Environment.NewLine,
            run.Tail);
        run.Output.Should().Contain("Licensing:DevGrantEdition is a test/dev-only license override");
        run.Output.Should().NotContain("Unhandled exception");
        run.Output.Should().NotContain("Now listening on:");
    }

    private static ProcessStartInfo ServerStart()
    {
        var start = RefusedBootRun.ProductionServer(
            "Host=127.0.0.1;Port=5432;Database=honua;Username=honua;Password=unused", InheritedKeysToRemove);
        start.Environment["Licensing__DevGrantEdition"] = "Enterprise";
        start.Environment["HONUA_ADMIN_PASSWORD"] = "DevGrant-Refusal-1a";
        start.Environment["Security__ConnectionEncryption__MasterKey"] = "devgrant-refusal-connection-encryption-key";
        start.Environment["Security__ConnectionEncryption__Salt"] = "ZGV2Z3JhbnQtcmVmdXNhbC1zYWx0";
        return start;
    }
}
