// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Operations.Services;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;
using Honua.Server.Tests.Infrastructure;
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

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class ProductionOperationPolicyHostTests
{
    private readonly DatabaseFixtureAdapter _database;

    public ProductionOperationPolicyHostTests(DatabaseFixtureAdapter database) => _database = database;

    private const string AdminPassword = "ProductionPolicyAdmin123!";
    private const string ConnectionEncryptionMasterKey = "ProductionPolicyConnectionMasterKey-123456";

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task ProductionHost_AdminWriteKeyIsAllowed_WriteKeyIsDenied()
    {
        using var factory = CreateProductionFactory(new Dictionary<string, string?>());
        var services = factory.Services;

        var canonical = services.GetRequiredService<IOperationPolicyDecisionPoint>()
            .Should().BeOfType<CanonicalOperationPolicyDecisionPoint>().Subject;
        canonical.TypedPolicy.Describe().Should().Be("0 operator rules + 2 built-in rules, default Deny");

        var keys = services.GetRequiredService<IAdminApiKeyStore>();
        var adminWrite = await keys.CreateAsync("policy-admin-write", ["admin:write"], null, "test", CancellationToken.None);
        var approver = await keys.CreateAsync("policy-approver", ["admin:approve"], null, "test", CancellationToken.None);
        var layerWrite = await keys.CreateAsync("policy-layer-write", ["write:*"], null, "test", CancellationToken.None);

        // admin:write confers the full "admin" role, so the full-administrator built-in admits it.
        var allowed = await EvaluateAsKeyAsync(services, adminWrite.Key);
        allowed.Kind.Should().Be(PolicyDecisionKind.Allow);
        allowed.Reason.Should().Be(OperationPolicyBuiltInRules.FullAdministratorReason);

        // A narrower administrative grant carries "scoped-admin-key" and the second built-in.
        var scoped = await EvaluateAsKeyAsync(services, approver.Key);
        scoped.Kind.Should().Be(PolicyDecisionKind.Allow);
        scoped.Reason.Should().Be(OperationPolicyBuiltInRules.AdministrativeKeyReason);

        var denied = await EvaluateAsKeyAsync(services, layerWrite.Key);
        denied.Kind.Should().Be(PolicyDecisionKind.Deny);
        denied.Reason.Should().Be("No policy rule matched this caller; Production denies by default.");

        // Over HTTP the layer write key is refused by the operation surface before policy.
        using var client = factory.CreateClient();
        using var submit = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/operations/{ServicePublishOperation.OperationId}/submit")
        {
            Content = JsonContent.Create(new { parameters = new Dictionary<string, string>() }),
        };
        submit.Headers.Add("X-API-Key", layerWrite.Key);
        (await client.SendAsync(submit)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/operations")]
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

        // The key itself is still admitted to the operations surface; only the policy denies.
        using var client = factory.CreateClient();
        using var list = new HttpRequestMessage(HttpMethod.Get, "/api/v1/operations");
        list.Headers.Add("X-API-Key", adminWrite.Key);
        (await client.SendAsync(list)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // One policy decides every agent change. Walk the shipped catalog so a new
    // state-changing operation cannot let an administrative agent act without one human approval.
    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task ProductionHost_EveryStateChangingOperation_AgentNeedsHumanApproval()
    {
        using var factory = CreateProductionFactory(new Dictionary<string, string?>());
        await using var scope = factory.Services.CreateAsyncScope();
        var pdp = scope.ServiceProvider.GetRequiredService<IOperationPolicyDecisionPoint>();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IOperationCatalog>().GetSnapshotAsync();
        var changes = snapshot.Operations
            .Where(static descriptor => descriptor.Policy.SideEffectClass != OperationSideEffectClass.ReadOnly)
            .ToArray();
        changes.Should().Contain(static descriptor => descriptor.OperationId.StartsWith("admin.", StringComparison.Ordinal));

        var (direct, unmappable) = await AuditAgentPolicyAndMappersAsync(
            pdp,
            changes,
            OperationDescriptorPublication.CountMappings(
                scope.ServiceProvider.GetServices<IOperationApprovalRequestMapper>()));

        direct.Should().BeEmpty("an agent's change is a proposal unless explicitly approved for agents");
        unmappable.Should().BeEmpty("every agent-gated operation must have exactly one durable replay mapper so it can be filed as a proposal");
        var publish = changes.Single(static descriptor => descriptor.OperationId == ServicePublishOperation.OperationId);
        (await pdp.EvaluateAsync(
                publish,
                new OperationRequest { OperationId = publish.OperationId },
                new OperationPolicyContext { Roles = [OperationPolicyBuiltInRules.FullAdministratorRole] }))
            .Kind.Should().Be(PolicyDecisionKind.Allow, "a human administrator keeps the built-in allow");
    }

    private static async Task<(List<string> Direct, List<string> Unmappable)> AuditAgentPolicyAndMappersAsync(
        IOperationPolicyDecisionPoint pdp,
        IReadOnlyList<IOperationDescriptor> changes,
        IReadOnlyDictionary<string, int> mapperCounts)
    {
        var direct = new List<string>();
        var unmappable = new List<string>();
        foreach (var descriptor in changes)
        {
            var decision = await pdp.EvaluateAsync(
                descriptor,
                new OperationRequest { OperationId = descriptor.OperationId },
                new OperationPolicyContext
                {
                    PrincipalId = "agent-catalog-walk",
                    Roles = [AgentApprovalRule.AgentRole, OperationPolicyBuiltInRules.FullAdministratorRole],
                });
            if (decision.Kind is not (PolicyDecisionKind.RequireApproval or PolicyDecisionKind.Deny))
            {
                direct.Add($"{descriptor.OperationId}: {decision.Kind}");
            }
            else if (decision.Kind == PolicyDecisionKind.RequireApproval &&
                     !descriptor.IsCompatibilityOnly &&
                     (!mapperCounts.TryGetValue(descriptor.OperationId, out var count) || count != 1))
            {
                unmappable.Add($"{descriptor.OperationId}: mappers={count}");
            }
        }

        return (direct, unmappable);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task ProductionHost_AgentKeySubmittingMetadataPrevalidate_FilesApprovalProposal()
    {
        using var factory = CreateProductionFactory(new Dictionary<string, string?>());
        var keys = factory.Services.GetRequiredService<IAdminApiKeyStore>();
        var agentKey = await keys.CreateAsync(
            "policy-agent-prevalidate",
            ["agent", "admin:write"],
            null,
            "test",
            CancellationToken.None);

        using var client = factory.CreateClient();
        using var submit = new HttpRequestMessage(HttpMethod.Post, "/api/v1/operations/admin.metadata.prevalidate/submit")
        {
            Content = JsonContent.Create(new
            {
                parameters = new Dictionary<string, string>
                {
                    ["releasePackageId"] = Guid.NewGuid().ToString("D"),
                    ["targetEnvironment"] = "staging",
                },
            }),
        };
        submit.Headers.Add("X-API-Key", agentKey.Key);

        using var response = await client.SendAsync(submit);
        var body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue("agent submission should file a proposal instead of failing: {0}", body);
        using var json = JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");
        data.GetProperty("status").GetString().Should().Be("RequiresApproval");
        data.GetProperty("proposalId").GetString().Should().NotBeNullOrWhiteSpace();
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

    private WebApplicationFactory<Program> CreateProductionFactory(IDictionary<string, string?> settings)
        => TestWebApplicationFactory.CreateForEnvironment("Production")
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    var values = new Dictionary<string, string?>(settings)
                    {
                        ["ConnectionStrings:DefaultConnection"] = _database.ConnectionString,
                        ["ConnectionStrings:honua"] = _database.ConnectionString,
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
                    services.AddSingleton(_ => NpgsqlDataSource.Create(_database.ConnectionString));
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
