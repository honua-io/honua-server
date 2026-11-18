// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Server.Hosting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Hosting;

// The service and layer catalog lives only in the PostgreSQL/PostGIS system database. A production host whose
// primary is DuckDB or MySQL has no catalog store, so it must refuse to start with one clear message and a plain
// exit status, instead of starting and answering every catalog-bound request with an error. The startup checks
// that still apply to such a host in the Test environment refuse the same way rather than crashing.
[Protocol(TestProtocols.Infrastructure)]
public sealed class NonPostgreSqlPrimaryProductionBootTests
{
    private const string CatalogRefusal = "service and layer catalog is stored only in a PostgreSQL/PostGIS system database";
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(4);

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
    public Task DuckDbPrimaryInProduction_WithoutAConnectionString_RefusesNamingTheCatalog()
        => AssertRefusedAsync("duckdb", new Dictionary<string, string>(), CatalogRefusal);

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public Task DuckDbPrimaryInProduction_WithAConnectionString_RefusesNamingTheCatalog()
        => AssertRefusedAsync("duckdb", new Dictionary<string, string>
        {
            ["ConnectionStrings__DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=honua;Username=honua;Password=unused",
        }, CatalogRefusal);

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public Task MySqlPrimaryInProduction_RefusesNamingTheCatalog()
        => AssertRefusedAsync("mysql", new Dictionary<string, string>(), CatalogRefusal);

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public Task DuckDbPrimaryRequiringADurableJobStore_RefusesWithoutCrashing()
        => AssertRefusedAsync("duckdb", new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Test",
            ["DOTNET_ENVIRONMENT"] = "Test",
            ["Jobs__RequireDurableStore"] = "true",
        }, "Jobs:RequireDurableStore");

    private static async Task AssertRefusedAsync(string provider, Dictionary<string, string> extraEnvironment, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("honua-nonpg-primary-");
        try
        {
            var start = RefusedBootRun.ProductionServer(
                InheritedKeysToRemove,
                CreateEnvironment(provider, directory.FullName, extraEnvironment));
            var run = await RefusedBootRun.RunAsync(start, Deadline);
            run.Exited.Should().BeTrue("the host must refuse instead of serving; output tail:{0}{1}", Environment.NewLine, run.Tail);
            run.ExitCode.Should().Be(StartupRefusal.ExitCode, "output tail:{0}{1}", Environment.NewLine, run.Tail);
            run.Output.Should().Contain(expected);
            run.Output.Should().NotContain("Unhandled exception");
            run.Output.Should().NotContain("NpgsqlDataSource");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static Dictionary<string, string> CreateEnvironment(
        string provider,
        string directory,
        Dictionary<string, string> extraEnvironment)
    {
        var environment = new Dictionary<string, string>
        {
            ["AllowedHosts"] = "*",
            ["Licensing__Mode"] = "Disabled",
            ["FileStorage__Provider"] = "Local",
            ["FileStorage__LocalStorage__BasePath"] = Path.Join(directory, "storage"),
            ["DataSource__Provider"] = provider,
            ["DuckDB__DatabasePath"] = Path.Join(directory, "primary.duckdb"),
            ["DuckDB__ReadOnly"] = "false",
            ["HONUA_ADMIN_PASSWORD"] = "NonPg-Primary-Boot-1a!",
            ["AuditLog__ChainVerification__Key"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("nonpg-primary-boot-audit-chain-verification-key")),
            ["Security__ConnectionEncryption__MasterKey"] = "nonpg-primary-boot-connection-encryption-key",
            ["Security__ConnectionEncryption__Salt"] = "ZHVja2RiLXByaW1hcnktc2FsdA==",
        };
        foreach (var (key, value) in extraEnvironment)
        {
            environment[key] = value;
        }

        return environment;
    }
}
