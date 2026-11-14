// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Attachments.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Policy;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Db.Postgres.Features.Attachments;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using CoreSslMode = Honua.Core.Features.Security.Domain.SslMode;

namespace Honua.Server.Tests.Features.Admin;

// An agent's access-policy change and layer publish are filed as proposals, a person approves
// them in an interactive Console session, and the server replays the original admin request over
// its loopback listener. The agent may be an API key with the agent grant on the REST operations
// API, or any credential on MCP: an operator key presented as a bearer and an OIDC administrator
// token are both agents there. The host has no Redis.
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApprovalManagement)]
public sealed class AgentProposalReplayIntegrationTests : IAsyncLifetime
{
    private const string Issuer = AdminAuthEndpointsTests.TestOidcIssuer;
    private const string Audience = AdminAuthEndpointsTests.TestOidcAudience;
    private const string SigningKey = "agent-proposal-replay-signing-key-at-least-32-characters";
    private const string OperatorBearerSigningKey = "agent-proposal-replay-operator-bearer-key-32-bytes";
    private const string Tenant = "public";
    private const string ServiceName = "test";

    private readonly WebAppFixture _fixture = NoRedisJobHost.Create();
    private readonly AdminAuthEndpointsTests.StubHttpClientFactory _identity = AdminAuthEndpointsTests.CreateStubFactory();
    private HumanApprovalHttpClient _approver = null!;
    private IOperationProposalStore _proposals = null!;

