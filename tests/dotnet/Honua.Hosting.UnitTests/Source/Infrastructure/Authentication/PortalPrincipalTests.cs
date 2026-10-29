// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Xunit;

namespace Honua.Hosting.UnitTests.Source.Infrastructure.Authentication;

public sealed class PortalPrincipalTests
{
    [Fact]
    public void ManagedKeyRequiresStampedImmutableIdentity()
    {
        var id = Guid.NewGuid();
        var identity = new ClaimsIdentity([new Claim("api_key_id", id.ToString("D")), new Claim("auth_type", "admin-api-key"), new Claim(ClaimTypes.Name, "same name")], AuthenticationExtensions.ApiKeyScheme);
        var principal = new ClaimsPrincipal(identity);
        Assert.Null(PortalPrincipal.Resolve(principal));
        CanonicalSecurityActor.StampAuthorityClaims(identity);
        Assert.Equal($"api-key:{id:D}", PortalPrincipal.Resolve(principal));
        var other = new ClaimsIdentity([new Claim("api_key_id", Guid.NewGuid().ToString("D")), new Claim("auth_type", "admin-api-key"), new Claim(ClaimTypes.Name, "same name")], AuthenticationExtensions.ApiKeyScheme);
        CanonicalSecurityActor.StampAuthorityClaims(other);
        Assert.NotEqual(PortalPrincipal.Resolve(principal), PortalPrincipal.Resolve(new ClaimsPrincipal(other)));
    }

    [Fact]
    public void UnauthenticatedStaticAndSpoofedCredentialsCannotBecomeMachineSubjects()
    {
        Assert.Null(PortalPrincipal.Resolve(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")]))));
        Assert.Null(PortalPrincipal.Resolve(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], AuthenticationExtensions.ApiKeyScheme))));
        var spoofed = new ClaimsIdentity([new Claim("api_key_id", Guid.NewGuid().ToString("D"))], "Bearer");
        CanonicalSecurityActor.StampAuthorityClaims(spoofed);
        Assert.Null(PortalPrincipal.Resolve(new ClaimsPrincipal(spoofed)));
        Assert.Null(PortalPrincipal.Resolve(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, $"api-key:{Guid.NewGuid():D}")], "Bearer"))));
        Assert.Equal("alice", PortalPrincipal.Resolve(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "alice")], "Bearer"))));
    }

    [Fact]
    public void BootstrapActorWithStampedGuidIsNotManagedKey()
    {
        var bootstrap = new ClaimsIdentity([new Claim("api_key_id", "00000000-0000-0000-0000-000000000002"), new Claim("auth_type", "admin")], AuthenticationExtensions.ApiKeyScheme);
        CanonicalSecurityActor.StampAuthorityClaims(bootstrap);
        Assert.Null(PortalPrincipal.Resolve(new ClaimsPrincipal(bootstrap)));
        var managed = new ClaimsIdentity([new Claim("api_key_id", Guid.NewGuid().ToString("D")), new Claim("auth_type", "admin-api-key")], AuthenticationExtensions.ApiKeyScheme);
        CanonicalSecurityActor.StampAuthorityClaims(managed);
        var subject = PortalPrincipal.Resolve(new ClaimsPrincipal(managed));
        Assert.NotNull(subject);
        Assert.StartsWith("api-key:", subject);
    }
}
