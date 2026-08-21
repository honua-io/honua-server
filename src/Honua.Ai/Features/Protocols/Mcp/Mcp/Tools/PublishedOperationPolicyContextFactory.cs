// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// Builds the canonical authorization snapshot passed from MCP publishing tools to the
/// operation policy and approval pipeline.
/// </summary>
internal static class PublishedOperationPolicyContextFactory
{
    /// <summary>Safe denial returned when a caller has no durable actor binding.</summary>
    public const string UnstableIdentityMessage =
        "The authenticated principal does not have a stable subject or API-key identity.";

    /// <summary>
    /// Resolves a complete policy context from a framework-authenticated principal.
    /// Missing or ambiguous durable identity fails closed before operation dispatch.
    /// </summary>
    public static bool TryCreate(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        out OperationPolicyContext context)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(principal);

        var actor = CanonicalSecurityActor.Resolve(principal);
        if (actor is null)
        {
            context = null!;
            return false;
        }

        context = new OperationPolicyContext
        {
            PrincipalId = actor.ActorId,
            AuthenticationScheme = actor.AuthenticationScheme,
            SubjectId = actor.SubjectId,
            SubjectIssuer = actor.SubjectIssuer,
            ApiKeyId = actor.ApiKeyId,
            CredentialKind = actor.CredentialKind,
            Tier = ResolveTier(httpContext),
            Roles = principal.FindAll(ClaimTypes.Role).Select(static claim => claim.Value).ToArray(),
            Permissions = principal.FindAll("permission").Select(static claim => claim.Value).ToArray(),
            TenantId = principal.FindFirst("tenant_id")?.Value ?? principal.FindFirst("tid")?.Value,
            CorrelationId = httpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault()
                ?? httpContext.TraceIdentifier,
        };
        return true;
    }

    private static string? ResolveTier(HttpContext httpContext)
    {
        // Lightweight hosts without licensing leave tier unset, matching the Community
        // pass-through behavior used by the operation policy decision point.
        var licensing = httpContext.RequestServices.GetService<ILicenseEntitlementService>();
        return licensing?.GetSnapshot().Edition.ToString().ToLowerInvariant();
    }
}