    public async Task InitializeAsync()
    {
        HumanApprovalHttpClient.ConfigureKestrel(_fixture, _identity).ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("Oidc:Enabled", "true");
            builder.UseSetting("Oidc:RequireHttps", "true");
            builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", SigningKey);
            builder.UseSetting("Oidc:TokenValidation:EnableTokenReplayProtection", "false");
            builder.UseSetting("Oidc:Generic:Enabled", "true");
            builder.UseSetting("Oidc:Generic:Authority", Issuer);
            builder.UseSetting("Oidc:Generic:ClientId", Audience);
            builder.UseSetting("Oidc:Generic:DisplayName", "Test IdP");
            builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
            builder.UseSetting("Authentication:OperatorBearer:SigningKey", OperatorBearerSigningKey);
        }).ConfigureServices(services =>
        {
            // An installed database already carries its migrations, and the fixture schema is installed
            // at start. Running them again inside the replayed publish would take DDL locks while the
            // approval request holds its own; migrations are not what these tests cover.
            services.RemoveAll<IDatabaseMigrationRunner>();
            services.AddSingleton<IDatabaseMigrationRunner, AlreadyMigratedRunner>();
            // Published layers keep their attachment metadata in the canonical honua schema, the same
            // authority the layer publishing tests use.
            services.RemoveAll<PostgresAttachmentStore>();
            services.AddScoped(provider => new PostgresAttachmentStore(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(),
                provider.GetService<ICloudFileStorage>(),
                provider.GetRequiredService<ILogger<PostgresAttachmentStore>>(),
                schemaName: "honua",
                orphanLedger: provider.GetRequiredService<IAttachmentOrphanLedger>(),
                attachmentLimits: provider.GetService<IOptions<LimitsOptions>>()?.Value.Attachments ?? new AttachmentLimits(),
                migrationContract: Honua.Server.Startup.ServerCoreSchemaMigrations.Manifest.Attachments!));
        });
        await NoRedisJobHost.StartAsync(_fixture);
        await ServerTestData.EnsureFixtureAttachmentsAsync(_fixture.Postgres, "honua");
        _proposals = _fixture.Services.GetRequiredService<IOperationProposalStore>();
        UnavailableOperationProposalStore.IsDurable(_proposals).Should().BeTrue("approval replay needs the durable store");
        _approver = await HumanApprovalHttpClient.AuthenticateAsync(_fixture, _identity, "agent-change-approver", Tenant);
    }

    public async Task DisposeAsync()
    {
        _approver.Dispose();
        using var identity = _identity;
        await _fixture.DisposeAsync();
    }

    [IntegrationTheory]
    [InlineData("agent-key-rest")]
    [InlineData("operator-bearer-mcp")]
    [InlineData("oidc-admin-mcp")]
    [Protocol(TestProtocols.Admin, TestProtocols.Mcp)]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [Endpoint("POST /mcp tools/call honua_admin_services_access_policy_set")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/access-policy")]
    public async Task AccessPolicyChange_AgentProposalApprovedByPerson_ReplaysThroughLoopback(string caller)
    {
        var before = await AllowAnonymousAsync();
        // The service has several publications; pick the value that changes at least one of them.
        var target = !before.All(static value => value);
        using var agent = await CreateCallerAsync(caller);

        var proposalId = caller == "agent-key-rest"
            ? await SubmitRestAsync(agent, "admin.services.access-policy.set", new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["serviceName"] = ServiceName,
                ["allowAnonymous"] = target ? "true" : "false",
            })
            : await SubmitMcpAsync(agent, "honua_admin_services_access_policy_set", new { serviceName = ServiceName, allowAnonymous = target });

        (await AllowAnonymousAsync()).Should().Equal(before, "an agent's change waits for a person");
        await ApproveAndAssertSucceededAsync(proposalId);
        (await AllowAnonymousAsync()).Should().OnlyContain(value => value == target, "the approved request was replayed");
        await AssertReplayedWithServerMintedCredentialAsync(proposalId);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Admin, TestProtocols.Mcp)]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_admin_layer_publish")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task LayerPublish_AgentProposalApprovedByPerson_ReplaysThroughLoopback()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var table = $"agent_replay_{suffix}";
        var service = $"agent_replay_{suffix}";
        var connectionId = await CreateConnectionAsync($"agent-replay-{suffix}");
        await _fixture.Postgres.ExecuteDdlUnderLockAsync($"""
            CREATE TABLE public.{table} (
                id SERIAL PRIMARY KEY,
                name TEXT NOT NULL,
                geom geometry(Point, 4326) NOT NULL
            );
            INSERT INTO public.{table} (name, geom) VALUES ('Replayed', ST_SetSRID(ST_Point(1, 1), 4326));
            """);
        try
        {
            using var agent = await CreateCallerAsync("operator-bearer-mcp");
            var proposalId = await SubmitMcpAsync(agent, "honua_admin_layer_publish", new
            {
                connectionId = connectionId.ToString("D"),
                schema = "public",
                table,
                layerName = "Agent replay",
                serviceName = service,
                geometryColumn = "geom",
                geometryType = "Point",
                srid = 4326,
                primaryKey = "id",
                fields = new[] { "id", "name" },
            });

            (await PublishedLayerNamesAsync(connectionId, service)).Should().BeEmpty("an agent's publish waits for a person");
            await ApproveAndAssertSucceededAsync(proposalId);
            (await PublishedLayerNamesAsync(connectionId, service)).Should().ContainSingle()
                .Which.Should().Be("Agent replay", "the approved publish was replayed");
            await AssertReplayedWithServerMintedCredentialAsync(proposalId);
        }
        finally
        {
            // Publishing also writes the global compatibility catalog; remove this service before
            // the connection so the next seed is not blocked by its foreign key.
            await _fixture.Postgres.ApplyGlobalSeedSqlAsync(
                "DELETE FROM honua.services WHERE service_name = @serviceName;",
                command => command.Parameters.AddWithValue("serviceName", service));
            using (var scope = _fixture.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>().DeleteConnectionAsync(connectionId);
            }

            await _fixture.Postgres.ExecuteDdlUnderLockAsync($"DROP TABLE IF EXISTS public.{table};");
        }
    }

    private async Task ApproveAndAssertSucceededAsync(string proposalId)
    {
        using var response = await _approver.ApproveAsync(proposalId);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var proposal = (await _proposals.GetAsync(proposalId, CancellationToken.None))!;
        var instance = proposal.Audit.OperationInstanceId is { } instanceId
            ? await _fixture.Services.GetRequiredService<IOperationInstanceStore>().GetAsync(instanceId)
            : null;
        proposal.Status.Should().Be(OperationProposalStatus.Succeeded,
            "{0} {1}", string.Join("; ", proposal.Plan.BlockingReasons), JsonSerializer.Serialize(instance?.Result?.Details));
        proposal.ResolvedBy.Should().Contain("agent-change-approver");
    }

    // The replay runs as a key minted for this proposal, never as the agent's credential.
    private async Task AssertReplayedWithServerMintedCredentialAsync(string proposalId)
    {
        using var scope = _fixture.Services.CreateScope();
        var keys = await scope.ServiceProvider.GetRequiredService<IAdminApiKeyStore>().ListAsync(CancellationToken.None);
        var minted = keys.Should().ContainSingle(key => key.Name == $"approved-operation:{proposalId}").Subject;
        minted.RevokedAt.Should().NotBeNull("the replay credential is revoked once the request completes");
    }

    private async Task<string> SubmitRestAsync(HttpClient agent, string operationId, Dictionary<string, string?> parameters)
    {
        using var response = await agent.PostAsJsonAsync($"/api/v1/operations/{operationId}/submit",
            new { serviceName = ServiceName, parameters });
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        return await AssertAwaitingApprovalAsync(document.RootElement.GetProperty("data"), operationId);
    }

    private async Task<string> SubmitMcpAsync(HttpClient agent, string tool, object arguments)
    {
        var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments } });
        using var content = new StringContent(request, Encoding.UTF8, "application/json");
        using var response = await agent.PostAsync("/mcp", content);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(payload);
        return await AssertAwaitingApprovalAsync(document.RootElement.GetProperty("result").GetProperty("structuredContent"), operationId: null);
    }

    private async Task<string> AssertAwaitingApprovalAsync(JsonElement handle, string? operationId)
    {
        var raw = handle.GetRawText();
        handle.GetProperty("status").GetString().Should().Be("RequiresApproval", raw);
        // No operator policy is configured, so only the built-in agent rule can route this to approval.
        handle.GetProperty("approvalLane").GetString().Should().Be(AgentApprovalRule.ApprovalLane, raw);
        var proposalId = handle.GetProperty("proposalId").GetString();
        proposalId.Should().NotBeNullOrWhiteSpace(raw);
        var proposal = (await _proposals.GetAsync(proposalId!, CancellationToken.None))!;
        proposal.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        if (operationId is not null) proposal.OperationId.Should().Be(operationId);
        return proposalId!;
    }

    private async Task<HttpClient> CreateCallerAsync(string caller) => caller switch
    {
        "agent-key-rest" => ApiKeyClient(await CreateKeyAsync(["admin:read", "admin:write", AgentPrincipal.AgentGrant])),
        // A person's own Console session exchanged for an operator bearer: on MCP it is still an agent.
        "operator-bearer-mcp" => BearerClient(await IssueOperatorBearerAsync()),
        "oidc-admin-mcp" => BearerClient(CreateAdminToken()),
        _ => throw new ArgumentOutOfRangeException(nameof(caller), caller, null),
    };

    private HttpClient ApiKeyClient(string key)
        => _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key));

    private HttpClient BearerClient(string token)
        => _fixture.CreateClient(client => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token));

    private async Task<string> IssueOperatorBearerAsync()
    {
        using var operatorSession = await HumanApprovalHttpClient.AuthenticateAsync(
            _fixture, _identity, "operator-bearer-holder", Tenant);
        using var response = await operatorSession.Client.PostAsync("/api/v1/admin/auth/bearer", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var issued = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminOperatorBearerResponse);
        return issued!.AccessToken;
    }

    private async Task<string> CreateKeyAsync(IReadOnlyList<string> permissions)
    {
        using var scope = _fixture.Services.CreateScope();
        var issued = await scope.ServiceProvider.GetRequiredService<IAdminApiKeyStore>().CreateAsync(
            $"agent-replay-{Guid.NewGuid():N}", permissions, DateTimeOffset.UtcNow.AddHours(1), "test", CancellationToken.None);
        return issued.Key;
    }

    private static string CreateAdminToken()
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim("sub", "oidc-agent-administrator"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
                new Claim("tid", Tenant),
                new Claim("roles", "admin"),
                new Claim("scope", OperatorScopeCatalog.Full),
            ],
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<bool[]> AllowAnonymousAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IMetadataV2GraphStore>().GetCurrentAsync();
        var services = snapshot.Graph.Services.Where(service => service.Metadata.Name == ServiceName).ToArray();
        services.Should().NotBeEmpty();
        return services.Select(service => service.AccessPolicy?.AllowAnonymous ?? false).ToArray();
    }

    private async Task<string[]> PublishedLayerNamesAsync(Guid connectionId, string service)
    {
        using var admin = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword));
        using var response = await admin.GetAsync($"/api/v1/admin/connections/{connectionId:D}/layers?serviceName={service}");
        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        var items = data.ValueKind == JsonValueKind.Array ? data : data.GetProperty("items");
        return items.EnumerateArray()
            .Where(layer => layer.TryGetProperty("serviceName", out var name) && name.GetString() == service)
            .Select(layer => layer.GetProperty("layerName").GetString()!)
            .ToArray();
    }

    private async Task<Guid> CreateConnectionAsync(string name)
    {
        using var scope = _fixture.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ISecureConnectionRegistry>();
        var encryption = scope.ServiceProvider.GetRequiredService<IConnectionEncryptionService>();
        // The fixture schema holds the server's canonical features table; the source table is in public.
        var builder = new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            SearchPath = $"{_fixture.CurrentSchema},public",
        };
        var connection = DataConnection.CreateWithEncryptedCredentials(
            name: name,
            host: builder.Host!,
            port: builder.Port,
            databaseName: builder.Database!,
            username: builder.Username!,
            encryptedConnectionString: await encryption.EncryptConnectionStringAsync(builder.ConnectionString),
            encryptionKeyVersion: await encryption.GetCurrentKeyVersionAsync(),
            createdBy: nameof(AgentProposalReplayIntegrationTests),
            description: "Agent proposal replay test connection",
            sslRequired: false,
            sslMode: CoreSslMode.Disable);
        return (await registry.CreateConnectionAsync(connection)).ConnectionId;
    }

    private sealed class AlreadyMigratedRunner : IDatabaseMigrationRunner
    {
        public Task<DatabaseMigrationPlan> PlanMigrationsAsync(
            string connectionString, Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationPlan.Succeeded());

        public Task<DatabaseMigrationResult> RunMigrationsAsync(
            string connectionString, Assembly migrationsAssembly, CancellationToken cancellationToken = default)
            => Task.FromResult(DatabaseMigrationResult.Succeeded());
    }
}
