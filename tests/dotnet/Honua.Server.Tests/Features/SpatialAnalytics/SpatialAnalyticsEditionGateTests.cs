// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Protocols.SpatialAnalytics;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.SpatialAnalytics;

/// <summary>
/// Unit tests for the spatial analytics Pro-tier edition gate. Mirrors the
/// <c>PrintingToolsRequestHandlerTests.ValidateEdition_*</c> pattern: instead of
/// going through the full HTTP pipeline (which would require booting Postgres),
/// the helper is exercised directly with a stub <see cref="ILicenseStatusProvider"/>.
/// This proves that <see cref="SpatialAnalyticsRequestHandlers.RequireProEdition"/>
/// blocks Community licenses with HTTP 402 and lets Pro / Enterprise through.
/// </summary>
[Protocol(TestProtocols.SpatialAnalytics)]
public sealed class SpatialAnalyticsEditionGateTests
{
    [UnitTest]
    [Protocol(TestProtocols.SpatialAnalytics)]
    public async Task RequireProEdition_CommunityEdition_ReturnsPaymentRequired()
    {
        var context = BuildHttpContext(HonuaEdition.Community);

        var result = SpatialAnalyticsRequestHandlers.RequireProEdition(
            context, "queryClusters", NullLogger.Instance);

        result.Should().NotBeNull();
        await AssertPaymentRequiredAsync(result!);
    }

    [UnitTest]
    [Protocol(TestProtocols.SpatialAnalytics)]
    public void RequireProEdition_ProEdition_ReturnsNull()
    {
        var context = BuildHttpContext(HonuaEdition.Pro);

        var result = SpatialAnalyticsRequestHandlers.RequireProEdition(
            context, "spatialJoin", NullLogger.Instance);

        result.Should().BeNull();
    }

    [UnitTest]
    [Protocol(TestProtocols.SpatialAnalytics)]
    public void RequireProEdition_EnterpriseEdition_ReturnsNull()
    {
        var context = BuildHttpContext(HonuaEdition.Enterprise);

        var result = SpatialAnalyticsRequestHandlers.RequireProEdition(
            context, "queryDensity", NullLogger.Instance);

        result.Should().BeNull();
    }

    [UnitTest]
    [Protocol(TestProtocols.SpatialAnalytics)]
    public async Task RequireProEdition_LoggerNull_StillBlocksCommunity()
    {
        // The handler tolerates a null logger (it's resolved best-effort from DI)
        // so missing observability must not break the gate decision.
        var context = BuildHttpContext(HonuaEdition.Community);

        var result = SpatialAnalyticsRequestHandlers.RequireProEdition(
            context, "queryBufferAggregate", logger: null);

        result.Should().NotBeNull();
        await AssertPaymentRequiredAsync(result!);
    }

    private static DefaultHttpContext BuildHttpContext(HonuaEdition edition)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILicenseStatusProvider>(new StubLicenseStatusProvider(edition));

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };
    }

    private static async Task AssertPaymentRequiredAsync(IResult result)
    {
        // StandardErrorHelpers.CreateForbidden routes through the standard error
        // formatter which produces an IStatusCodeHttpResult variant — the typed
        // status code is the contract we care about regardless of the wrapper.
        if (result is IStatusCodeHttpResult statusCodeResult)
        {
            statusCodeResult.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
            return;
        }

        // Fallback: execute the result against a recording HttpContext so we can
        // observe the status code regardless of the underlying wrapper type.
        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        await result.ExecuteAsync(ctx);
        ctx.Response.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
    }

    private sealed class StubLicenseStatusProvider : ILicenseStatusProvider
    {
        private readonly HonuaEdition _edition;

        public StubLicenseStatusProvider(HonuaEdition edition) => _edition = edition;

        public LicenseStatus GetCurrentStatus() =>
            new(_edition, IsValid: true, ExpiresAt: null, LicensedTo: "test");

        public Task<LicenseUploadResult> UploadLicenseAsync(
            Stream licenseStream, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LicenseUploadResult(false, "Stub does not support upload."));
    }
}
