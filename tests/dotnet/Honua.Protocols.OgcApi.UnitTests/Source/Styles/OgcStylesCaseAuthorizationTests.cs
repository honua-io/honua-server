// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.Ogc.Api.Styles;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Styles;

/// <summary>
/// SRV-OGC-008: style authorization uses the same ordinal name match as stylesheet resolution.
/// </summary>
public sealed class OgcStylesCaseAuthorizationTests
{
    [UnitTest]
    public async Task AuthorizeStyleAsync_CaseVariant_UsesTheOrdinalResourcePolicy()
    {
        var provider = new TestMetadataV2GraphBuilder()
            .AddResource(
                "style-parcels",
                "Parcels",
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddResource(
                "style-parcels-lower",
                "parcels",
                accessPolicy: new AccessPolicy { AllowedRoles = ["gis-admin"] })
            .AddService("maps", "maps", protocols: [ServiceProtocols.OgcApiMaps])
            .AddPublication("pub-parcels", "maps", "style-parcels", serviceLocalId: "Parcels")
            .AddPublication("pub-parcels-lower", "maps", "style-parcels-lower", serviceLocalId: "parcels")
            .BuildProvider();
        var context = CreateViewerContext();

        var denied = await OgcStylesEndpoints.AuthorizeStyleAsync(context, "parcels", provider, CancellationToken.None);
        var allowed = await OgcStylesEndpoints.AuthorizeStyleAsync(context, "Parcels", provider, CancellationToken.None);
        var unmatched = await OgcStylesEndpoints.AuthorizeStyleAsync(context, "PARCELS", provider, CancellationToken.None);

        denied.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        allowed.Should().BeNull();
        unmatched.Should().BeNull();
    }

    private static DefaultHttpContext CreateViewerContext()
    {
        var services = new ServiceCollection()
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();
        return new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, "viewer")],
                "test"))
        };
    }
}
