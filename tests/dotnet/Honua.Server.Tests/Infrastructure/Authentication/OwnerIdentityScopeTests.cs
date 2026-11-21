// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Security)]
public sealed class OwnerIdentityScopeTests
{
    private const string GoogleIssuer = "https://accounts.google.com";
    private const string GenericIssuer = "https://idp.example.com/generic";
    private const string Subject = "shared-subject";

    [UnitTest]
    public async Task SingleProvider_KeepsBareSubjectOwner()
    {
        var services = Services(GoogleOnly());

        var principal = await TransformAsync(services, Bearer(GoogleIssuer, Subject));

        Assert.False(services.GetRequiredService<OwnerIdentityScope>().IsMultiProvider);
        Assert.Equal(Subject, ServiceDataEditorAuthorization.ResolveOwnerSubject(principal));
    }

    [UnitTest]
    public async Task TwoProviders_SameSubject_ResolveDifferentOwners()
    {
        var services = Services(GoogleAndGeneric());

        var google = await TransformAsync(services, Bearer(GoogleIssuer, Subject));
        var generic = await TransformAsync(services, Bearer(GenericIssuer, Subject));

        var googleOwner = ServiceDataEditorAuthorization.ResolveOwnerSubject(google);
        var genericOwner = ServiceDataEditorAuthorization.ResolveOwnerSubject(generic);
        Assert.Equal(OwnerIdentityScope.Qualify(OwnerIdentityScope.ProviderTag(GoogleIssuer), Subject), googleOwner);
        Assert.NotEqual(googleOwner, genericOwner);
        Assert.False(ReplicaSecurity.CanAccess(googleOwner, genericOwner, isAdmin: false));
    }

    [UnitTest]
    public async Task TwoProviders_LegacyBareOwner_MatchesOnlyAdmins()
    {
        var services = Services(GoogleAndGeneric());

        var caller = await TransformAsync(services, Bearer(GenericIssuer, Subject));
        var owner = ServiceDataEditorAuthorization.ResolveOwnerSubject(caller);

        Assert.False(ReplicaSecurity.CanAccess(Subject, owner, isAdmin: false));
        Assert.True(ReplicaSecurity.CanAccess(Subject, owner, isAdmin: true));
    }

    [UnitTest]
    public async Task TwoProviders_IssuerTrailingSlash_ResolvesSameOwner()
    {
        var services = Services(GoogleAndGeneric());

        var plain = await TransformAsync(services, Bearer(GenericIssuer, Subject));
        var slashed = await TransformAsync(services, Bearer(GenericIssuer + "/", Subject));

        Assert.Equal(
            ServiceDataEditorAuthorization.ResolveOwnerSubject(plain),
            ServiceDataEditorAuthorization.ResolveOwnerSubject(slashed));
    }

