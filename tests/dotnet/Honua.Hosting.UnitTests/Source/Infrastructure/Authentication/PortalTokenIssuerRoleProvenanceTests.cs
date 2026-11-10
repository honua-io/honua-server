// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class PortalTokenIssuerRoleProvenanceTests
{
    private const string ClientIp = "192.0.2.12";

    [UnitTest]
    public async Task ValidateAsync_RolesFromRoleClaimSelection_AreKeptOnPro()
    {
        var roles = await RestoreRolesAsync(HonuaEdition.Pro, rolesDependOnlyOnRoleClaimSelection: true);

        roles.Should().BeEquivalentTo(
            ["viewer", "editor"],
            "role claim selection needs identity.oidc, which Pro includes without identity.claims-mapping");
    }

    [UnitTest]
    public async Task ValidateAsync_RolesFromRoleClaimSelection_FallBackOnCommunity()
    {
        var roles = await RestoreRolesAsync(HonuaEdition.Community, rolesDependOnlyOnRoleClaimSelection: true);

        roles.Should().Equal("viewer");
    }

    [UnitTest]
    public async Task ValidateAsync_RolesFromCustomMapping_FallBackOnProWithoutClaimsMapping()
    {
        var roles = await RestoreRolesAsync(HonuaEdition.Pro, rolesDependOnlyOnRoleClaimSelection: false);

        roles.Should().Equal("viewer");
    }

    private static async Task<string[]> RestoreRolesAsync(HonuaEdition edition, bool rolesDependOnlyOnRoleClaimSelection)
    {
        var issuer = CreateIssuer(edition);
        var issuance = await issuer.IssueAsync(
            new PortalTokenIssueRequest(
                PrincipalId: "alice",
                DisplayName: null,
                TenantId: null,
                Roles: ["viewer", "editor"],
                ClientType: PortalTokenClientType.Ip,
                BindingValue: ClientIp,
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30),
                RolesRequireClaimsMappingEntitlement: true,
                RolesWithoutClaimsMapping: ["viewer"])
            {
                RolesDependOnlyOnRoleClaimSelection = rolesDependOnlyOnRoleClaimSelection,
            },
            CancellationToken.None);

        var validation = await issuer.ValidateAsync(
            issuance.Token,
            new PortalTokenBinding(Referer: null, ClientIp: ClientIp),
            CancellationToken.None);

        return validation!.Principal.FindAll(ClaimTypes.Role).Select(static claim => claim.Value).ToArray();
    }

    private static PortalTokenIssuer CreateIssuer(HonuaEdition edition)
    {
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var services = new ServiceCollection()
            .AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(edition))
            .AddSingleton<IMemoryCache>(memoryCache)
            .AddSingleton(Options.Create(new PortalTokenAuthenticationOptions()))
            .AddSingleton<IPortalTokenSourceValidator>(provider => new PortalTokenSourceValidator(
                provider.GetRequiredService<IMemoryCache>(),
                provider.GetRequiredService<IOptions<PortalTokenAuthenticationOptions>>(),
                provider))
            .BuildServiceProvider();
        return new PortalTokenIssuer(memoryCache, NullLogger<PortalTokenIssuer>.Instance, serviceProvider: services);
    }
}
