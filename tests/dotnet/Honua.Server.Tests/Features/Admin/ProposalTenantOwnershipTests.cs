// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

[Trait("Tier", "Fast")]
public sealed class ProposalTenantOwnershipTests
{
    [Theory]
    [InlineData("tenant-a", "admin", true)]
    [InlineData("tenant-b", "admin", false)]
    [InlineData("tenant-b", "platform_admin", true)]
    [InlineData("tenant-a", "admin", true, "tenant-a")]
    [InlineData("tenant-a", "admin", false, "tenant-b")]
    [InlineData("tenant-b", "admin", false, "tenant-b")]
    [InlineData("tenant-b", "platform_admin", false, "tenant-a")]
    public async Task ProposalResource_ProposerIdentityDoesNotBypassTenantOwnership(
        string tenantId, string role, bool allowed, string? evidenceTenant = null)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("api_key_id", "c9d03ead-f8c8-45d8-996d-bf020abcbd10"),
            new Claim(ClaimTypes.Role, role),
        }, "ApiKey"));
        var now = DateTimeOffset.UtcNow;
        var proposal = new OperationProposal
        {
            ProposalId = "proposal-a",
            TenantId = "tenant-a",
            Evidence = evidenceTenant is null ? null : Evidence(evidenceTenant),
            RequestedBy = CanonicalSecurityActor.Resolve(principal)!.ActorId,
            Kind = OperationClass.AdminConfigChange,
            Status = OperationProposalStatus.AwaitingApproval,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var store = Substitute.For<IOperationProposalStore>();
        store.GetAsync(proposal.ProposalId, Arg.Any<CancellationToken>()).Returns(proposal);
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(tenantId);
        using var services = new ServiceCollection().AddSingleton(store).AddSingleton(tenant)
            .AddSingleton(Options.Create(new TenantContextOptions())).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        var resource = new ProposalStatusResource(NullLogger<ProposalStatusResource>.Instance);

        if (allowed)
        {
            Assert.NotNull(await resource.ReadAsync(context, "honua://proposals/proposal-a", CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<KeyNotFoundException>(() => resource.ReadAsync(
                context, "honua://proposals/proposal-a", CancellationToken.None));
        }
    }

    internal static OperationProposalEvidence Evidence(string tenantId) => new()
    {
        TenantId = tenantId,
        ToolName = "test",
        OperationId = "test",
        CandidateId = "test",
        TargetId = "test",
        DescriptorRevision = "test",
        PolicyRevision = "test",
        AuthorizationDecision = "test",
        RequestDigest = "test",
        CanonicalRequest = "test",
        PayloadDigest = "test",
        CanonicalPayload = "test",
        TranscriptDigest = "test",
        TranscriptKeyId = "test",
        CanonicalTranscript = "test",
        TranscriptSignature = "test",
        ReleaseId = "test",
        ActionId = "test",
        RunNonce = "test",
        McpSessionId = "test",
        McpCallId = "test",
        IssuedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
    };
}
