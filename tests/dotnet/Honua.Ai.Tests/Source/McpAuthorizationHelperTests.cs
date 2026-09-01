// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Diagnostics;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Tests for MCP principal key derivation and session-binding identity selection.
/// </summary>
public sealed class McpAuthorizationHelperTests
{
    [UnitTest]
    public void CanonicalActor_ApiKeyWithoutImmutableId_FailsClosed()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "mutable-display-name"),
        ], AuthenticationExtensions.ApiKeyScheme));

        CanonicalSecurityActor.Resolve(principal).Should().BeNull();
    }

    [UnitTest]
    public void EnrichActivity_StampedTenant_AddsTenantDimension()
    {
        var context = CreateBearerContext("operator", "https://issuer.example", "tenant-a");
        CanonicalSecurityActor.StampRequestBinding(context.User, "tenant-a");
        using var listener = new ActivityListener
        {
            ShouldListenTo = static _ => true,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource("Honua.Tests.McpTenant");
        using var activity = source.StartActivity("mcp-test");

        McpTelemetry.EnrichActivity("tools/call", context);

        activity.Should().NotBeNull();
        activity!.GetTagItem("honua.tenant.id").Should().Be("tenant-a");
    }

    [UnitTest]
    public void ResolvePrincipalKey_IncludesAuthenticationScheme_WithNameIdentifier()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "operator-123"),
            new Claim(ClaimTypes.Name, "Operator One")
        ], "JwtBearer"));

        McpAuthorizationHelper.ResolvePrincipalKey(principal).Should().Be("JwtBearer:sub:operator-123");
    }

    [UnitTest]
    public void ResolvePrincipalKey_IncludesAuthenticationScheme_WithNameWhenNoSubject()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, "admin")
        ], "ApiKey"));

        McpAuthorizationHelper.ResolvePrincipalKey(principal).Should().Be("ApiKey:name:admin");
    }

    [UnitTest]
    public void ResolvePrincipalKey_UsesAnonymousForUnauthenticated()
    {
        var principal = new ClaimsPrincipal();

        McpAuthorizationHelper.ResolvePrincipalKey(principal).Should().Be(McpSessionManager.AnonymousPrincipalKey);
    }

    [UnitTest]
    public void ResolvePrincipalKey_DifferentSchemesYieldDifferentKeys()
    {
        var bearer = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "identity-1")
        ], "JwtBearer"));
        var apiKey = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "identity-1"),
            new Claim(ClaimTypes.Name, "identity-1")
        ], "ApiKey"));

        McpAuthorizationHelper.ResolvePrincipalKey(bearer).Should().Be("JwtBearer:sub:identity-1");
        McpAuthorizationHelper.ResolvePrincipalKey(apiKey).Should().Be("ApiKey:sub:identity-1");
    }

    [UnitTest]
    public void ResolveSessionBindingKey_SameSubjectAcrossIssuersAndTenants_DoesNotCollide()
    {
        var issuerA = CreateBearerContext("same-subject", "https://issuer-a.example", "tenant-a");
        var issuerB = CreateBearerContext("same-subject", "https://issuer-b.example", "tenant-a");
        var tenantB = CreateBearerContext("same-subject", "https://issuer-a.example", "tenant-b");

        var first = McpAuthorizationHelper.ResolveSessionBindingKey(issuerA);

        first.Should().NotBe(McpAuthorizationHelper.ResolveSessionBindingKey(issuerB));
        first.Should().NotBe(McpAuthorizationHelper.ResolveSessionBindingKey(tenantB));
    }

    [UnitTest]
    public void ResolveSessionBindingKey_DelimiterBearingComponents_AreCollisionFree()
    {
        var first = CreateBearerContext("alice:tenant:a", "https://issuer.example", "b");
        var second = CreateBearerContext("alice", "https://issuer.example", "a:tenant:b");

        McpAuthorizationHelper.ResolveSessionBindingKey(first)
            .Should().NotBe(McpAuthorizationHelper.ResolveSessionBindingKey(second));
    }

    [UnitTest]
    public void ResolveSessionBindingKey_UriScopeClaims_AreIncludedInCeiling()
    {
        var read = CreateBearerContext(
            "same-subject",
            "https://issuer.example",
            "tenant-a",
            new Claim(OperatorScopeCatalog.ScopeClaimUri, OperatorScopeCatalog.Read));
        var full = CreateBearerContext(
            "same-subject",
            "https://issuer.example",
            "tenant-a",
            new Claim(OperatorScopeCatalog.ScopeClaimUri, OperatorScopeCatalog.Full));

        McpAuthorizationHelper.ResolveSessionBindingKey(read)
            .Should().NotBe(McpAuthorizationHelper.ResolveSessionBindingKey(full));
    }

    [UnitTest]
    public void ResolveSessionBindingKey_CommaDelimitedScopes_DoNotMatchAuthorizedWhitespaceSet()
    {
        var rejectedCommaValue = CreateBearerContext(
            "same-subject",
            "https://issuer.example",
            "tenant-a",
            new Claim("scope", $"{OperatorScopeCatalog.Read},{OperatorScopeCatalog.Execute}"));
        var recognizedWhitespaceValue = CreateBearerContext(
            "same-subject",
            "https://issuer.example",
            "tenant-a",
            new Claim("scope", $"{OperatorScopeCatalog.Read} {OperatorScopeCatalog.Execute}"));

        McpAuthorizationHelper.ResolveSessionBindingKey(rejectedCommaValue)
            .Should().NotBe(McpAuthorizationHelper.ResolveSessionBindingKey(recognizedWhitespaceValue));
    }

    [UnitTest]
    public void ResolveSessionBindingKey_MissingTenant_DoesNotCollideWithLiteralDashTenant()
    {
        var missing = CreateBearerContext("same-subject", "https://issuer.example", tenant: null);
        var literalDash = CreateBearerContext("same-subject", "https://issuer.example", tenant: "-");

        McpAuthorizationHelper.ResolveSessionBindingKey(missing)
            .Should().NotBe(McpAuthorizationHelper.ResolveSessionBindingKey(literalDash));
    }

    [UnitTest]
    public void ResolveSessionBindingKey_ApiKeysUseImmutableKeyId()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = CreateApiKeyContext(firstId, "tenant-a");
        var second = CreateApiKeyContext(secondId, "tenant-a");

        var firstBinding = McpAuthorizationHelper.ResolveSessionBindingKey(first);
        var secondBinding = McpAuthorizationHelper.ResolveSessionBindingKey(second);

        firstBinding.Should().NotBe(secondBinding);
        firstBinding.Should().Contain(firstId.ToString("D"));
        secondBinding.Should().Contain(secondId.ToString("D"));
    }

    [UnitTest]
    public void ResolveSessionBindingKey_BearerCannotForgeApiKeyIdentity()
    {
        var apiKeyId = Guid.NewGuid();
        var apiKey = CreateApiKeyContext(apiKeyId, "tenant-a");
        var bearer = CreateBearerContext(
            "bearer-subject",
            "https://issuer.example",
            "tenant-a",
            new Claim("api_key_id", apiKeyId.ToString("D")));

        McpAuthorizationHelper.ResolveSessionBindingKey(bearer)
            .Should().NotBe(McpAuthorizationHelper.ResolveSessionBindingKey(apiKey));
    }

    [UnitTest]
    public void ResolveSessionBindingKey_AuthenticatedWithoutDurableActor_DoesNotBecomeAnonymous()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("iss", "https://issuer.example"),
            new Claim("client_id", "machine-client"),
            new Claim(ClaimTypes.Name, "Mutable Machine Display Name"),
        ], OidcAuthenticationExtensions.JwtBearerScheme));
        var context = CreateContext(principal, "tenant-a");

        McpAuthorizationHelper.ResolveSessionBindingKey(context).Should().BeNull();
    }

    [UnitTest]
    public void ResolveSessionBindingKey_BearerSubjectWithoutIssuer_IsRejected()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "subject-without-issuer"),
        ], OidcAuthenticationExtensions.JwtBearerScheme));
        var context = CreateContext(principal, "tenant-a");

        McpAuthorizationHelper.ResolveSessionBindingKey(context).Should().BeNull();
    }

    [UnitTest]
    public void ResolveSessionBindingKey_SameClaimsWithDifferentBearerCredentials_DoNotCollide()
    {
        var first = CreateBearerContext(
            "same-subject",
            "https://issuer.example",
            "tenant-a",
            new Claim("roles", "admin"));
        var second = CreateBearerContext(
            "same-subject",
            "https://issuer.example",
            "tenant-a",
            new Claim("roles", "admin"));
        first.Request.Headers.Authorization = "Bearer credential-a";
        second.Request.Headers.Authorization = "Bearer credential-b";

        McpAuthorizationHelper.ResolveSessionBindingKey(first)
            .Should().NotBe(McpAuthorizationHelper.ResolveSessionBindingKey(second));
    }

    [UnitTest]
    public void ResolveSessionBindingKey_BearerWithoutPresentedCredential_FailsClosed()
    {
        var context = CreateBearerContext("subject", "https://issuer.example", "tenant-a");
        context.Request.Headers.Remove("Authorization");

        McpAuthorizationHelper.ResolveSessionBindingKey(context).Should().BeNull();
    }

    [UnitTest]
    public void CreateTrustedBearerPrincipal_StripsFrameworkOwnedClaims()
    {
        var forged = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "subject-1"),
            new Claim("iss", "https://issuer.example"),
            new Claim(CanonicalSecurityActor.CanonicalActorClaim, "forged-actor"),
            new Claim(CanonicalSecurityActor.EffectiveTenantClaim, "forged-tenant"),
            new Claim(CanonicalSecurityActor.ScopeCeilingClaim, "forged-scope"),
            new Claim("honua:auth_scheme", "ApiKey"),
            new Claim("honua:issuer", "forged-issuer"),
            new Claim(OperatorScopeCatalog.ScopeGovernedClaimType, "forged-marker"),
        ], "issuer-controlled"));
        var trustedMarker = new Claim(
            OperatorScopeCatalog.ScopeGovernedClaimType,
            OperatorScopeCatalog.ScopeGovernedClaimValue);
        trustedMarker.Properties[CanonicalSecurityActor.FrameworkOwnedClaimProperty] = bool.TrueString;
        forged.AddIdentity(new ClaimsIdentity([trustedMarker]));
        var result = AuthenticateResult.Success(new AuthenticationTicket(
            forged,
            OidcAuthenticationExtensions.JwtBearerScheme));

        var promoted = McpBearerAuthenticationEndpointExtensions.CreateTrustedBearerPrincipal(result);

        promoted.Should().NotBeNull();
        promoted!.Claims.Should().NotContain(claim =>
            claim.Type.StartsWith("honua:", StringComparison.OrdinalIgnoreCase)
            && claim.Type != OperatorScopeCatalog.ScopeGovernedClaimType);
        promoted.FindAll(OperatorScopeCatalog.ScopeGovernedClaimType)
            .Should().ContainSingle(claim =>
                claim.Value == OperatorScopeCatalog.ScopeGovernedClaimValue
                && CanonicalSecurityActor.IsFrameworkOwnedClaim(claim));
        promoted.FindFirst("sub")?.Value.Should().Be("subject-1");
        promoted.FindFirst("iss")?.Value.Should().Be("https://issuer.example");
    }

    [UnitTest]
    public async Task EnsureBearerDataTenantAsync_TenantlessBearer_IsRejectedAndAudited()
    {
        var context = CreateBearerContext("subject", "https://issuer.example", tenant: null);
        var auditLog = new CapturingAuditLog();
        context.RequestServices = new ServiceCollection()
            .AddSingleton<ITenantContext>(new StubTenantContext(tenantId: null))
            .AddSingleton<IAuditLog>(auditLog)
            .BuildServiceProvider();

        var act = () => McpAuthorizationHelper.EnsureBearerDataTenantAsync(context, "tools/call");

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("A validated tenant is required to invoke MCP tools.");
        auditLog.Events.Should().ContainSingle();
        auditLog.Events[0].EventType.Should().Be(AuditEventType.Authorization);
        auditLog.Events[0].Actor.Should().Be("subject");
        auditLog.Events[0].ActorType.Should().Be(AuditActorType.UserId);
        auditLog.Events[0].ResourceType.Should().Be("mcp");
        auditLog.Events[0].ResourceId.Should().Be("tools/call");
        auditLog.Events[0].Action.Should().Be("mcp.authorization");
        auditLog.Events[0].Outcome.Should().Be(AuditOutcome.Denied);
        auditLog.Events[0].Details.Should().Be("{\"code\":\"tenant_required\"}");
    }

    [UnitTest]
    public async Task EnsureBearerDataTenantAsync_ApiKeyPath_IsUnchanged()
    {
        var context = CreateApiKeyContext(Guid.NewGuid(), tenant: null);

        var act = () => McpAuthorizationHelper.EnsureBearerDataTenantAsync(context, "tools/call");

        await act.Should().NotThrowAsync();
    }

    [UnitTest]
    public async Task EnsureBearerDataTenantAsync_TenantResolutionDisabled_AllowsSingleTenantBearerDataCalls()
    {
        var context = CreateBearerContext("subject", "https://issuer.example", tenant: null);
        var auditLog = new CapturingAuditLog();
        context.RequestServices = new ServiceCollection()
            .AddSingleton<ITenantContext>(new StubTenantContext(tenantId: null))
            .AddSingleton<IAuditLog>(auditLog)
            .AddSingleton(Options.Create(new TenantContextOptions { Enabled = false }))
            .BuildServiceProvider();

        foreach (var target in new[] { "tools/call", "resources/read" })
        {
            var act = () => McpAuthorizationHelper.EnsureBearerDataTenantAsync(context, target);

            await act.Should().NotThrowAsync();
        }

        auditLog.Events.Should().BeEmpty();
    }

    [UnitTest]
    public async Task BearerWithoutConfiguredAuthority_RemainsAnonymousAndCannotAuthorizeTool()
    {
        var services = new ServiceCollection();
        services.AddOptions<OidcAuthenticationOptions>();
        await using var provider = services.BuildServiceProvider();
        var app = new ApplicationBuilder(provider);
        Exception? authorizationFailure = null;
        var discoveryReached = false;

        app.UseMcpBearerAuthentication();
        app.Run(context =>
        {
            discoveryReached = true;
            authorizationFailure = Record.Exception(() => McpAuthorizationHelper.EnsurePrincipal(context));

            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/mcp";
        context.Request.Headers.Authorization = "Bearer cannot-be-validated";

        await app.Build()(context);

        discoveryReached.Should().BeTrue("anonymous discovery must remain reachable");
        (context.User.Identity?.IsAuthenticated ?? false).Should().BeFalse();
        McpBearerAuthenticationEndpointExtensions.HasAuthenticationFailure(context).Should().BeFalse();
        authorizationFailure.Should().NotBeNull("tool authorization must still reject the anonymous caller");
    }

    [UnitTest]
    public async Task InvalidBearer_IsRejectedBeforeTenantResolutionMiddlewareRuns()
    {
        var authentication = new CountingAuthenticationService(
            AuthenticateResult.Fail("invalid bearer"));
        var oidc = new OidcAuthenticationOptions
        {
            Enabled = true,
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = "https://issuer.example",
                ClientId = "mcp-client",
            },
        };
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IAuthenticationService>(authentication)
            .AddSingleton<IOptions<OidcAuthenticationOptions>>(Options.Create(oidc))
            .BuildServiceProvider();
        await using var provider = services;
        var app = new ApplicationBuilder(provider);
        var tenantResolutionReached = false;

        app.UseMcpBearerAuthentication();
        app.UseWhen(
            McpBearerAuthenticationEndpointExtensions.HasAuthenticationFailure,
            branch => branch.UseMcpBearerAuthenticationRejection());
        app.Run(_ =>
        {
            tenantResolutionReached = true;
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Response.Body = new MemoryStream();
        context.Request.Path = "/mcp";
        context.Request.Headers.Authorization = "Bearer invalid-token";

        await app.Build()(context);

        authentication.AuthenticateCount.Should().Be(1);
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        tenantResolutionReached.Should().BeFalse(
            "a rejected credential must terminate before tenant resolution can observe the request");
    }

    [UnitTest]
    public async Task ValidBearer_IsAuthenticatedBeforeTenantResolution_ThenBoundToResolvedTenant()
    {
        var sourcePrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "shared-subject"),
            new Claim("iss", "https://issuer.example"),
            new Claim("tenant_id", "tenant-a"),
        ], OidcAuthenticationExtensions.JwtBearerScheme));
        var authentication = new CountingAuthenticationService(AuthenticateResult.Success(
            new AuthenticationTicket(sourcePrincipal, OidcAuthenticationExtensions.JwtBearerScheme)));
        var oidc = new OidcAuthenticationOptions
        {
            Enabled = true,
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = "https://issuer.example",
                ClientId = "mcp-client",
            },
        };
        var serviceCollection = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IAuthenticationService>(authentication)
            .AddSingleton<IOptions<OidcAuthenticationOptions>>(Options.Create(oidc));
        serviceCollection.AddHonuaTenantContext(new ConfigurationBuilder().Build());
        var services = serviceCollection.BuildServiceProvider();
        await using var provider = services;
        var app = new ApplicationBuilder(provider);
        string? bindingSeenByEndpoint = null;
        ITenantContext? tenantSeenByEndpoint = null;

        app.UseMcpBearerAuthentication();
        app.UseHonuaTenantContext();
        app.Run(context =>
        {
            tenantSeenByEndpoint = context.RequestServices.GetRequiredService<ITenantContext>();
            bindingSeenByEndpoint = McpAuthorizationHelper.ResolveSessionBindingKey(context);
            return Task.CompletedTask;
        });

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/mcp";
        context.Request.Headers.Authorization = "Bearer validated-token";

        await app.Build()(context);

        authentication.AuthenticateCount.Should().Be(1);
        tenantSeenByEndpoint.Should().NotBeNull();
        tenantSeenByEndpoint!.TenantId.Should().Be("tenant-a");
        tenantSeenByEndpoint.Source.Should().Be(TenantContextSource.Claim);
        bindingSeenByEndpoint.Should().Contain("subject:https%3A%2F%2Fissuer.example:shared-subject");
        bindingSeenByEndpoint.Should().Contain("tenant:value%3Atenant-a");
    }

    [UnitTest]
    public async Task EndpointFilter_PreservesEarlyValidatedTenantStampedPrincipal()
    {
        var sourcePrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "shared-subject"),
            new Claim("iss", "https://issuer-a.example"),
        ], OidcAuthenticationExtensions.JwtBearerScheme));
        var authentication = new CountingAuthenticationService(AuthenticateResult.Success(
            new AuthenticationTicket(sourcePrincipal, OidcAuthenticationExtensions.JwtBearerScheme)));
        var oidc = new OidcAuthenticationOptions
        {
            Enabled = true,
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = "https://issuer-a.example",
                ClientId = "mcp-client",
            },
        };
        var services = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(authentication)
            .AddSingleton<IOptions<OidcAuthenticationOptions>>(Options.Create(oidc))
            .BuildServiceProvider();
        await using var provider = services;
        var app = new ApplicationBuilder(provider);
        ClaimsPrincipal? principalSeenByEndpoint = null;

        app.UseMcpBearerAuthentication();
        app.Run(async httpContext =>
        {
            CanonicalSecurityActor.StampRequestBinding(httpContext.User, "tenant-a");
            await McpBearerAuthenticationEndpointExtensions.AuthenticateBearerAsync(
                EndpointFilterInvocationContext.Create(httpContext),
                invocation =>
                {
                    principalSeenByEndpoint = invocation.HttpContext.User;
                    return ValueTask.FromResult<object?>(null);
                });
        });

        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/mcp";
        context.Request.Headers.Authorization = "Bearer validated-token";

        await app.Build()(context);

        authentication.AuthenticateCount.Should().Be(1, "the endpoint filter must reuse early validation");
        principalSeenByEndpoint.Should().BeSameAs(context.User);
        CanonicalSecurityActor.FindStampedValue(context.User, CanonicalSecurityActor.CanonicalActorClaim)
            .Should().Be(
                $"{OidcAuthenticationExtensions.JwtBearerScheme.ToLowerInvariant()}:" +
                "subject:https%3A%2F%2Fissuer-a.example:shared-subject");
        CanonicalSecurityActor.FindStampedValue(context.User, CanonicalSecurityActor.EffectiveTenantClaim)
            .Should().Be("tenant-a");
    }

    private static DefaultHttpContext CreateBearerContext(
        string subject,
        string issuer,
        string? tenant,
        params Claim[] additionalClaims)
    {
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("iss", issuer),
        };
        claims.AddRange(additionalClaims);
        var context = CreateContext(new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")), tenant);
        context.Request.Headers.Authorization = "Bearer unit-test-credential";
        return context;
    }

    private static DefaultHttpContext CreateApiKeyContext(Guid apiKeyId, string? tenant)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("api_key_id", apiKeyId.ToString("D")),
            new Claim(ClaimTypes.Name, "admin"),
        ], "ApiKey"));
        return CreateContext(principal, tenant);
    }

    private sealed class CountingAuthenticationService(AuthenticateResult result) : IAuthenticationService
    {
        public int AuthenticateCount { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            AuthenticateCount++;
            return Task.FromResult(result);
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(
            HttpContext context,
            string? scheme,
            ClaimsPrincipal principal,
            AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;
    }

    private sealed class CapturingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.FromResult<string?>("audit-test");
        }
    }

    private static DefaultHttpContext CreateContext(ClaimsPrincipal principal, string? tenant)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StubTenantContext(tenant));

        return new DefaultHttpContext
        {
            User = principal,
            RequestServices = services.BuildServiceProvider(),
        };
    }

    private sealed class StubTenantContext(string? tenantId) : ITenantContext
    {
        public string? TenantId => tenantId;

        public TenantContextSource Source => tenantId is null
            ? TenantContextSource.Anonymous
            : TenantContextSource.Claim;

        public bool RequireTenantId(out string tenantIdValue, out string? reason)
        {
            tenantIdValue = tenantId ?? string.Empty;
            reason = tenantId is null ? "Tenant required." : null;
            return tenantId is not null;
        }
    }

    [Fact]
    public void ResolveDistinctPrincipalKey_WithOnlyRawSubject_FailsClosed()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "identity-1"),
        ], "JwtBearer"));

        McpAuthorizationHelper.ResolvePrincipalKey(principal).Should().Be("JwtBearer:authenticated");
    }
}
