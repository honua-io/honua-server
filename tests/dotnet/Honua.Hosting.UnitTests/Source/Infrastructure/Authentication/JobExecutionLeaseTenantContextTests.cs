// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Hosting.UnitTests.Infrastructure.Authentication;

public sealed class JobExecutionLeaseTenantContextTests
{
    [Fact]
    public void ExactCurrentRequestRetainsSelectedTenantAcrossFreshAuthorityScope()
    {
        var accessor = new HttpContextAccessor();
        using var services = Services(accessor);
        using var request = services.CreateScope();
        using var authority = services.CreateScope();
        var caller = Agent();
        ((ClaimsIdentity)caller.Identity!).AddClaim(new Claim("tenant_id", "raw-token-tenant"));
        request.ServiceProvider.GetRequiredService<RequestTenantContext>().Set("selected-tenant", TenantContextSource.Header);
        accessor.HttpContext = new DefaultHttpContext { User = caller, RequestServices = request.ServiceProvider };
        try
        {
            JobExecutionLeaseTenantContext.BindRequestTenant(authority.ServiceProvider, caller);

            var retained = authority.ServiceProvider.GetRequiredService<RequestTenantContext>();
            retained.TenantId.Should().Be("selected-tenant");
            retained.Source.Should().Be(TenantContextSource.Header);
            caller.FindFirst("tenant_id")!.Value.Should().Be("raw-token-tenant");
        }
        finally { accessor.HttpContext = null; }
    }

    [Fact]
    public void UnresolvedRequestContextCannotStandInForMiddlewareSelectedTenant()
    {
        var accessor = new HttpContextAccessor();
        using var services = Services(accessor);
        using var request = services.CreateScope();
        using var authority = services.CreateScope();
        var caller = Agent();
        accessor.HttpContext = new DefaultHttpContext { User = caller, RequestServices = request.ServiceProvider };
        try
        {
            var bind = () => JobExecutionLeaseTenantContext.BindRequestTenant(authority.ServiceProvider, caller);
            bind.Should().Throw<UnauthorizedAccessException>();
            authority.ServiceProvider.GetRequiredService<RequestTenantContext>().IsInitialized.Should().BeFalse();
        }
        finally { accessor.HttpContext = null; }
    }

    [Fact]
    public void MatchingClaimStringsDoNotBorrowAnotherRequestPrincipalTenant()
    {
        var accessor = new HttpContextAccessor();
        using var services = Services(accessor);
        using var request = services.CreateScope();
        using var authority = services.CreateScope();
        var caller = Agent();
        CanonicalSecurityActor.StampRequestBinding(caller, "selected-tenant");
        var supplied = new ClaimsPrincipal(new ClaimsIdentity(caller.Claims.Select(claim =>
            new Claim(claim.Type, claim.Value)), caller.Identity!.AuthenticationType));
        request.ServiceProvider.GetRequiredService<RequestTenantContext>().Set("selected-tenant", TenantContextSource.Header);
        accessor.HttpContext = new DefaultHttpContext { User = caller, RequestServices = request.ServiceProvider };
        try
        {
            JobExecutionLeaseTenantContext.BindRequestTenant(authority.ServiceProvider, supplied);
            var retained = authority.ServiceProvider.GetRequiredService<RequestTenantContext>();
            retained.TenantId.Should().BeNull();
            retained.IsInitialized.Should().BeFalse();
        }
        finally { accessor.HttpContext = null; }
    }

    [Fact]
    public void DetachedObserverUsesOnlyItsExistingFrameworkSelectedTenant()
    {
        using var services = Services(new HttpContextAccessor());
        using var authority = services.CreateScope();
        var observer = Agent();
        ((ClaimsIdentity)observer.Identity!).AddClaim(new Claim("tenant_id", "raw-token-tenant"));
        CanonicalSecurityActor.StampRequestBinding(observer, "selected-tenant");

        JobExecutionLeaseTenantContext.BindRequestTenant(authority.ServiceProvider, observer);

        authority.ServiceProvider.GetRequiredService<RequestTenantContext>().TenantId.Should().Be("selected-tenant");
    }

    [Fact]
    public void CallerSuppliedTenantAndJobMetadataCannotInitializeAuthorityScope()
    {
        using var services = Services(new HttpContextAccessor());
        using var authority = services.CreateScope();
        var caller = Agent();
        var identity = (ClaimsIdentity)caller.Identity!;
        identity.AddClaim(new Claim("tenant_id", "raw-token-tenant"));
        identity.AddClaim(new Claim(CanonicalSecurityActor.CanonicalActorClaim, "forged-actor"));
        identity.AddClaim(new Claim(CanonicalSecurityActor.EffectiveTenantClaim, "forged-tenant"));
        using var job = JobSecurityScope.Begin(new JobSecurityContext("caller", "job-metadata-tenant", [], "role")
        { OwnerActorId = "caller" });

        JobExecutionLeaseTenantContext.BindRequestTenant(authority.ServiceProvider, caller);

        var retained = authority.ServiceProvider.GetRequiredService<RequestTenantContext>();
        retained.TenantId.Should().BeNull();
        retained.IsInitialized.Should().BeFalse();
    }

    [Theory]
    [InlineData("conflicting-tenant")]
    [InlineData(null)]
    public void PreinitializedTenantIncludingExplicitUnscopedContextCannotBeOverwritten(string? existing)
    {
        using var services = Services(new HttpContextAccessor());
        using var authority = services.CreateScope();
        var observer = Agent();
        CanonicalSecurityActor.StampRequestBinding(observer, "selected-tenant");
        var retained = authority.ServiceProvider.GetRequiredService<RequestTenantContext>();
        retained.Set(existing, existing is null ? TenantContextSource.Anonymous : TenantContextSource.Header);

        var bind = () => JobExecutionLeaseTenantContext.BindRequestTenant(authority.ServiceProvider, observer);

        bind.Should().Throw<UnauthorizedAccessException>();
        retained.TenantId.Should().Be(existing);
    }

    [Fact]
    public void ExplicitlyUnscopedStampedObserverNeverFallsBackToRawTokenTenant()
    {
        using var services = Services(new HttpContextAccessor());
        using var authority = services.CreateScope();
        var observer = Agent();
        ((ClaimsIdentity)observer.Identity!).AddClaim(new Claim("tenant_id", "raw-token-tenant"));
        CanonicalSecurityActor.StampRequestBinding(observer, null);

        JobExecutionLeaseTenantContext.BindRequestTenant(authority.ServiceProvider, observer);

        var retained = authority.ServiceProvider.GetRequiredService<RequestTenantContext>();
        retained.TenantId.Should().BeNull();
        retained.IsInitialized.Should().BeTrue();
    }

    private static ClaimsPrincipal Agent() => ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(
        Guid.NewGuid(), "tenant-binding-agent", ["agent:workspace", "admin:read"]);

    private static ServiceProvider Services(IHttpContextAccessor accessor) => new ServiceCollection()
        .AddSingleton(accessor)
        .AddScoped<RequestTenantContext>()
        .AddScoped<ITenantContext>(provider => provider.GetRequiredService<RequestTenantContext>())
        .BuildServiceProvider(validateScopes: true);
}
