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
using Honua.Core.Features.ControlPlane.Abstractions;
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

namespace Honua.Server.Tests.Features.Studio;

/// <summary>
/// Regression proof for honua-server#4901: the principal that proposed a Studio publication
/// cannot approve it, and a different authorized principal in the same tenant can.
/// </summary>
/// <remarks>
/// The Studio propose path records the Studio owner key
/// (<c>subject:{iss}:{sub}@tenant:{tenant}</c>) as the requester, while the approval
/// endpoint's separation-of-duties check compared the raw <c>sub</c> claim, so the two never
/// matched and the proposer's own approval executed the publication. The tokens are
/// deliberately <b>issuer-bearing</b>: an issuer-less identity collapses both encodings onto
/// the bare subject and cannot observe the defect. The propose token omits the admin role
/// because an admin-role call publishes in the same request; the admin-role token of that
/// same subject is what attempts approval and must still be forbidden.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class StudioPublicationSelfApprovalTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";
    private const string Issuer = "https://idp.example.com";
    private const string Audience = "honua-mcp-client-id";
    private const string SigningKey = "studio-publication-self-approval-signing-key-at-least-32-chars";
    private const string Tenant = "tenant-a";
    private const string SeparationOfDuties = "Separation of duties";

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
                // Authentication must be decided by the presented credential only: the dev-auth
                // bypass would hand the proposer and the approver the same identity.
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Studio:EndUserAuthorization:Enabled", "true");

                // Symmetric-key generic OIDC provider so issuer-bearing tokens are minted and
                // validated in-process without a live IdP.
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
                // separation-of-duties decision rather than an operator-grant denial.
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
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_studio_propose_publication")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [Endpoint("GET /api/v1/admin/proposals/{id}")]
    public async Task ApproveProposal_StudioPublicationByItsOwnIssuerBearingProposer_IsForbidden()
    {
        const string subject = "studio-proposer-alice";
        using var proposer = CreateBearerClient(CreateToken(subject));
        using var sameAdmin = CreateBearerClient(CreateToken(subject, administrator: true));

        var proposalId = await ProposeAsync(proposer);
        var recorded = await GetProposalAsync(sameAdmin, proposalId);
        recorded.GetProperty("requestedBy").GetString().Should().StartWith(
            "subject:",
            "the Studio propose path records the issuer- and tenant-qualified owner key, not the raw subject");

        using var response = await sameAdmin.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve", null);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain(SeparationOfDuties);
        var after = await GetProposalAsync(sameAdmin, proposalId);
        after.GetProperty("status").GetString().Should().Be(
            "AwaitingApproval",
            "a refused self-approval must not move the publication pointer");
        // The detail response omits null members, so an unresolved proposal carries no resolver.
        (after.TryGetProperty("resolvedBy", out var resolvedBy) && resolvedBy.ValueKind != JsonValueKind.Null)
            .Should().BeFalse("a refused self-approval must not record a resolver");
    }

    [IntegrationTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_studio_propose_publication")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    public async Task ApproveProposal_StudioPublicationByADifferentPrincipal_Succeeds()
    {
        using var proposer = CreateBearerClient(CreateToken("studio-proposer-carol"));
        using var approver = CreateBearerClient(CreateToken("studio-approver-dave", administrator: true));

        var proposalId = await ProposeAsync(proposer);

        using var response = await approver.PostAsync($"/api/v1/admin/proposals/{proposalId}/approve", null);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("status").GetString().Should().Be("Succeeded", body);
        document.RootElement.GetProperty("resolvedBy").GetString().Should().NotContain("studio-proposer-carol");
    }

    [IntegrationTest]
    [Operation(Operations.ApprovalManagement)]
    [Endpoint("POST /mcp tools/call honua_studio_propose_publication")]
    public async Task ApplyApprovedProposal_ApproverIdentitiesNameTheRequester_GatewayRefuses()
    {
        const string subject = "studio-proposer-erin";
        using var proposer = CreateBearerClient(CreateToken(subject));
        using var sameAdmin = CreateBearerClient(CreateToken(subject, administrator: true));
        var proposalId = await ProposeAsync(proposer);
        var requestedBy = (await GetProposalAsync(sameAdmin, proposalId)).GetProperty("requestedBy").GetString()!;
        var gateway = _fixture.Services.GetRequiredService<IOperationGateway>();

        // Every approval entry point reaches the gateway, so it refuses the requester on its own
        // even when a caller presents a different primary actor string.
        var act = () => gateway.ApplyApprovedProposalAsync(
            proposalId,
            new OperationProposalApprovalContext
            {
                ApprovedBy = "studio-proposer-erin",
                TenantId = Tenant,
                ApproverIdentities = ["studio-proposer-erin", requestedBy],
            });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"{SeparationOfDuties}*");
        (await GetProposalAsync(sameAdmin, proposalId)).GetProperty("status").GetString()
            .Should().Be("AwaitingApproval");
    }

    /// <summary>
    /// Composes, saves and proposes a publication as one principal over MCP, as the SDK replay
    /// harness does, returning the proposal id the tool reports.
    /// </summary>
    private static async Task<string> ProposeAsync(HttpClient client)
    {
        var packageKey = $"self-approval-{Guid.NewGuid():N}";
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

    private static async Task<JsonElement> GetProposalAsync(HttpClient client, string proposalId)
    {
        using var response = await client.GetAsync($"/api/v1/admin/proposals/{proposalId}");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
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

    private static string CreateToken(string subject, bool administrator = false)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        List<Claim> claims =
        [
            new Claim("sub", subject),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim("tid", Tenant),
            new Claim("scope", OperatorScopeCatalog.Full),
        ];
        if (administrator)
        {
            claims.Add(new Claim("roles", "admin"));
        }

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
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
            Task.FromResult(AccessDecision.Allowed("self-approval fixture"));
    }
}
