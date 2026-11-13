// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Honua.Server.Tests.Features.AgentWorkspace;

[Collection("Database")]
[Protocol(TestProtocols.Studio)]
[Operation(Operations.StudioLifecycle)]
public sealed class LocalProcessWorkspaceEndpointTests : IAsyncLifetime
{
    private const string Issuer = "https://idp.example.com";
    private const string Audience = "honua-mcp-client-id";
    private const string SigningKey = "local-process-human-owner-test-signing-key-at-least-32-characters";
    private readonly WebAppFixture _fixture = new();
    private readonly SandboxHandler _sandbox = new();

    public async Task InitializeAsync()
    {
        _fixture.ConfigureWebHost(ConfigureHost).ConfigureServices(services =>
        {
            services.RemoveAll<JobStoreDatabase>();
            services.AddSingleton(provider => new JobStoreDatabase(provider.GetRequiredService<NpgsqlDataSource>(), _fixture.CurrentSchema));
            services.RemoveAll<IOperatorAuthorizationEvaluator>();
            services.AddSingleton<IOperatorAuthorizationEvaluator, AllowOperator>();
            services.RemoveAll<ILocalProcessWorkspaceGateway>();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LocalProcesses:WorkspaceUrl"] = "http://127.0.0.1:8090",
                ["HONUA_GP_WORKSPACE_KEY"] = new string('k', 32)
            }).Build();
            services.AddSingleton<ILocalProcessWorkspaceGateway>(new LocalProcessWorkspaceGateway(new HttpClient(_sandbox), configuration));
        });
        await _fixture.InitializeAsync();
        await using var stream = typeof(Program).Assembly.GetManifestResourceStream("Honua.Server.Migrations.155_CreateAgentWorkspaceIdentities.sql")!;
        using var reader = new StreamReader(stream);
        var ddl = (await reader.ReadToEndAsync()).Replace("$HonuaSchema$", $"\"{_fixture.CurrentSchema}\"", StringComparison.Ordinal);
        await _fixture.Postgres.ExecuteDdlUnderLockAsync(ddl);
    }

    private static void ConfigureHost(IWebHostBuilder builder)
    {
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("Studio:EndUserAuthorization:Enabled", "true");
        builder.UseSetting("Studio:Session:EnvironmentId", "local-process-test");
        builder.UseSetting("Oidc:Enabled", "true");
        builder.UseSetting("Oidc:RequireHttps", "true");
        builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", SigningKey);
        builder.UseSetting("Oidc:TokenValidation:EnableTokenReplayProtection", "false");
        builder.UseSetting("Oidc:Generic:Enabled", "true");
        builder.UseSetting("Oidc:Generic:Authority", Issuer);
        builder.UseSetting("Oidc:Generic:ClientId", Audience);
        builder.UseSetting("Oidc:Generic:DisplayName", "Test IdP");
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/agent/workspaces")]
    public async Task CreateWorkspace_Anonymous_IsUnauthorized()
    {
        using var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Clear();
        using var response = await client.PostAsync("/api/v1/agent/workspaces", null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/agent/workspaces")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}")]
    public async Task Workspace_PersistsServerIdentity_AndRejectsOtherOwnersTenantsAndTargets()
    {
        using var owner = Client("alice", "tenant-a");
        var workspace = await CreateWorkspace(owner);
        workspace.OwnerActorId.Should().Contain("alice");
        workspace.TargetId.Should().Be("local-process-test");
        using var read = await owner.GetAsync($"/api/v1/agent/workspaces/{workspace.WorkspaceId}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        var store = _fixture.Services.GetRequiredService<AgentWorkspaceIdentityStore>();
        (await store.GetAsync(workspace.WorkspaceId, CancellationToken.None)).Should().Be(workspace);
        using var otherOwner = Client("bob", "tenant-a");
        using var otherTenant = Client("alice", "tenant-b");
        using var deniedOwner = await otherOwner.GetAsync($"/api/v1/agent/workspaces/{workspace.WorkspaceId}");
        using var deniedTenant = await otherTenant.GetAsync($"/api/v1/agent/workspaces/{workspace.WorkspaceId}");
        deniedOwner.StatusCode.Should().Be(HttpStatusCode.NotFound);
        deniedTenant.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var foreignTarget = workspace with { WorkspaceId = Guid.NewGuid().ToString("N"), TargetId = "another-environment" };
        await store.CreateAsync(foreignTarget, CancellationToken.None);
        using var deniedTarget = await owner.GetAsync($"/api/v1/agent/workspaces/{foreignTarget.WorkspaceId}");
        deniedTarget.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/agent/workspaces")]
    public async Task CreateWorkspace_MachineCredential_IsForbidden()
    {
        using var machine = Client("machine", "tenant-a", false);
        using var response = await machine.PostAsync("/api/v1/agent/workspaces", null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        _sandbox.Calls.Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/agent/workspaces")]
    public async Task CreateWorkspace_BrowserCookieWithoutCsrf_IsRejected()
    {
        using var owner = Client("alice", "tenant-a");
        owner.DefaultRequestHeaders.Add("Cookie", "honua_admin_session=untrusted-browser-cookie");
        using var response = await owner.PostAsync("/api/v1/agent/workspaces", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _sandbox.Calls.Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/project/identity")]
    public async Task LocalProcess_Owner_UsesStoredIdentityWithoutForwardingBearer()
    {
        using var owner = Client("alice", "tenant-a");
        var workspace = await CreateWorkspace(owner);
        using var response = await owner.GetAsync($"/api/v1/agent/workspaces/{workspace.WorkspaceId}/processes/local/project/identity");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _sandbox.Calls.Should().Be(1);
        _sandbox.Workspace.Should().Be(workspace.WorkspaceId);
        _sandbox.Owner.Should().Be(workspace.OwnerActorId);
        _sandbox.Authorization.Should().BeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/project/identity")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/processes/local/project/bind")]
    [Endpoint("DELETE /api/v1/agent/workspaces/{workspaceId}/processes/local/project/bindings/{bindingId}")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/project")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/sources/{**path}")]
    [Endpoint("PUT /api/v1/agent/workspaces/{workspaceId}/processes/local/sources/{**path}")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/processes/local/snapshots")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/snapshots/{snapshotId}")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/snapshots/{snapshotId}/sources/{**path}")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/processes/local/runs")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/runs")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/runs/{runId}")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/processes/local/runs/{runId}/cancel")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/commands/{commandId}")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/processes/local/runs/{runId}/artifacts/{**path}")]
    public async Task LocalProcess_AllOperations_RejectNonOwnerBeforeContactingSandbox()
    {
        using var owner = Client("alice", "tenant-a");
        var workspace = await CreateWorkspace(owner);
        using var stranger = Client("bob", "tenant-a");
        var prefix = $"/api/v1/agent/workspaces/{workspace.WorkspaceId}/processes/local/";
        var routes = new[]
        {
            ("GET", "project/identity"), ("POST", "project/bind"), ("DELETE", $"project/bindings/{workspace.WorkspaceId}"),
            ("GET", "project"), ("GET", "sources/main.py"), ("PUT", "sources/main.py"), ("POST", "snapshots"),
            ("GET", "snapshots/" + new string('a', 32)), ("GET", $"snapshots/{new string('a', 32)}/sources/main.py"),
            ("POST", "runs"), ("GET", "runs"), ("GET", "runs/" + new string('b', 32)),
            ("POST", $"runs/{new string('b', 32)}/cancel"), ("GET", "commands/command-1"),
            ("GET", $"runs/{new string('b', 32)}/artifacts/result.txt")
        };
        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), prefix + path);
            if (method is "POST" or "PUT")
                request.Content = new StringContent("""{"projectId":"project","expectedRevision":1,"content":"source","sourceDigest":"digest","snapshotId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","snapshotDigest":"digest","commandId":"command-1"}""", Encoding.UTF8, "application/json");
            using var response = await stranger.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, "{0} {1} must check the stored owner", method, path);
        }
        _sandbox.Calls.Should().Be(0);
    }

    private async Task<AgentWorkspaceIdentity> CreateWorkspace(HttpClient client)
    {
        using var response = await client.PostAsync("/api/v1/agent/workspaces", null);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(), LocalProcessJsonContext.Default.AgentWorkspaceIdentity)!;
    }

    private HttpClient Client(string subject, string tenant, bool human = true)
    {
        var claims = new List<Claim>
        {
            new("sub", subject), new("tid", tenant), new("roles", "admin"), new("scope", OperatorScopeCatalog.Full),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        claims.Add(new Claim(human ? "amr" : "grant_type", human ? "mfa" : "client_credentials"));
        var token = new JwtSecurityToken(Issuer, Audience, claims, expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256));
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private sealed class AllowOperator : IOperatorAuthorizationEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(ClaimsPrincipal principal, OperatorAuthorizationRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(AccessDecision.Allowed("workspace boundary fixture"));
    }

    private sealed class SandboxHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Workspace { get; private set; }
        public string? Owner { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Workspace = request.Headers.GetValues("X-Honua-Workspace-Id").Single();
            Owner = request.Headers.GetValues("X-Honua-Workspace-Owner-Id").Single();
            Authorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"projectId\":\"project\",\"revision\":1,\"sourceDigest\":\"digest\",\"image\":\"python\",\"binding\":null}", Encoding.UTF8, "application/json")
            });
        }
    }
}