    [UnitTest]
    public async Task OidcPlusSaml_SamlSession_IsItsOwnProvider()
    {
        var services = Services(GoogleOnly(), new SamlSource());
        var session = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Subject)], "AdminAuthSession");
        session.AddClaim(CanonicalSecurityActor.CreateStampedClaim("auth_type", "saml"));

        var saml = await TransformAsync(services, new ClaimsPrincipal(session));
        var google = await TransformAsync(services, Bearer(GoogleIssuer, Subject));

        Assert.True(services.GetRequiredService<OwnerIdentityScope>().IsMultiProvider);
        Assert.Equal(
            OwnerIdentityScope.Qualify(OwnerIdentityScope.ProviderTag("saml:idp"), Subject),
            ServiceDataEditorAuthorization.ResolveOwnerSubject(saml));
        Assert.NotEqual(
            ServiceDataEditorAuthorization.ResolveOwnerSubject(saml),
            ServiceDataEditorAuthorization.ResolveOwnerSubject(google));
    }

    [UnitTest]
    public async Task TwoProviders_FederatedPortalToken_MatchesItsBearerOwner()
    {
        var services = Services(GoogleAndGeneric());
        var portal = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Subject)], "PortalToken");
        portal.AddClaim(CanonicalSecurityActor.CreateStampedClaim("auth_type", "portal-token"));
        portal.AddClaim(CanonicalSecurityActor.CreateStampedClaim(OwnerIdentityScope.SubjectIssuerClaim, GenericIssuer));

        var exchanged = await TransformAsync(services, new ClaimsPrincipal(portal));
        var bearer = await TransformAsync(services, Bearer(GenericIssuer, Subject));

        Assert.Equal(
            ServiceDataEditorAuthorization.ResolveOwnerSubject(bearer),
            ServiceDataEditorAuthorization.ResolveOwnerSubject(exchanged));
    }

    [UnitTest]
    public async Task IssuerSuppliedOwnerClaim_IsIgnored()
    {
        var services = Services(GoogleAndGeneric());
        var forged = Bearer(GoogleIssuer, Subject);
        ((ClaimsIdentity)forged.Identity!).AddClaim(new Claim(OwnerIdentityScope.OwnerClaim, "victim"));

        var principal = await TransformAsync(services, forged);

        Assert.Equal(
            OwnerIdentityScope.Qualify(OwnerIdentityScope.ProviderTag(GoogleIssuer), Subject),
            ServiceDataEditorAuthorization.ResolveOwnerSubject(principal));
        Assert.Equal("victim", ServiceDataEditorAuthorization.ResolveOwnerSubject(StampedOwner("victim")));
    }

    [UnitTest]
    public async Task SingleProvider_ExtraValidIssuerAlias_StaysSingleProvider()
    {
        var options = GoogleOnly();
        options.TokenValidation.ValidIssuers = ["https://google-alias.example.com"];
        var services = Services(options);

        var principal = await TransformAsync(services, Bearer("https://google-alias.example.com", Subject));

        Assert.False(services.GetRequiredService<OwnerIdentityScope>().IsMultiProvider);
        Assert.Equal(Subject, ServiceDataEditorAuthorization.ResolveOwnerSubject(principal));
    }

    [UnitTest]
    public async Task TwoProviders_LocalPrincipal_IsTaggedLocal()
    {
        var services = Services(GoogleAndGeneric());
        var key = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Subject)], "ApiKey");
        key.AddClaim(CanonicalSecurityActor.CreateStampedClaim("auth_type", "admin"));

        var principal = await TransformAsync(services, new ClaimsPrincipal(key));

        Assert.Equal(OwnerIdentityScope.Qualify("local", Subject), ServiceDataEditorAuthorization.ResolveOwnerSubject(principal));
        Assert.False(ReplicaSecurity.CanAccess(Subject, ServiceDataEditorAuthorization.ResolveOwnerSubject(principal), isAdmin: false));
    }

    private static ClaimsPrincipal StampedOwner(string owner)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Subject)], "Bearer");
        identity.AddClaim(CanonicalSecurityActor.CreateStampedClaim(OwnerIdentityScope.OwnerClaim, owner));
        return new ClaimsPrincipal(identity);
    }

    private static ClaimsPrincipal Bearer(string issuer, string subject)
        => new(new ClaimsIdentity([new Claim("iss", issuer), new Claim("sub", subject)], "Bearer"));

    private static Task<ClaimsPrincipal> TransformAsync(ServiceProvider services, ClaimsPrincipal principal)
        => new OidcClaimsTransformation(
                services.GetRequiredService<IOptions<OidcAuthenticationOptions>>(),
                NullLogger<OidcClaimsTransformation>.Instance,
                services)
            .TransformAsync(principal);

    private static ServiceProvider Services(OidcAuthenticationOptions options, params IExternalIdentityProviderSource[] external)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<IOidcRuntimeProviderSource>(new NoRuntimeProviders());
        foreach (var source in external)
        {
            services.AddSingleton(source);
        }

        services.AddSingleton<OwnerIdentityScope>();
        return services.BuildServiceProvider();
    }

    private static OidcAuthenticationOptions GoogleOnly() => new()
    {
        Enabled = true,
        Google = new GoogleProviderOptions { Enabled = true, ClientId = "google-client", ClientSecret = "secret" },
    };

    private static OidcAuthenticationOptions GoogleAndGeneric()
    {
        var options = GoogleOnly();
        options.Generic = new GenericOidcProviderOptions { Enabled = true, Authority = GenericIssuer, ClientId = "generic-client" };
        return options;
    }

    private sealed class NoRuntimeProviders : IOidcRuntimeProviderSource
    {
        public IReadOnlyList<OidcRuntimeProvider> Current => [];
    }

    private sealed class SamlSource : IExternalIdentityProviderSource
    {
        public IEnumerable<ExternalIdentityProviderRegistration> GetProviders()
            => [new ExternalIdentityProviderRegistration("saml:idp", "saml")];
    }
}
