// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Unit tests for the queryDateBins Pro-tier edition gate added for ticket #379.
/// Mirrors <c>SpatialAnalyticsEditionGateTests</c>: instead of going through the
/// full HTTP pipeline (which would require booting Postgres), the helper is
/// exercised directly with a stub <see cref="ILicenseStatusProvider"/> so we
/// can verify the gate decision in isolation. Confirms the contract documented
/// at docs/gis/temporal-animation-api.md (`temporal.histogram` is Pro and
/// Community requests are answered with HTTP 402).
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureServerQueryDateBinsEditionGateTests
{
    [UnitTest]
    [Operation(Operations.QueryDateBins)]
    public async Task RequireProEditionForDateBins_CommunityEdition_ReturnsPaymentRequired()
    {
        var context = BuildHttpContext(HonuaEdition.Community);

        var result = FeatureServerEndpoints.RequireProEditionForDateBins(context);

        result.Should().NotBeNull();
        await AssertPaymentRequiredAsync(result!);
    }

    [UnitTest]
    [Operation(Operations.QueryDateBins)]
    public void RequireProEditionForDateBins_ProEdition_ReturnsNull()
    {
        var context = BuildHttpContext(HonuaEdition.Pro);

        var result = FeatureServerEndpoints.RequireProEditionForDateBins(context);

        result.Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.QueryDateBins)]
    public void RequireProEditionForDateBins_EnterpriseEdition_ReturnsNull()
    {
        var context = BuildHttpContext(HonuaEdition.Enterprise);

        var result = FeatureServerEndpoints.RequireProEditionForDateBins(context);

        result.Should().BeNull();
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
        if (result is IStatusCodeHttpResult statusCodeResult)
        {
            statusCodeResult.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
            return;
        }

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
