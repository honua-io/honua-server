// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Server.Features.Operations;
using Honua.Server.Hosting;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Honua.Server.Tests.Hosting;

// A ring wrapped with another KeyRingSecret is refused when the key-ring options are built. The
// refusal must leave with the refusal exit code, not abort the runtime (exit 139 on the AOT image).
[Protocol(TestProtocols.Infrastructure)]
public sealed class KeyRingSecretRefusalExitTests
{
    private const string PostgisImage = "postgis/postgis:18-3.6";
    private const string TestRunIdEnv = "HONUA_TEST_RUN_ID";

    private static readonly string[] InheritedKeysToRemove =
    [
        "HONUA_APPROVE_CONTRACT_MIGRATIONS",
        "HONUA_SKIP_MIGRATIONS",
        "HONUA_MIGRATE_ONLY",
        "HONUA_REGISTER_TEST_INFRASTRUCTURE",
        "HONUA_TEST_SCHEMA_HEADERS",
        "HONUA_DB_DEGRADED_START",
        "AWS_LAMBDA_FUNCTION_NAME",
        "Operations__SecretChannel__KeyRingCertificatePath",
        "Operations__SecretChannel__KeyRingCertificatePkcs12",
        "Operations__SecretChannel__KeyRingSecret",
    ];

    [IntegrationTest]
    [Operation(Operations.ContractTesting)]
    public async Task RingWrappedWithAnotherSecret_RefusesWithRefusalExitCodeAndKeepsTheRing()
    {
        await using var container = NewDatabase();
        await container.StartAsync();
        var connectionString = ConnectionString(container);
        var migrate = await RefusedBootRun.RunAsync(MigrateOnly(connectionString), TimeSpan.FromMinutes(4));
        migrate.ExitCode.Should().Be(0, "migrate-only prepares the schema. Output tail:{0}{1}", Environment.NewLine, migrate.Tail);
        var written = SecretKeyRingMaterial.Parse(NewSecret());
        var configuredSecret = NewSecret();
        await SeedKeyAsync(connectionString, WrappedKey(written));

        var run = await RefusedBootRun.RunAsync(Serve(connectionString, configuredSecret), TimeSpan.FromMinutes(4));

        run.Exited.Should().BeTrue("a refused boot must exit instead of serving. Output tail:{0}{1}", Environment.NewLine, run.Tail);
        run.ExitCode.Should().Be(
            StartupRefusal.ExitCode,
            "a ring wrapped with another secret must exit {0}, not abort (observed {1}). Output tail:{2}{3}",
            StartupRefusal.ExitCode,
            run.ExitCode,
            Environment.NewLine,
            run.Tail);
        run.Output.Should().Contain(OperationSecretKeyRingProtection.SecretKey)
            .And.Contain(written.Fingerprint)
            .And.Contain(SecretKeyRingMaterial.Parse(configuredSecret).Fingerprint)
            .And.NotContain(configuredSecret);
        run.Output.Should().NotContain("Unhandled exception");
        run.Output.Should().NotContain("Now listening on:");
        (await CountKeysAsync(connectionString)).Should().Be(1, "a refused boot must not add or replace keys");
    }

    private static PostgreSqlContainer NewDatabase()
        => new PostgreSqlBuilder()
            .WithImage(PostgisImage)
            .WithDatabase("honua_keyring_secret")
            .WithUsername("postgres")
            .WithPassword("keyring_secret_password")
            .WithLabel("honua.test.owner", "honua-server")
            .WithLabel("honua.test.run_id", Environment.GetEnvironmentVariable(TestRunIdEnv) ?? "manual")
            .Build();

    private static string ConnectionString(PostgreSqlContainer container)
        => new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Timeout = 60, CommandTimeout = 120 }.ConnectionString;

    private static ProcessStartInfo MigrateOnly(string connectionString)
    {
        var start = Serve(connectionString, NewSecret());
        start.Environment["HONUA_MIGRATE_ONLY"] = "true";
        return start;
    }

    private static ProcessStartInfo Serve(string connectionString, string secret)
    {
        var start = RefusedBootRun.ProductionServer(connectionString, InheritedKeysToRemove);
        start.Environment["Operations__SecretChannel__KeyRingSecret"] = secret;
        start.Environment["HONUA_ADMIN_PASSWORD"] = "KeyRingSecret-Refusal-1a";
        start.Environment["Security__ConnectionEncryption__MasterKey"] = "keyring-secret-refusal-connection-encryption-key";
        start.Environment["Security__ConnectionEncryption__Salt"] = "a2V5cmluZy1zZWNyZXQtcmVmdXNhbA==";
        return start;
    }

    private static XElement WrappedKey(SecretKeyRingMaterial material)
    {
        var repository = new MemoryKeyRepository();
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("Honua.Server")
            .AddKeyManagementOptions(options => options.XmlRepository = repository)
            .ProtectKeysWith(new OperationKeyRingProtector(null, material));
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        return repository.Elements.Should().ContainSingle().Subject;
    }

    private static async Task SeedKeyAsync(string connectionString, XElement element)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "INSERT INTO honua.data_protection_keys (friendly_name, element) VALUES ('seeded', @element);",
            connection);
        command.Parameters.AddWithValue("element", element.ToString(SaveOptions.DisableFormatting));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountKeysAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM honua.data_protection_keys;", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static string NewSecret()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class MemoryKeyRepository : IXmlRepository
    {
        public List<XElement> Elements { get; } = [];

        public IReadOnlyCollection<XElement> GetAllElements() => Elements.Select(element => new XElement(element)).ToArray();

        public void StoreElement(XElement element, string friendlyName) => Elements.Add(new XElement(element));
    }
}
