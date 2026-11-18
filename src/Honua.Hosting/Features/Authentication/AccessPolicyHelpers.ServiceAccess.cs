// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Models;

namespace Honua.Infrastructure.Authentication;

internal static partial class AccessPolicyHelpers
{
    internal static async Task<ServiceAccessEvaluation> EvaluateServiceAccessCoreAsync(
        ServiceAccessContext context,
        MetadataV2Service service,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!MetadataV2TenantVisibility.IsVisibleToTenant(service, context.TenantId))
        {
            return new(AccessDecision.Forbidden(TenantScopeDeniedReason), ServiceAccessBasis.TenantScope);
        }

        var serviceName = service.Metadata.Name;
        var scopedKeyDecision = EvaluateScopedKeyAccess(context.Principal, serviceName, null, context.Operation);
        if (scopedKeyDecision is { } decision)
        {
            return new(decision, ServiceAccessBasis.ScopedKey);
        }

        if (!string.IsNullOrWhiteSpace(serviceName))
        {
            var grantDecision = await EvaluateGrantCoreAsync(
                context.Services, context.Principal, serviceName, layerName: null,
                context.Operation, cancellationToken, context.RequestCache).ConfigureAwait(false);
            if (grantDecision.IsAllowed)
            {
                var granted = NarrowsWithRoleList(grantDecision.MatchedGrant)
                    ? ApplyRoleListToGrant(context.Services, context.Principal, resourcePolicy: null, service, context.Operation)
                    : AccessDecision.Allowed();
                return new(granted, ServiceAccessBasis.PermissionGrant);
            }
        }

        return new(EvaluateServicePolicy(context.Services, context.Principal, null, service,
            ScopeForOperation(context.Operation), context.Operation), ServiceAccessBasis.Policy);
    }

    internal static IResult? CreateServiceAccessDeniedResult(HttpContext context, ServiceAccessEvaluation evaluation)
        => evaluation.Basis switch
        {
            ServiceAccessBasis.TenantScope => StandardErrorHelpers.CreateForbidden(context, AccessForbiddenMessage),
            ServiceAccessBasis.PermissionGrant when evaluation.Decision.IsAllowed => null,
            _ => CreateAccessDeniedResult(context, evaluation.Decision)
        };
}

// A cache belongs to one principal. Delegated callers must provide their own cache or leave it null.
internal sealed record ServiceAccessContext
{
    public required IServiceProvider Services { get; init; }
    public required ClaimsPrincipal Principal { get; init; }
    public required AuthorizationOperation Operation { get; init; }
    public string? TenantId { get; init; }
    public IDictionary<object, object?>? RequestCache { get; init; }
}

internal readonly record struct ServiceAccessEvaluation(AccessDecision Decision, ServiceAccessBasis Basis);

internal enum ServiceAccessBasis
{
    TenantScope,
    ScopedKey,
    PermissionGrant,
    Policy
}
