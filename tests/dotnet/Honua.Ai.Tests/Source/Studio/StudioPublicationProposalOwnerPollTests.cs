// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Regression proof for honua-server#4910: the principal that proposed a Studio
/// publication can poll that proposal's status over MCP, and no other principal can.
/// </summary>
/// <remarks>
/// The propose side records the Studio owner key
/// (<c>subject:{iss}:{sub}@tenant:{tenant}</c> for an issuer-bearing subject, the bare
/// key id for an API key) while the read side compared the scheme-qualified canonical
/// actor id, so the owner's every poll was refused with <c>permission_denied</c>. The
/// bearer case deliberately mints an <b>issuer-bearing</b> token — an issuer-less test
/// identity collapses both encodings onto the same bare subject and cannot observe the
/// defect at all.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class StudioPublicationProposalOwnerPollTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";
    private const string Issuer = "https://idp.example.com";
    private const string Audience = "honua-mcp-client-id";
    private const string SigningKey = "studio-proposal-owner-test-signing-key-at-least-32-characters";
    private const string Tenant = "tenant-a";

    private readonly WebAppFixture _fixture = new();
    private readonly RedisFixture _redis = new();

    public async Task InitializeAsync()
    {
        await _redis.InitializeAsync();
        _fixture
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("ConnectionStrings:redis", _redis.ConnectionString);
                builder.UseSetting("Licensing:DevGrantEdition", "Pro");
                // Authentication must be decided by the presented credential only: the
                // dev-auth bypass would hand every request the same issuer-less identity.
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Studio:EndUserAuthorization:Enabled", "true");

                // Symmetric-key generic OIDC provider so issuer-bearing tokens are minted and
                // validated in-process without a live IdP (mirrors McpBearerAuthenticationTests).
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", SigningKey);
                builder.UseSetting("Oidc:TokenValidation:EnableTokenReplayProtection", "false");
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", Issuer);
                builder.UseSetting("Oidc:Generic:ClientId", Audience);
                builder.UseSetting("Oidc:Generic:DisplayName", "Test IdP");
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStudioPackageStore>();
                services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
                // The generic operator gate is admitted so the assertions isolate the
                // proposal-ownership decision rather than an operator-grant denial.
                services.RemoveAll<IOperatorAuthorizationEvaluator>();
                services.AddSingleton<IOperatorAuthorizationEvaluator, AllowAllOperatorAuthorizationEvaluator>();
            });
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_propose_publication")]
    [Endpoint("POST /mcp resources/read honua://proposals/{proposalId}")]
    public async Task IssuerBearingBearerProposer_PollsItsOwnProposal_AndAnotherSubjectIsRefused()
    {
        using var owner = CreateBearerClient(CreateToken("studio-owner-alice"));
        using var stranger = CreateBearerClient(CreateToken("studio-stranger-bob"));

        var proposalId = await ProposeAsync(owner, "bearer");

        var ownerRead = await RpcAsync(owner, ReadProposalRequest(proposalId));
        AssertProposalReadable(ownerRead, proposalId);

        var strangerRead = await RpcAsync(stranger, ReadProposalRequest(proposalId));
        AssertOwnershipRefusal(strangerRead);
    }

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_propose_publication")]
    [Endpoint("POST /mcp resources/read honua://proposals/{proposalId}")]
    public async Task ApiKeyProposer_PollsItsOwnProposal_AndAnotherKeyIsRefused()
    {
        var apiKeyStore = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var ownerKey = await apiKeyStore.CreateAsync(
            "proposal-owner-key", ["studio:enduser"], null, null, CancellationToken.None);
        var strangerKey = await apiKeyStore.CreateAsync(
            "proposal-stranger-key", ["studio:enduser"], null, null, CancellationToken.None);
        using var owner = _fixture.CreateClient(
            client => client.DefaultRequestHeaders.Add("X-API-Key", ownerKey.Key));
        using var stranger = _fixture.CreateClient(
            client => client.DefaultRequestHeaders.Add("X-API-Key", strangerKey.Key));

        var proposalId = await ProposeAsync(owner, "apikey");

        var ownerRead = await RpcAsync(owner, ReadProposalRequest(proposalId));
        AssertProposalReadable(ownerRead, proposalId);

        var strangerRead = await RpcAsync(stranger, ReadProposalRequest(proposalId));
        AssertOwnershipRefusal(strangerRead);
    }

    /// <summary>
    /// Composes, saves and proposes a publication as one principal, returning the proposal
    /// id the tool reports. Every step runs over MCP, as the SDK replay harness does.
    /// </summary>
    private static async Task<string> ProposeAsync(HttpClient client, string label)
    {
        var packageKey = $"proposal-owner-{label}-{Guid.NewGuid():N}";
        var created = await CallToolAsync(client, "honua_studio_create_draft",
            $$"""{"packageKey":"{{packageKey}}","family":"dashboard","schemaVersion":"1.0"}""");
        var draftId = created.GetProperty("draftId").GetGuid();

        var updated = await CallToolAsync(client, "honua_studio_update_draft",
            $$$"""{"body":{"layers":[{"id":"roads"}],"view":{"center":[-158,22],"zoom":7}},"draftId":"{{{draftId:D}}}","generation":1,"packageKey":"{{{packageKey}}}","schemaVersion":"1.0"}""");
        var generation = updated.GetProperty("generation").GetInt64();

        await CallToolAsync(client, "honua_studio_validate_draft", $$"""{"draftId":"{{draftId:D}}"}""");

        var saved = await CallToolAsync(client, "honua_studio_save_version",
            $$"""{"draftId":"{{draftId:D}}","generation":{{generation}}}""");
        var version = saved.GetProperty("version");

        var proposed = await CallToolAsync(client, "honua_studio_propose_publication",
            $$"""{"itemId":"{{version.GetProperty("itemId").GetGuid():D}}","versionId":"{{version.GetProperty("versionId").GetGuid():D}}","contentHash":"{{version.GetProperty("contentHash").GetString()}}","route":"/studio/{{packageKey}}","visibility":"personal"}""");
        proposed.GetProperty("status").GetString().Should().Be("AwaitingApproval");
        var proposalId = proposed.GetProperty("proposalId").GetString();
        proposalId.Should().NotBeNullOrWhiteSpace();
        return proposalId!;
    }

    private static string ReadProposalRequest(string proposalId) =>
        $$$"""{"jsonrpc":"2.0","id":"read","method":"resources/read","params":{"uri":"honua://proposals/{{{proposalId}}}"}}""";

    private static void AssertProposalReadable(JsonElement response, string proposalId)
    {
        var hasError = response.TryGetProperty("error", out var error);
        hasError.Should().BeFalse(
            $"the proposal's own proposer must be able to poll its status: {(hasError ? error.GetRawText() : "(none)")}");
        var contents = response.GetProperty("result").GetProperty("contents");
        contents.GetArrayLength().Should().Be(1);
        contents[0].GetProperty("uri").GetString().Should().Be($"honua://proposals/{proposalId}");
        using var body = JsonDocument.Parse(contents[0].GetProperty("text").GetString()!);
        body.RootElement.GetProperty("proposalId").GetString().Should().Be(proposalId);
        body.RootElement.GetProperty("status").GetString().Should().Be("AwaitingApproval");
    }

    private static void AssertOwnershipRefusal(JsonElement response)
    {
        response.TryGetProperty("error", out var error).Should().BeTrue(
            "a principal that did not propose the publication must not read it");
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("permission_denied");
        // The refusal discloses no proposal state.
        error.GetRawText().Should().NotContain("AwaitingApproval");
    }

    private HttpClient CreateBearerClient(string token)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string toolName, string argumentsJson)
    {
        var response = await RpcAsync(
            client,
            $$$"""{"jsonrpc":"2.0","id":"{{{toolName}}}","method":"tools/call","params":{"name":"{{{toolName}}}","arguments":{{{argumentsJson}}}}}""");
        var hasProtocolError = response.TryGetProperty("error", out var protocolError);
        hasProtocolError.Should().BeFalse(
            $"tool '{toolName}' should return a tool result: {(hasProtocolError ? protocolError.GetRawText() : "(none)")}");
        var result = response.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError))
        {
            isError.GetBoolean().Should().BeFalse($"tool '{toolName}' failed: {result.GetRawText()}");
        }

        return result.GetProperty("structuredContent").Clone();
    }

    private static async Task<JsonElement> RpcAsync(HttpClient client, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8) { Headers = { ContentType = new MediaTypeHeaderValue(JsonMediaType) } },
        };
        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private static string CreateToken(string subject)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim("sub", subject),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim("tid", Tenant),
                new Claim("scope", OperatorScopeCatalog.Full),
            ],
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class AllowAllOperatorAuthorizationEvaluator : IOperatorAuthorizationEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessDecision.Allowed("proposal-owner fixture"));
    }
}
