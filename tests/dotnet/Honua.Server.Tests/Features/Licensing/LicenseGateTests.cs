// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Helpers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Licensing;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class LicenseGateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Tier", "Fast")]
    public void ExpiredPaidTier_EditionFallback_CannotReactivateOperations(bool statusFacade)
    {
        const string key = "serve.i3s-scene";
        var services = new ServiceCollection();
        if (statusFacade)
        {
            var status = Substitute.For<ILicenseStatusProvider>();
            status.GetCurrentStatus().Returns(new LicenseStatus(HonuaEdition.Enterprise, false,
                DateTimeOffset.UtcNow.AddDays(-1), "Synthetic operator", LicenseValidationState.Expired));
            services.AddSingleton(status);
        }
        else
        {
            var entitlements = Substitute.For<ILicenseEntitlementService>();
            entitlements.CheckEntitlement(key).Returns(new LicenseEntitlementDecision(key, false,
                HonuaEdition.Enterprise, LicenseValidationState.Expired, HonuaEdition.Enterprise, "license expired"));
            services.AddSingleton(entitlements);
        }
        using var provider = services.BuildServiceProvider();
        Assert.False(LicenseGate.CheckEntitlement(provider, key).IsActive);
    }

    [UnitTest]
    public async Task RequireEntitlement_MissingPaidEntitlement_ReturnsPaymentRequired()
    {
        var context = BuildContext(HonuaEdition.Community);

        var result = LicenseGate.RequireEntitlement(
            context,
            "analytics.clustering",
            "Spatial clustering",
            NullLogger.Instance);

        result.Should().NotBeNull();
        (await GetStatusCodeAsync(result!)).Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [UnitTest]
    public void RequireEntitlement_ActiveEntitlement_ReturnsNull()
    {
        var context = BuildContext(HonuaEdition.Pro);

        var result = LicenseGate.RequireEntitlement(
            context,
            "analytics.clustering",
            "Spatial clustering",
            NullLogger.Instance);

        result.Should().BeNull();
    }

    [UnitTest]
    public async Task RequireEntitlement_EntitlementStrictKeyAtEdition_HonorsProviderDenial()
    {
        // A FeatureCatalog key can be entitlement-strict: holding the required
        // edition without the explicit entitlement must stay 402. The routed-
        // experimental edition fallback must not widen to these keys.
        var context = BuildContext(HonuaEdition.Pro, entitlements: []);

        var result = LicenseGate.RequireEntitlement(
            context,
            "ai.spec-apply",
            "Spec Apply",
            NullLogger.Instance);

        result.Should().NotBeNull();
        (await GetStatusCodeAsync(result!)).Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [UnitTest]
    public async Task RequireEntitlement_RoutedExperimentalCapabilityBelowEdition_ReturnsPaymentRequired()
    {
        var context = BuildContext(HonuaEdition.Community);

        var result = LicenseGate.RequireEntitlement(
            context,
            "serve.i3s-scene",
            "I3S Scene Serving",
            NullLogger.Instance);

        result.Should().NotBeNull();
        (await GetStatusCodeAsync(result!)).Should().Be(StatusCodes.Status402PaymentRequired);
    }

    [UnitTest]
    public void RequireEntitlement_RoutedExperimentalCapabilityAtEdition_ReturnsNull()
    {
        var context = BuildContext(HonuaEdition.Enterprise);

        var result = LicenseGate.RequireEntitlement(
            context,
            "serve.i3s-scene",
            "I3S Scene Serving",
            NullLogger.Instance);

        result.Should().BeNull();
    }

    [UnitTest]
    public void CreateFailedPreconditionRpcException_MissingPaidEntitlement_ReturnsFailedPrecondition()
    {
        var context = BuildContext(HonuaEdition.Community);

        var exception = LicenseGate.CreateFailedPreconditionRpcException(
            context.RequestServices,
            "analytics.clustering");

        exception.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        exception.Status.Detail.Should().Contain("analytics.clustering");
    }

    private static DefaultHttpContext BuildContext(HonuaEdition edition, string[]? entitlements = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILicenseEntitlementService>(
            new TestLicenseEntitlementService(edition, entitlements: entitlements));
        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
    }

    private static async Task<int?> GetStatusCodeAsync(IResult result)
    {
        if (result is IStatusCodeHttpResult statusCodeResult)
        {
            return statusCodeResult.StatusCode;
        }

        var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await result.ExecuteAsync(context);
        return context.Response.StatusCode;
    }
}
