// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Honua.Server.Tests.Features.OperationsToolset;

/// <summary>
/// Owner decision 10 (2026-10-10): a Production host composed from the shipped
/// <c>appsettings.Production.json</c> lets an administrative key execute typed operations through
/// the built-in rule, without operator-authored rules, while a layer write key still hits the
/// fail-closed default. Reproduces the honua-release run 38066103745 denial of
/// <c>service.publish</c> for an <c>admin:write</c> key.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ProductionOperationPolicyHostTests
{
    private const string AdminPassword = "ProductionPolicyAdmin123!";
    private const string ConnectionEncryptionMasterKey = "ProductionPolicyConnectionMasterKey-123456";

    [IntegrationTest]
    public async Task ProductionHost_AdminWriteKeyIsAllowed_WriteKeyIsDenied()
    {
        using var factory = CreateProductionFactory(new Dictionary<string, string?>());
        var services = factory.Services;

        var canonical = services.GetRequiredService<IOperationPolicyDecisionPoint>()
            .Should().BeOfType<CanonicalOperationPolicyDecisionPoint>().Subject;
        canonical.TypedPolicy.Describe().Should().Be("0 operator rules + 2 built-in rules, default Deny");

        var keys = services.GetRequiredService<IAdminApiKeyStore>();
        var adminWrite = await keys.CreateAsync("policy-admin-write", ["admin:write"], null, "test", CancellationToken.None);
        var layerWrite = await keys.CreateAsync("policy-layer-write", ["write:*"], null, "test", CancellationToken.None);

        var allowed = await EvaluateAsKeyAsync(services, adminWrite.Key);
        allowed.Kind.Should().Be(PolicyDecisionKind.Allow);
        allowed.Reason.Should().Be(OperationPolicyBuiltInRules.AdministrativeKeyReason);

        var denied = await EvaluateAsKeyAsync(services, layerWrite.Key);
        denied.Kind.Should().Be(PolicyDecisionKind.Deny);
        denied.Reason.Should().Be("No policy rule matched this caller; Production denies by default.");
    }

    [IntegrationTest]
    public async Task ProductionHost_BuiltInOptOut_DeniesAdminWriteKey()
    {
        using var factory = CreateProductionFactory(new Dictionary<string, string?>
        {
            ["Operations:Policy:BuiltInProductionRules"] = "false",
        });
        var services = factory.Services;

        var keys = services.GetRequiredService<IAdminApiKeyStore>();
        var adminWrite = await keys.CreateAsync("policy-admin-write", ["admin:write"], null, "test", CancellationToken.None);

        var decision = await EvaluateAsKeyAsync(services, adminWrite.Key);
        decision.Kind.Should().Be(PolicyDecisionKind.Deny);
    }

    private static async Task<PolicyDecision> EvaluateAsKeyAsync(IServiceProvider services, string apiKey)
    {
        await using var scope = services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Headers["X-API-Key"] = apiKey;
        var authentication = await http.AuthenticateAsync(AuthenticationExtensions.ApiKeyScheme);
        authentication.Succeeded.Should().BeTrue(authentication.Failure?.Message);

        var descriptor = await scope.ServiceProvider.GetRequiredService<IOperationCatalog>()
            .GetDescriptorAsync(ServicePublishOperation.OperationId);
        descriptor.Should().NotBeNull();

        return await scope.ServiceProvider.GetRequiredService<IOperationPolicyDecisionPoint>().EvaluateAsync(
            descriptor!,
            new OperationRequest { OperationId = ServicePublishOperation.OperationId },
            new OperationPolicyContext
            {
                Roles = authentication.Principal!.FindAll(ClaimTypes.Role).Select(static claim => claim.Value).ToArray(),
            });
    }

    private static WebApplicationFactory<Program> CreateProductionFactory(IDictionary<string, string?> settings)
        => TestWebApplicationFactory.CreateForEnvironment("Production")
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    var values = new Dictionary<string, string?>(settings)
                    {
                        ["ConnectionStrings:DefaultConnection"] = TestConnectionStrings.DefaultPostgresConnectionString,
                        ["ConnectionStrings:honua"] = TestConnectionStrings.DefaultPostgresConnectionString,
                        ["Security:ConnectionEncryption:MasterKey"] = ConnectionEncryptionMasterKey,
                        ["HONUA_ADMIN_PASSWORD"] = AdminPassword,
                    };
                    configuration.AddInMemoryCollection(values);
                });
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IDatabaseCompatibilityChecker>();
                    services.AddSingleton<IDatabaseCompatibilityChecker, AlwaysCompatibleDatabaseCompatibilityChecker>();
                    services.RemoveAll<NpgsqlDataSource>();
                    services.AddSingleton(_ => NpgsqlDataSource.Create(TestConnectionStrings.DefaultPostgresConnectionString));
                });
            });

    private sealed class AlwaysCompatibleDatabaseCompatibilityChecker : IDatabaseCompatibilityChecker
    {
        public Task<DatabaseCompatibilityResult> CheckCompatibilityAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseCompatibilityResult.Compatible(
                "PostgreSQL test",
                "3.6.2",
                "3.6.2",
                ["postgis", "postgis_raster"]));
    }
}
