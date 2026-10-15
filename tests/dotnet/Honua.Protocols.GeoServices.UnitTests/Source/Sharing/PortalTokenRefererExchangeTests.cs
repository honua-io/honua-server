// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Protocols.GeoServices.Sharing;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Sharing;

/// <summary>
/// A federated token exchange that supplies <c>referer</c> but omits <c>client</c>
/// binds the new token to that referer instead of inheriting the presented binding.
/// </summary>
public sealed class PortalTokenRefererExchangeTests
{
    [UnitTest]
    public async Task GenerateToken_RefererWithoutClient_UsesTheSuppliedReferer()
    {
        var captured = await ExchangeAsync(new Dictionary<string, StringValues>
        {
            ["token"] = "presented-token",
            ["serverUrl"] = "https://gis.example/arcgis",
            ["referer"] = "  https://app.example/new  ",
            ["f"] = "json",
        });

        captured.ClientType.Should().Be(PortalTokenClientType.Referer);
        captured.BindingValue.Should().Be("https://app.example/new");
    }

    [UnitTest]
    public async Task GenerateToken_ExchangeWithoutClientOrReferer_InheritsPresentedBinding()
    {
        var captured = await ExchangeAsync(new Dictionary<string, StringValues>
        {
            ["token"] = "presented-token",
            ["serverUrl"] = "https://gis.example/arcgis",
            ["f"] = "json",
        });

        captured.ClientType.Should().BeNull();
        captured.BindingValue.Should().BeNull();
    }

    private static async Task<PortalTokenExchangeRequest> ExchangeAsync(Dictionary<string, StringValues> form)
    {
        PortalTokenExchangeRequest? captured = null;
        var issuer = Substitute.For<IPortalTokenIssuer>();
        issuer.ExchangeAsync(Arg.Any<PortalTokenExchangeRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<PortalTokenExchangeRequest>();
                return new PortalTokenExchange(
                    new PortalTokenIssuance("server-token", DateTimeOffset.UnixEpoch.AddHours(1)),
                    "user",
                    null,
                    captured.ClientType ?? PortalTokenClientType.Referer);
            });

        var license = Substitute.For<ILicenseEntitlementService>();
        license.CheckEntitlement(Arg.Any<string>()).Returns(call => new LicenseEntitlementDecision(
            call.Arg<string>(),
            true,
            HonuaEdition.Community,
            LicenseValidationState.Valid,
            null,
            string.Empty));
        var services = new ServiceCollection();
        services.AddSingleton(license);
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() },
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(form);
        context.Request.Headers.Referer = "https://app.example/old";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.10");

        var result = await SharingRestEndpoints.HandleGenerateTokenAsync(
            context,
            issuer,
            Substitute.For<IPortalCredentialVerifier>(),
            Options.Create(new PortalTokenAuthenticationOptions { Enabled = true, RequireHttps = false }),
            Substitute.For<ITenantContext>(),
            NullLogger<SharingRestLog>.Instance);

        await result.ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        captured.Should().NotBeNull();
        return captured!;
    }
}
