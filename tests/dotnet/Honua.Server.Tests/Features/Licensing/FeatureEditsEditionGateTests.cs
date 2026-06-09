// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Licensing;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Licensing;

/// <summary>
/// Unit tests for the multi-user feature-editing Pro-tier gate (#1548). Every write
/// protocol — FeatureServer applyEdits/add/update/delete, OGC API Features mutations,
/// WFS-T, OData CRUD, and gRPC edits — enforces editing behind the shared
/// <see cref="LicenseGate"/> using <c>FeatureCatalog.FeatureEditsKey</c>. This mirrors
/// <c>SpatialAnalyticsEditionGateTests</c>: instead of booting Postgres, the gate
/// decision is exercised directly with a stub <see cref="ILicenseStatusProvider"/>,
/// proving Community licenses are blocked with HTTP 402 while Pro / Enterprise pass.
/// </summary>
[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureEditsEditionGateTests
{
    [UnitTest]
    [Protocol(TestProtocols.FeatureServer)]
    public void RequireEntitlement_CommunityEdition_ReturnsPaymentRequired()
    {
        var context = BuildHttpContext(HonuaEdition.Community);

        var result = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.FeatureEditsKey, "Feature editing");

        result.Should().NotBeNull();
        AssertPaymentRequired(result!);
    }

    [UnitTest]
    [Protocol(TestProtocols.FeatureServer)]
    public void RequireEntitlement_ProEdition_ReturnsNull()
    {
        var context = BuildHttpContext(HonuaEdition.Pro);

        var result = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.FeatureEditsKey, "Feature editing");

        result.Should().BeNull();
    }

    [UnitTest]
    [Protocol(TestProtocols.FeatureServer)]
    public void RequireEntitlement_EnterpriseEdition_ReturnsNull()
    {
        var context = BuildHttpContext(HonuaEdition.Enterprise);

        var result = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.FeatureEditsKey, "Feature editing");

        result.Should().BeNull();
    }

    [UnitTest]
    [Protocol(TestProtocols.FeatureServer)]
    public void IsEntitlementActive_CommunityEdition_IsFalse()
    {
        // The gRPC ApplyEdits path gates via IsEntitlementActive + a FailedPrecondition
        // RpcException rather than an IResult, so cover that decision shape too.
        var context = BuildHttpContext(HonuaEdition.Community);

        LicenseGate.IsEntitlementActive(context.RequestServices, FeatureCatalog.FeatureEditsKey)
            .Should().BeFalse();
    }

    [UnitTest]
    [Protocol(TestProtocols.FeatureServer)]
    public void IsEntitlementActive_ProEdition_IsTrue()
    {
        var context = BuildHttpContext(HonuaEdition.Pro);

        LicenseGate.IsEntitlementActive(context.RequestServices, FeatureCatalog.FeatureEditsKey)
            .Should().BeTrue();
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

    private static void AssertPaymentRequired(IResult result)
    {
        if (result is IStatusCodeHttpResult statusCodeResult)
        {
            statusCodeResult.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
            return;
        }

        var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
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
