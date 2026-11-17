// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Server.Hosting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;

namespace Honua.Server.Tests.Hosting;

// A configuration validation failure must leave with the refusal exit code and name the failed
// setting, not abort the runtime (exit 139 on the AOT image).
[Protocol(TestProtocols.Infrastructure)]
public sealed class ConfigurationValidationRefusalExitTests
{
    private static readonly string[] InheritedKeysToRemove =
    [
        "HONUA_SKIP_MIGRATIONS",
        "HONUA_MIGRATE_ONLY",
        "HONUA_REGISTER_TEST_INFRASTRUCTURE",
        "HONUA_TEST_SCHEMA_HEADERS",
        "HONUA_DB_DEGRADED_START",
        "AWS_LAMBDA_FUNCTION_NAME",
        "Operations__SecretChannel__KeyRingSecret",
    ];

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task DevelopmentAuthInProduction_RefusesWithRefusalExitCodeAndTheFailedSetting()
    {
        var start = ServerStart();
        var keyRingPath = TestKeyRingCertificate.AddToEnvironment(start.Environment, "Config-Refusal-KeyRing");
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
            "a configuration validation failure must exit {0}, not abort (observed {1}). Output tail:{2}{3}",
            StartupRefusal.ExitCode,
            run.ExitCode,
            Environment.NewLine,
            run.Tail);
        run.Output.Should().Contain("Configuration validation failed").And.Contain("HONUA_DEV_AUTH");
        run.Output.Should().NotContain("Unhandled exception");
        run.Output.Should().NotContain("Now listening on:");
    }

    private static ProcessStartInfo ServerStart()
    {
        var start = RefusedBootRun.ProductionServer(
            "Host=127.0.0.1;Port=5432;Database=honua;Username=honua;Password=unused", InheritedKeysToRemove);
        start.Environment["HONUA_DEV_AUTH"] = "true";
        start.Environment["HONUA_ADMIN_PASSWORD"] = "Config-Refusal-1a";
        start.Environment["Security__ConnectionEncryption__MasterKey"] = "config-refusal-connection-encryption-key";
        start.Environment["Security__ConnectionEncryption__Salt"] = "Y29uZmlnLXJlZnVzYWwtc2FsdA==";
        return start;
    }
}
