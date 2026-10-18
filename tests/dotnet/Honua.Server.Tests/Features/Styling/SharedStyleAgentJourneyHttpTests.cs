// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using IOperationExecutor = Honua.Core.Features.Operations.Abstractions.IOperationExecutor;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Server.Features.AgentWorkspace;
using Honua.Server.Features.Operations;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Styling;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.OgcApiStyles, TestProtocols.Mcp)]
public sealed class SharedStyleAgentJourneyHttpTests(RedisFixture redis)
{
    [IntegrationTheory]
    [InlineData("none", false)]
    [InlineData("none", true)]
    [InlineData("throw", false)]
    [InlineData("cancel", false)]
    [Operation(Operations.Update)]
    [Endpoint("GET /ogc/styles/{styleId}/impact")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/operations/{operationInstanceId}")]
    [Endpoint("POST /mcp tools/call honua_workspace_get_command")]
    [Endpoint("POST /mcp tools/call honua_shared_style_get")]
    [Endpoint("POST /mcp tools/call honua_shared_style_propose")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld")]
    public async Task FrozenSharedStyle_AgentProposes_HumanApproves_ExactBytesCommitAndLostResponseReconciles(string postCommitFault, bool delegatedAgent)
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "shared-style-workload" };
        var membership = new MutableMembership { WorkloadOnly = delegatedAgent, Current = delegatedAgent ? new(true, ["worker"]) : null };
        var initialMembership = membership.Current;
        var fault = new PostCommitFault();
        var fixture = new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IPrincipalMembershipSource>(membership);
                if (delegatedAgent)
                {
                    services.Configure<OidcAuthenticationOptions>(options =>
                    {
                        options.DelegatedAgentAudiences = [new() { Issuer = "https://workload.example", Audience = "honua-workloads" }];
                        options.DelegatedAgents = [new() { Id = "style-agent", Generation = 1, Enabled = true, Issuer = "https://workload.example", Audience = "honua-workloads",
                            Subject = "shared-style-agent", Tenant = "public", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), Permissions = ["agent:workspace", "admin:write", "read:*"] }];
                    });
                    services.PostConfigure<JwtBearerOptions>(OidcAuthenticationExtensions.JwtBearerScheme, options =>
                    {
                        var discovery = new OpenIdConnectConfiguration { Issuer = "https://workload.example" };
                        discovery.SigningKeys.Add(signingKey);
                        options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(discovery);
                    });
                    var roles = Substitute.For<IRoleStore>();
                    roles.GetEffectivePermissionsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                        .Returns(new EffectivePermissions
                        {
                            UserId = "shared-style-agent",
                            Roles = ["worker"],
                            Permissions = [
                            new PermissionGrant { Service = "agent", Layer = "*", Operation = "workspace" },
                            new PermissionGrant { Service = "admin", Layer = "*", Operation = "write" },
                            new PermissionGrant { Service = "*", Layer = "*", Operation = "*" }]
                        });
                    services.RemoveAll<IRoleStore>();
                    services.AddSingleton(roles);
                }
                services.AddKeyedScoped<IOperationExecutor>(SharedStyleEditOperation.OperationId,
                    (provider, _) => new FaultingStyleExecutor(new SharedStyleEditExecutor(provider), fault));
            });
            if (delegatedAgent)
            {
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", "https://workload.example");
                builder.UseSetting("Oidc:Generic:ClientId", "honua-workloads");
                builder.UseSetting("Oidc:TokenValidation:EnableTokenReplayProtection", "false");
            }
            builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("AgentWorkspace:DeploymentId", "shared-style-http-" + Guid.NewGuid().ToString("N"));
            builder.UseSetting("AgentWorkspace:ReleaseId", "shared-style-preview");
            builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
            builder.UseSetting("Authentication:OperatorBearer:SigningKey", "shared-style-http-only-signing-key-at-least-32-bytes");
            builder.UseSetting("OperatorApproval:PublishRequiresApproval", "false");
            builder.UseSetting("OperatorApproval:AdminExemptFromApproval", "true");
        });
        await fixture.InitializeAsync();
        try
        {
            using var bootstrap = fixture.CreateAdminClient();
            string material;
            string keyId;
            if (delegatedAgent)
            {
                var jwt = new JwtSecurityToken("https://workload.example", "honua-workloads",
                    [new Claim("sub", "shared-style-agent"), new Claim("tenant_id", "public"), new Claim("scope", "agent:workspace honua.mcp.full")],
                    DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(20), new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
                material = new JwtSecurityTokenHandler().WriteToken(jwt);
                keyId = "";
                fixture.Services.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = CanonicalSecurityActor.Resolve(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", "https://workload.example"), new Claim("sub", "shared-style-agent")], OidcAuthenticationExtensions.JwtBearerScheme)))!.ActorId;
            }
            else
            {
                var issued = await Read(await bootstrap.PostAsJsonAsync("/api/v1/admin/api-keys", new
                { name = "shared-style-agent", permissions = new[] { "admin", "read:*", "agent:workspace" } }), HttpStatusCode.Created);
                material = issued.GetProperty("data").GetProperty("key").GetString()!;
                keyId = issued.GetProperty("data").GetProperty("apiKey").GetProperty("id").GetString()!;
                fixture.Services.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = "apikey:api-key:" + keyId;
            }
            var token = fixture.Services.GetRequiredService<OperatorBearerTokenService>().Issue(
                [new() { Type = ClaimTypes.NameIdentifier, Value = "shared-style-human" }, new() { Type = ClaimTypes.Role, Value = "admin" },
                 new() { Type = "tenant_id", Value = "public" }], DateTimeOffset.UtcNow.AddMinutes(10));
            token.Should().NotBeNull();
            using var human = fixture.CreateClient(client => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Token));
            using var agent = fixture.CreateClient(client =>
            {
                if (delegatedAgent) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", material);
                else client.DefaultRequestHeaders.Add("X-API-Key", material);
            });
            {
                // A workload's admin:write metadata capability must not escape
                // its profile by issuing another credential or changing trust/RBAC.
                var humanIssued = await Read(await human.PostAsJsonAsync("/api/v1/admin/api-keys", new
                { name = "human-credential-control", permissions = new[] { "admin:read" } }), HttpStatusCode.Created);
                var controlKeyId = humanIssued.GetProperty("data").GetProperty("apiKey").GetProperty("id").GetGuid();
                await using var credentialScope = fixture.Services.CreateAsyncScope();
                var keys = credentialScope.ServiceProvider.GetRequiredService<IAdminApiKeyStore>();
                var before = (await keys.GetAsync(controlKeyId, default))!;
                using var mint = await agent.PostAsJsonAsync("/api/v1/admin/api-keys", new
                { name = "workload-escape", permissions = new[] { "admin" } });
                mint.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                using var rotate = await agent.PostAsync($"/api/v1/admin/api-keys/{controlKeyId}/rotate", null);
                rotate.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                using var revoke = await agent.PostAsync($"/api/v1/admin/api-keys/{controlKeyId}/revoke", null);
                revoke.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                var after = (await keys.GetAsync(controlKeyId, default))!;
                after.KeyHash.Should().Equal(before.KeyHash);
                after.RevokedAt.Should().BeNull();
                using var oauth = await agent.PostAsJsonAsync("/api/v1/admin/oauth-clients", new { });
                oauth.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                using var scopes = await agent.PutAsJsonAsync("/api/v1/admin/oauth-scopes", new { });
                scopes.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                using var trust = await agent.PostAsJsonAsync("/api/v1/admin/security/client-certificates/profiles", new { });
                trust.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                using var mapping = await agent.PostAsJsonAsync("/api/v1/admin/security/client-certificates/profiles/profile/mappings", new { });
                mapping.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                using var roles = await agent.PostAsJsonAsync("/api/v1/admin/roles", new { });
                roles.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                using var membershipChange = await agent.PutAsJsonAsync("/api/v1/admin/users/subject/roles", new { });
                membershipChange.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await keys.ListAsync(default)).Should().NotContain(key => key.Name == "workload-escape");
            }
            using (var legacy = await agent.PutAsJsonAsync($"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
                new { mapLibreStyle = new { version = 8, sources = new { }, layers = Array.Empty<object>() } }))
                legacy.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using (var legacySld = await agent.PostAsync($"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/import-sld",
                new StringContent("<StyledLayerDescriptor/>", Encoding.UTF8, "application/xml")))
                legacySld.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var styleId = "shared-style-journey-" + Guid.NewGuid().ToString("N");
            const string initial = """{"version":8,"sources":{},"layers":[{"id":"background","type":"background","paint":{"background-color":"#112233"}}]}""";
            const string proposed = """{"version":8,"sources":{},"layers":[{"id":"background","type":"background","paint":{"background-color":"#8844aa"}}]}""";
            using (var create = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles")
            { Content = new StringContent(initial, Encoding.UTF8, "application/vnd.mapbox.style+json") })
            {
                create.Headers.Add("X-Style-Id", styleId);
                using var response = await human.SendAsync(create);
                response.StatusCode.Should().Be(HttpStatusCode.Created);
            }
            var impact = await Read(await human.GetAsync("/ogc/styles/" + styleId + "/impact"), HttpStatusCode.OK);
            var target = (await Read(await human.GetAsync("/api/v1/agent/target"), HttpStatusCode.OK)).GetProperty("targetId").GetString()!;
            var workspace = await Read(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "shared-style-tab", targetId = target }), HttpStatusCode.OK);
            var workspaceId = workspace.GetProperty("workspaceId").GetString()!;
            var root = "/api/v1/agent/workspaces/" + workspaceId;
            var task = new
            {
                kind = "shared-style",
                id = styleId,
                revision = impact.GetProperty("styleVersion").GetInt32(),
                contentHash = impact.GetProperty("contentHash").GetString(),
                impactHash = impact.GetProperty("impactHash").GetString()
            };
            await Read(await human.PutAsJsonAsync(root + "/context", new
            { viewId = "shared-style-tab", expectedRevision = 0, targetId = target, selectedResources = Array.Empty<object>(), task }), HttpStatusCode.OK);
            await Read(await human.PostAsJsonAsync(root + "/turns", new
            { turnId = "style-turn", viewId = "shared-style-tab", contextRevision = 1, prompt = "Make this shared background purple and propose its exact impact for review." }), HttpStatusCode.OK);
            agent.DefaultRequestHeaders.Add("X-Honua-Workspace", workspaceId);
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "style-turn");
            var actual = await Tool(agent, "honua_shared_style_get", new { workspaceId, turnId = "style-turn" });
            actual.GetProperty("impact").GetProperty("contentHash").GetString().Should().Be(task.contentHash);
            actual.GetProperty("style").GetProperty("layers")[0].GetProperty("paint").GetProperty("background-color").GetString().Should().Be("#112233");
            using var candidate = JsonDocument.Parse(proposed);
            var arguments = new
            {
                workspaceId,
                turnId = "style-turn",
                intent = new
                { styleId, styleVersion = task.revision, task.contentHash, task.impactHash, style = candidate.RootElement.Clone() }
            };
            var handle = await Tool(agent, "honua_shared_style_propose", arguments);
            handle.GetProperty("status").GetString().Should().Be("RequiresApproval");
            var proposalId = handle.GetProperty("proposalId").GetString()!;
            var operationId = handle.GetProperty("operationInstanceId").GetString()!;
            (await Tool(agent, "honua_shared_style_propose", arguments)).GetProperty("operationInstanceId").GetString().Should().Be(operationId);
            await using var scope = fixture.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();
            (await catalog.GetStyleAsync(styleId))!.StyleVersion.Should().Be(task.revision);
            var proposalStore = scope.ServiceProvider.GetRequiredService<IOperationProposalStore>();
            var instanceStore = scope.ServiceProvider.GetRequiredService<IOperationInstanceStore>();
            var waitingProposal = (await proposalStore.GetAsync(proposalId))!;
            {
                var other = waitingProposal with { ProposalId = "other-agent-" + Guid.NewGuid().ToString("N"), RequestedBy = "different-agent", Version = 0 };
                (await proposalStore.TryCreateAsync(other)).Should().BeTrue();
                using var forbiddenApproval = await agent.PostAsync("/api/v1/admin/proposals/" + other.ProposalId + "/approve", null);
                forbiddenApproval.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                using var forbiddenRejection = await agent.PostAsJsonAsync("/api/v1/admin/proposals/" + other.ProposalId + "/reject", new { reason = "agent must not decide another actor's proposal" });
                forbiddenRejection.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await proposalStore.GetAsync(other.ProposalId))!.Status.Should().Be(other.Status);
            }
            var waitingHandle = (await instanceStore.GetAsync(operationId))!;
            // A durable human claim without SQL proof must remain exactly unchanged on actual MCP status read.
            await SetProposalState(proposalStore, waitingProposal with
            {
                Status = OperationProposalStatus.Executing,
                ApprovedByAtClaim = "shared-style-human",
                ApprovedAtClaim = DateTimeOffset.UtcNow
            });
            var beforeMissingReceipt = (await proposalStore.GetAsync(proposalId))!;
            (await ReadProposal(agent, proposalId)).GetProperty("status").GetString().Should().Be("Executing");
            (await Tool(agent, "honua_workspace_get_command", new { workspaceId, operationInstanceId = operationId }))
                .GetProperty("data").GetProperty("status").GetString().Should().Be("RequiresApproval");
            (await proposalStore.GetAsync(proposalId)).Should().BeEquivalentTo(beforeMissingReceipt);
            (await instanceStore.GetAsync(operationId)).Should().BeEquivalentTo(waitingHandle);
            await SetProposalState(proposalStore, beforeMissingReceipt with { Status = OperationProposalStatus.Failed });
            await instanceStore.SetAsync(waitingHandle with { Status = OperationHandleStatus.Failed });
            var noReceiptHandle = (await instanceStore.GetAsync(operationId))!;
            var noReceiptProposal = (await proposalStore.GetAsync(proposalId))!;
            (await Tool(agent, "honua_workspace_get_command", new { workspaceId, operationInstanceId = operationId }))
                .GetProperty("data").GetProperty("status").GetString().Should().Be("Failed");
            (await instanceStore.GetAsync(operationId)).Should().BeEquivalentTo(noReceiptHandle);
            (await proposalStore.GetAsync(proposalId)).Should().BeEquivalentTo(noReceiptProposal);
            await instanceStore.SetAsync(waitingHandle);
            await SetProposalState(proposalStore, waitingProposal);
            (await agent.PostAsync("/api/v1/admin/proposals/" + proposalId + "/approve", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using var inbox = await human.GetAsync("/api/v1/admin/proposals/" + proposalId);
            var inboxText = await inbox.Content.ReadAsStringAsync();
            inbox.StatusCode.Should().Be(HttpStatusCode.OK);
            inboxText.Contains(material, StringComparison.Ordinal).Should().BeFalse();
            inboxText.Contains("executionPayload", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            fault.Next = postCommitFault;
            (await human.PostAsync("/api/v1/admin/proposals/" + proposalId + "/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await proposalStore.GetAsync(proposalId))!.Status.Should().Be(
                postCommitFault == "none" ? OperationProposalStatus.Succeeded : OperationProposalStatus.Failed);
            var committed = (await catalog.GetStyleAsync(styleId))!;
            committed.StyleVersion.Should().Be(task.revision + 1);
            using var stored = JsonDocument.Parse(committed.MapLibreStyleJson);
            stored.RootElement.GetProperty("layers")[0].GetProperty("paint").GetProperty("background-color").GetString().Should().Be("#8844aa");
            // Reconstruct the durable records left by a process stop after the actual SQL commit,
            // before canonical completion persistence. This is a crash-window regression, not a process-kill certification.
            var successfulProposal = (await proposalStore.GetAsync(proposalId))!;
            successfulProposal.ApprovedByAtClaim.Should().NotBeNullOrWhiteSpace();
            successfulProposal.ApprovedAtClaim.Should().NotBeNull();
            if (postCommitFault == "none")
            {
                await SetProposalState(proposalStore, successfulProposal with
                { Status = OperationProposalStatus.Executing, ResolvedBy = null, ResolvedAt = null });
                await instanceStore.SetAsync(waitingHandle with { Status = OperationHandleStatus.Accepted });
            }
            successfulProposal.SealedPlanHash.Should().Be(OperationApprovalPlanSeal.Compute(successfulProposal.Plan));
            var replayVerifier = scope.ServiceProvider.GetRequiredService<IOperationApprovalReplayVerifier>();
            var uncertainProposal = (await proposalStore.GetAsync(proposalId))!;
            (await replayVerifier.VerifyCommittedReceiptAsync(proposalId, operationId, uncertainProposal.SealedPlanHash!)).Should().BeTrue();
            if (postCommitFault != "none")
                (await replayVerifier.VerifyAsync(proposalId, operationId, uncertainProposal.SealedPlanHash!)).Should().BeFalse(
                    "ordinary approval replay remains Executing-only; a SQL receipt is not permission to actuate again");
            var pendingStatus = (await instanceStore.GetAsync(operationId))!.Status;
            var pendingProposalStatus = (await proposalStore.GetAsync(proposalId))!.Status.ToString();
            // Even a genuine SQL receipt cannot revive explicit terminal denial or cancellation.
            foreach (var protectedStatus in new[] { OperationHandleStatus.Denied, OperationHandleStatus.Cancelled })
            {
                var priorHandle = (await instanceStore.GetAsync(operationId))!;
                await instanceStore.SetAsync(priorHandle with { Status = protectedStatus });
                var protectedHandle = (await instanceStore.GetAsync(operationId))!;
                var protectedProposal = (await proposalStore.GetAsync(proposalId))!;
                (await Tool(agent, "honua_workspace_get_command", new { workspaceId, operationInstanceId = operationId }))
                    .GetProperty("data").GetProperty("status").GetString().Should().Be(protectedStatus.ToString());
                (await instanceStore.GetAsync(operationId)).Should().BeEquivalentTo(protectedHandle);
                (await proposalStore.GetAsync(proposalId)).Should().BeEquivalentTo(protectedProposal);
                await instanceStore.SetAsync(priorHandle);
            }
            // A non-proposer may read an authorized inbox entry but cannot adopt its receipt.
            (await ReadProposal(human, proposalId)).GetProperty("status").GetString().Should().Be(pendingProposalStatus);
            (await instanceStore.GetAsync(operationId))!.Status.Should().Be(pendingStatus);
            var legacyExecuting = (await proposalStore.GetAsync(proposalId))!;
            await SetProposalState(proposalStore, legacyExecuting with { ApprovedByAtClaim = null, ApprovedAtClaim = null });
            (await replayVerifier.VerifyCommittedReceiptAsync(proposalId, operationId, legacyExecuting.SealedPlanHash!)).Should().BeFalse();
            (await ReadProposal(agent, proposalId)).GetProperty("status").GetString().Should().Be(pendingProposalStatus);
            (await instanceStore.GetAsync(operationId))!.Status.Should().Be(pendingStatus);
            await SetProposalState(proposalStore, legacyExecuting);
            var workspaceStore = scope.ServiceProvider.GetRequiredService<IAgentWorkspaceStore>();
            var workspaceDocument = (await workspaceStore.GetAsync(workspaceId))!;
            (await workspaceStore.SaveAsync(workspaceDocument with { Members = [workspaceDocument.OwnerId] })).Should().BeTrue();
            var removedMembership = await ProposalEnvelope(agent, proposalId);
            IsFailure(removedMembership).Should().BeTrue();
            IsFailure(await Rpc(agent, "honua_workspace_get_command", new { workspaceId, operationInstanceId = operationId })).Should().BeTrue();
            (await instanceStore.GetAsync(operationId))!.Status.Should().Be(pendingStatus);
            var removedDocument = (await workspaceStore.GetAsync(workspaceId))!;
            (await workspaceStore.SaveAsync(workspaceDocument with { Version = removedDocument.Version })).Should().BeTrue();
            membership.Current = new(false, []);
            if (delegatedAgent)
            {
                using var inactive = await agent.GetAsync(root + "/operations/" + operationId);
                inactive.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }
            else
            {
                IsFailure(await ProposalEnvelope(agent, proposalId)).Should().BeTrue();
                IsFailure(await Rpc(agent, "honua_workspace_get_command", new { workspaceId, operationInstanceId = operationId })).Should().BeTrue();
            }
            (await instanceStore.GetAsync(operationId))!.Status.Should().Be(pendingStatus);
            membership.Current = initialMembership;
            if (postCommitFault == "throw")
            {
                var originalRetry = await Tool(agent, "honua_shared_style_propose", arguments);
                originalRetry.GetProperty("operationInstanceId").GetString().Should().Be(operationId);
                originalRetry.GetProperty("status").GetString().Should().Be("Completed");
                (await catalog.GetStyleAsync(styleId))!.StyleVersion.Should().Be(task.revision + 1);
            }
            // Navigate away before starting a new chat; status must bind the stored
            // original task rather than the current (now empty) selection.
            await Read(await human.PutAsJsonAsync(root + "/context", new
            { viewId = "shared-style-tab", expectedRevision = 1, targetId = target, selectedResources = Array.Empty<object>(), task = (object?)null }), HttpStatusCode.OK);
            await Read(await human.PostAsJsonAsync(root + "/turns", new
            { turnId = "recovery-chat", viewId = "shared-style-tab", contextRevision = 2, prompt = "Check the prior operation without applying it again." }), HttpStatusCode.OK);
            agent.DefaultRequestHeaders.Remove("X-Honua-Turn");
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "recovery-chat");
            var statusReads = await Task.WhenAll(
                Tool(agent, "honua_workspace_get_command", new { workspaceId, operationInstanceId = operationId }),
                Tool(agent, "honua_workspace_get_command", new { workspaceId, operationInstanceId = operationId }));
            statusReads.Should().OnlyContain(value => value.GetProperty("data").GetProperty("status").GetString() == "Completed");
            using (var rest = await agent.GetAsync(root + "/operations/" + operationId)) rest.StatusCode.Should().Be(HttpStatusCode.OK);
            IsFailure(await Rpc(agent, "honua_workspace_get_command", new { workspaceId, commandId = "wrong", operationInstanceId = operationId })).Should().BeTrue();
            IsFailure(await Rpc(agent, "honua_workspace_get_command", new { workspaceId })).Should().BeTrue();
            agent.DefaultRequestHeaders.Remove("X-Honua-Turn");
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "style-turn");
            var recovered = await Task.WhenAll(ReadProposal(agent, proposalId), ReadProposal(agent, proposalId));
            recovered.Should().OnlyContain(value => value.GetProperty("status").GetString() == "Succeeded");
            var recoveredProposal = (await proposalStore.GetAsync(proposalId))!;
            recoveredProposal.ResolvedBy.Should().Be(successfulProposal.ApprovedByAtClaim);
            recoveredProposal.Audit.OperationInstanceId.Should().Be(operationId);
            var recoveryAudit = await scope.ServiceProvider.GetRequiredService<IAuditLogReader>().ListAsync(new()
            { ResourceId = proposalId, Action = "operation.receipt_reconciled" });
            recoveryAudit.Items.Should().ContainSingle();
            recoveryAudit.Items[0].Actor.Should().Be(successfulProposal.ApprovedByAtClaim);
            (await instanceStore.GetAsync(operationId))!.Status.Should().Be(OperationHandleStatus.Completed);
            (await catalog.GetStyleAsync(styleId))!.StyleVersion.Should().Be(task.revision + 1);
            // The reverse ordering (original completion already persisted) is a terminal no-op for the handle.
            var terminal = (await instanceStore.GetAsync(operationId))!;
            await SetProposalState(proposalStore, recoveredProposal with { Status = OperationProposalStatus.Executing });
            (await ReadProposal(agent, proposalId)).GetProperty("status").GetString().Should().Be("Succeeded");
            (await instanceStore.GetAsync(operationId)).Should().BeEquivalentTo(terminal);
            // A lost response reconciles the canonical operation even though its own mutation advanced the focused style.
            (await Tool(agent, "honua_shared_style_propose", arguments)).GetProperty("operationInstanceId").GetString().Should().Be(operationId);
            (await catalog.GetStyleAsync(styleId))!.StyleVersion.Should().Be(task.revision + 1);
            var staleRead = await Rpc(agent, "honua_shared_style_get", new { workspaceId, turnId = "style-turn" });
            IsFailure(staleRead).Should().BeTrue("a frozen read cannot quietly retarget to different current bytes");
            // Restoration is a new reviewed operation using retained canonical before bytes, never an old version label.
            var restoreImpact = await Read(await human.GetAsync("/ogc/styles/" + styleId + "/impact"), HttpStatusCode.OK);
            await Read(await human.PutAsJsonAsync(root + "/context", new
            {
                viewId = "shared-style-tab",
                expectedRevision = 2,
                targetId = target,
                selectedResources = Array.Empty<object>(),
                task = new
                {
                    kind = "shared-style",
                    id = styleId,
                    revision = restoreImpact.GetProperty("styleVersion").GetInt32(),
                    contentHash = restoreImpact.GetProperty("contentHash").GetString(),
                    impactHash = restoreImpact.GetProperty("impactHash").GetString()
                }
            }), HttpStatusCode.OK);
            await Read(await human.PostAsJsonAsync(root + "/turns", new
            { turnId = "restore-turn", viewId = "shared-style-tab", contextRevision = 3, prompt = "Restore the retained prior shared appearance for human review." }), HttpStatusCode.OK);
            agent.DefaultRequestHeaders.Remove("X-Honua-Turn");
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "restore-turn");
            var restore = await Tool(agent, "honua_shared_style_propose", new
            { workspaceId, turnId = "restore-turn", restoreFromProposalId = proposalId });
            restore.GetProperty("status").GetString().Should().Be("RequiresApproval");
            (await catalog.GetStyleAsync(styleId))!.StyleVersion.Should().Be(task.revision + 1);
            var restoreId = restore.GetProperty("proposalId").GetString()!;
            restoreId.Should().NotBe(proposalId);
            (await human.PostAsync("/api/v1/admin/proposals/" + restoreId + "/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            var restored = (await catalog.GetStyleAsync(styleId))!;
            restored.StyleVersion.Should().Be(task.revision + 2);
            using var restoredBytes = JsonDocument.Parse(restored.MapLibreStyleJson);
            restoredBytes.RootElement.GetProperty("layers")[0].GetProperty("paint").GetProperty("background-color").GetString().Should().Be("#112233");

            var restoredProposal = (await proposalStore.GetAsync(restoreId))!;
            await SetProposalState(proposalStore, restoredProposal with { Status = OperationProposalStatus.Executing });
            var restoredOperationId = restoredProposal.Audit.OperationInstanceId!;
            var restoredHandle = (await instanceStore.GetAsync(restoredOperationId))!;
            await instanceStore.SetAsync(restoredHandle with { Status = OperationHandleStatus.Accepted });
            if (delegatedAgent) membership.Current = new(false, []);
            else await scope.ServiceProvider.GetRequiredService<IAdminApiKeyStore>().RevokeAsync(Guid.Parse(keyId), CancellationToken.None);
            using var revokedResponse = await agent.PostAsJsonAsync("/mcp", new
            { jsonrpc = "2.0", id = "revoked", method = "resources/read", @params = new { uri = "honua://proposals/" + restoreId } });
            revokedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await proposalStore.GetAsync(restoreId))!.Status.Should().Be(OperationProposalStatus.Executing);
            (await instanceStore.GetAsync(restoredOperationId))!.Status.Should().Be(OperationHandleStatus.Accepted);

        }
        finally { await fixture.DisposeAsync(); }
    }

    private static async Task<JsonElement> Tool(HttpClient client, string name, object arguments)
    {
        var envelope = await Rpc(client, name, arguments);
        IsFailure(envelope).Should().BeFalse();
        return envelope.GetProperty("result").GetProperty("structuredContent").Clone();
    }
    private static bool IsFailure(JsonElement envelope) => envelope.TryGetProperty("error", out _) ||
        envelope.TryGetProperty("result", out var result) && result.TryGetProperty("isError", out var error) && error.GetBoolean();
    private static async Task SetProposalState(IOperationProposalStore store, OperationProposal proposal)
    {
        var current = (await store.GetAsync(proposal.ProposalId))!;
        (await store.TrySetAsync(proposal with { Version = current.Version })).Should().BeTrue();
    }

    private sealed class MutableMembership : IPrincipalMembershipSource
    {
        public PrincipalMembership? Current { get; set; }
        public bool WorkloadOnly { get; init; }
        public Task<PrincipalMembership?> ResolveMembershipAsync(string principalId, CancellationToken cancellationToken = default)
            => Task.FromResult(WorkloadOnly && principalId != "shared-style-agent" ? null : Current);
    }

    private static async Task<JsonElement> ProposalEnvelope(HttpClient client, string id)
        => await Read(await client.PostAsJsonAsync("/mcp", new
        { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "resources/read", @params = new { uri = "honua://proposals/" + id } }), HttpStatusCode.OK);

    private static async Task<JsonElement> ReadProposal(HttpClient client, string id)
    {
        var envelope = await ProposalEnvelope(client, id);
        envelope.TryGetProperty("error", out _).Should().BeFalse();
        using var parsed = JsonDocument.Parse(envelope.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!);
        return parsed.RootElement.Clone();
    }

    private static async Task<JsonElement> Rpc(HttpClient client, string name, object arguments)
        => await Read(await client.PostAsJsonAsync("/mcp", new
        { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "tools/call", @params = new { name, arguments } }), HttpStatusCode.OK);
    private static async Task<JsonElement> Read(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            response.StatusCode.Should().Be(expected);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }
    }
    private sealed class PostCommitFault { public string Next = "none"; }
    private sealed class FaultingStyleExecutor(SharedStyleEditExecutor inner, PostCommitFault fault)
        : IOperationExecutor, IOperationRequestPreparer, IOperationCommittedReceiptReader
    {
        public string OperationId => inner.OperationId;
        public Task<OperationRequest> PrepareAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
            => inner.PrepareAsync(request, context, cancellationToken);
        public Task<OperationValidation> ValidateAsync(OperationRequest request, CancellationToken cancellationToken = default)
            => inner.ValidateAsync(request, cancellationToken);
        public async Task<OperationHandle> SubmitAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
        {
            var committed = await inner.SubmitAsync(request, context, cancellationToken);
            var mode = Interlocked.Exchange(ref fault.Next, "none");
            if (mode == "throw") throw new InvalidOperationException("Injected after SQL commit, before canonical completion.");
            if (mode == "cancel") throw new OperationCanceledException("Injected post-commit cancellation.");
            return committed;
        }
        public Task<OperationHandle?> ReadCommittedReceiptAsync(OperationRequest request, OperationPolicyContext context, CancellationToken cancellationToken = default)
            => inner.ReadCommittedReceiptAsync(request, context, cancellationToken);
        public Task<OperationStatus> GetStatusAsync(OperationHandle handle, CancellationToken cancellationToken = default)
            => inner.GetStatusAsync(handle, cancellationToken);
    }
}
