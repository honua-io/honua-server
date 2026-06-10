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

namespace Honua.Server.Tests.Features.Licensing;

[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class LicenseGateTests
{
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
    public void CreateFailedPreconditionRpcException_MissingPaidEntitlement_ReturnsFailedPrecondition()
    {
        var context = BuildContext(HonuaEdition.Community);

        var exception = LicenseGate.CreateFailedPreconditionRpcException(
            context.RequestServices,
            "analytics.clustering");

        exception.StatusCode.Should().Be(StatusCode.FailedPrecondition);
        exception.Status.Detail.Should().Contain("analytics.clustering");
    }

    private static DefaultHttpContext BuildContext(HonuaEdition edition)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILicenseEntitlementService>(new TestLicenseEntitlementService(edition));
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
