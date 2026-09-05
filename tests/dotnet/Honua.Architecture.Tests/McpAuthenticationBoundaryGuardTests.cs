// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Architecture guard for the MCP control-plane authority boundary (#3430).
/// </summary>
/// <remarks>
/// <para>
/// Two invariants are load-bearing for the 2026.1 terminal gate and both are
/// invisible to ordinary behavioural tests once they regress silently:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// A presented <c>/mcp</c> bearer credential is validated — and an invalid one
/// rejected — before tenant resolution, tenant schema routing, tenant status
/// enforcement, and the shared rate limiter observe the request. Moving
/// <c>UseMcpBearerAuthentication</c> behind <c>UseHonuaTenantContext</c> again
/// re-opens the split security context described in the issue: a valid OAuth
/// call executing against a default/null tenant.
/// </description>
/// </item>
/// <item>
/// <description>
/// A live MCP session is keyed by the framework-owned canonical actor
/// (issuer-qualified OIDC subject, or the immutable API-key id), the effective
/// tenant, the OAuth scope ceiling, and a one-way fingerprint of the exact
/// validated credential. Dropping any of those components lets the same
/// <c>sub</c> from two issuers, or the same actor in two tenants, resume, read,
/// or terminate each other's sessions.
/// </description>
/// </item>
/// </list>
/// <para>
/// These are source-shape guards rather than behavioural assertions on purpose:
/// the behaviour is already covered by the MCP bearer/session suites, and what
/// this test adds is a fail-loud tripwire on the <em>structure</em> those suites
/// depend on. A deliberate change here must update the guard and say why.
/// </para>
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class McpAuthenticationBoundaryGuardTests
{
    private const string ProgramRelativePath = "src/Honua.Server/Program.cs";

    private const string CanonicalActorRelativePath =
        "src/Honua.Hosting/Features/Security/CanonicalSecurityActor.cs";

    private const string AuthorizationHelperRelativePath =
        "src/Honua.Ai/Features/Protocols/Mcp/Mcp/McpAuthorizationHelper.cs";

    private const string EndpointRelativePath =
        "src/Honua.Ai/Features/Protocols/Mcp/Mcp/McpEndpointExtensions.cs";

    [ArchitectureTest]
    public void McpBearerAuthentication_IsOrderedBeforeTheTenantAuthorityBoundary()
    {
        var source = ReadRepositoryFile(ProgramRelativePath);

        var authenticate = RequireIndex(source, ".UseMcpBearerAuthentication(app);");
        var audit = RequireIndex(source, "app.UseHonuaAuditLog();");
        var invalidBearerRateLimit = RequireIndex(source, "invalidBearer.UseRateLimiting();");
        var reject = RequireIndex(source, ".UseMcpBearerAuthenticationRejection(invalidBearer);");
        var tenantContext = RequireIndex(source, "app.UseHonuaTenantContext();");
        var schemaRouting = RequireIndex(source, "app.UseHonuaTenantSchemaRouting();");
        var statusEnforcement = RequireIndex(source, "app.UseHonuaTenantStatusEnforcement();");
        var requestRateLimit = source.LastIndexOf("app.UseRateLimiting();", StringComparison.Ordinal);

        requestRateLimit.Should().BeGreaterThan(-1);

        authenticate.Should().BeLessThan(tenantContext,
            "an MCP bearer must be validated before tenant context, or a valid OAuth call can execute "
            + "against the deployment default tenant (#3430)");
        audit.Should().BeGreaterThan(authenticate,
            "durable audit must record the canonical validated principal, not the pre-bearer principal");
        invalidBearerRateLimit.Should().BeGreaterThan(audit,
            "invalid-credential attempts must enter the shared rate limiter inside the audit boundary");
        reject.Should().BeGreaterThan(invalidBearerRateLimit);
        reject.Should().BeLessThan(tenantContext,
            "an invalid/expired/wrong-issuer bearer must be answered before tenant resolution runs");
        tenantContext.Should().BeLessThan(schemaRouting,
            "schema routing must observe the tenant selected from the validated principal");
        schemaRouting.Should().BeLessThan(statusEnforcement,
            "suspended/deleted tenant enforcement must run on the routed effective tenant");
        requestRateLimit.Should().BeGreaterThan(tenantContext,
            "the request rate limiter partitions by the validated tenant and authenticated actor");
        requestRateLimit.Should().BeLessThan(schemaRouting,
            "failed schema resolution must consume the configured tenant/actor rate-limit bucket");
    }

    [ArchitectureTest]
    public void CanonicalActorIdentity_QualifiesSubjectsByIssuer_AndUsesImmutableIdentifiers()
    {
        var source = ReadRepositoryFile(CanonicalActorRelativePath);

        source.Should().Contain(
            "$\"{scheme}:subject:{Encode(issuer ?? \"-\")}:{Encode(subject)}\"",
            "an OIDC actor id must be qualified by its validated issuer so the same sub from two "
            + "authorities cannot collide (#3430)");
        source.Should().Contain(
            "$\"{scheme}:api-key:{apiKeyId:D}\"",
            "an API-key actor id must be the immutable key id, never a mutable display name");
        source.Should().Contain(
            "if (string.Equals(scheme, AuthenticationExtensions.ApiKeyScheme, StringComparison.OrdinalIgnoreCase))",
            "an API-key principal without an immutable id must fail closed");
        source.Should().NotContain(
            "new CanonicalSecurityActorIdentity(\"admin:bootstrap\"",
            "bootstrap authentication must use a handler-stamped immutable id, not a special display-name fallback");
        source.Should().Contain(
            "Replace(identity, \"honua:issuer\", actor.SubjectIssuer);",
            "the validated issuer must be stamped onto the request principal by the framework");
        source.Should().Contain(
            "Replace(identity, EffectiveTenantClaim, NormalizeValue(effectiveTenant));",
            "the effective tenant must be stamped onto the request principal by the framework");
        source.Should().Contain(
            "claim.Properties[FrameworkOwnedClaimProperty] = bool.TrueString;",
            "stamped binding claims must carry in-memory provenance so a token payload cannot forge them");
    }

    [ArchitectureTest]
    public void SessionBindingKey_RetainsActorTenantScopeAndCredentialComponents()
    {
        var source = ReadRepositoryFile(CanonicalActorRelativePath);

        source.Should().Contain(
            "$\"{actor.ActorId}:tenant:{Encode(tenant)}:scope:{Encode(scopeCeiling)}"
            + ":credential:{Encode(credential)}\"",
            "the session binding key must retain the canonical actor, effective tenant, scope ceiling, "
            + "and validated-credential fingerprint; dropping any component lets cross-issuer or "
            + "cross-tenant callers share a session (#3430)");

        var buildBindingKey = ExtractRegion(
            source,
            "internal static string BuildBindingKey(",
            "internal static string ResolveScopeCeiling(");
        buildBindingKey.Should().Contain("Encode(tenant)")
            .And.Contain("Encode(scopeCeiling)")
            .And.Contain("Encode(credential)");
        buildBindingKey.Should().NotContain("identity.Name",
            "mutable display names are never session identifiers");
    }

    [ArchitectureTest]
    public void McpSessionBinding_FailsClosed_ForBearersWithoutDurableIssuerBoundIdentity()
    {
        var source = ReadRepositoryFile(AuthorizationHelperRelativePath);

        var resolve = ExtractRegion(
            source,
            "public static string? ResolveSessionBindingKey(HttpContext context)",
            "private static string? ResolveBearerCredentialFingerprint(HttpContext context)");

        resolve.Should().Contain("CanonicalSecurityActor.Resolve(context.User)");
        resolve.Should().Contain("CanonicalSecurityActor.BuildBindingKey(",
            "MCP must reuse the one framework-owned binding key rather than a protocol-local identity");
        resolve.Should().Contain("actor.IsDurablyRevalidatable")
            .And.Contain("string.IsNullOrWhiteSpace(actor.SubjectIssuer)",
                "a bearer session requires a durable, issuer-qualified actor and must otherwise fail closed");
        resolve.Should().Contain("CanonicalSecurityActor.EffectiveTenantClaim",
            "the tenant component must come from the resolved tenant context or the framework-stamped "
            + "effective-tenant claim");
        resolve.Should().NotContain("Request.Headers",
            "client-supplied issuer, subject, and tenant headers must never feed the session binding key");
        resolve.Should().NotContain("ResolvePrincipalKey",
            "the scheme+subject audit key is not issuer/tenant qualified and cannot bind a session");
    }

    [ArchitectureTest]
    public void McpTransport_DerivesEverySessionKeyFromTheCanonicalBinding()
    {
        var source = ReadRepositoryFile(EndpointRelativePath);

        source.Should().NotContain("ResolvePrincipalKey",
            "the MCP transport must key sessions on ResolveSessionBindingKey, which is issuer-, "
            + "tenant-, scope-, and credential-qualified (#3430)");

        var bindingSites = CountOccurrences(source, "McpAuthorizationHelper.ResolveSessionBindingKey(context)");
        bindingSites.Should().Be(3,
            "the POST, GET/SSE, and DELETE transport entrypoints each resolve the canonical binding key");

        var accessSites = CountOccurrences(source, "sessions.ValidateAccess(")
            + CountOccurrences(source, "sessions.TryCreateSession(");
        accessSites.Should().Be(4,
            "every session create/access call site is accounted for by this guard; a new one must be "
            + "reviewed for canonical binding before the count is raised");

        source.Should().Contain("if (principalKey is null)",
            "an authenticated caller without a durable canonical binding must fail closed rather than "
            + "fall back to the anonymous session namespace");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var path = ArchitectureTestHelpers.CombinePath(repoRoot, relativePath);
        File.Exists(path).Should().BeTrue($"'{relativePath}' is under architecture guard and must exist");
        return File.ReadAllText(path);
    }

    private static int RequireIndex(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        index.Should().BeGreaterThan(-1, $"'{marker}' is an ordering anchor for the MCP authority boundary");
        return index;
    }

    private static string ExtractRegion(string source, string startMarker, string endMarker)
    {
        var start = RequireIndex(source, startMarker);
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"'{endMarker}' must follow '{startMarker}'");
        return source[start..end];
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = source.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
