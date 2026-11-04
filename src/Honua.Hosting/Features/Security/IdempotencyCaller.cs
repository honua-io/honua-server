// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Security;

/// <summary>
/// The caller a stored idempotent result belongs to: the effective tenant and the scheme- and
/// issuer-qualified actor. A retry may replay a stored result only for the same caller.
/// </summary>
internal sealed record IdempotencyCaller(string Tenant, string Actor)
{
    /// <summary>Client-safe refusal for a keyed request whose caller <see cref="Resolve"/> cannot bind.</summary>
    public const string UnbindableCallerMessage =
        "An idempotency key requires a caller identity the server can bind retries to.";

    private const string AnonymousActor = "anonymous";
    private const string NoTenant = "none";

    /// <summary>
    /// Resolves the caller of <paramref name="httpContext"/>, or returns <see langword="null"/>
    /// for an authenticated caller whose identity cannot be bound to a durable actor.
    /// </summary>
    public static IdempotencyCaller? Resolve(HttpContext? httpContext)
    {
        var principal = httpContext?.User;
        if (ResolveActor(principal) is not { } actor)
        {
            return null;
        }

        var tenant = ResolveTenant(httpContext, principal);
        return new IdempotencyCaller(tenant is null ? NoTenant : "tenant:" + tenant, actor);
    }

    /// <summary>SHA-256 of the length-prefixed tenant and actor, safe to embed in a cache key.</summary>
    public string Fingerprint()
    {
        var canonical = string.Concat(Tenant.Length.ToString(CultureInfo.InvariantCulture), ":", Tenant, "|", Actor);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string? ResolveActor(ClaimsPrincipal? principal)
        => principal?.Identity?.IsAuthenticated == true
            ? CanonicalSecurityActor.Resolve(principal)?.ActorId
            : AnonymousActor;

    private static string? ResolveTenant(HttpContext? httpContext, ClaimsPrincipal? principal)
    {
        if (httpContext?.RequestServices?.GetService<ITenantContext>() is { } tenantContext)
        {
            return tenantContext.TenantId;
        }

        return principal is null
            ? null
            : CanonicalSecurityActor.FindStampedValue(principal, CanonicalSecurityActor.EffectiveTenantClaim);
    }
}
