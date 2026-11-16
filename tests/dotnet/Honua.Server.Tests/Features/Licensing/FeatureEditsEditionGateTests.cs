// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

[Protocol(TestProtocols.FeatureServer)]
public sealed class FeatureEditsEditionGateTests
{
    [UnitTest]
    [Protocol(TestProtocols.FeatureServer)]
    public void RequireEntitlement_CommunityEdition_ReturnsPaymentRequired()
    {
        var context = BuildHttpContext(HonuaEdition.Community);

        var result = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.FeatureServerEditsKey, "FeatureServer editing");

        result.Should().NotBeNull();
        AssertPaymentRequired(result!);
    }

    [UnitTest]
    [Protocol(TestProtocols.FeatureServer)]
    public void RequireEntitlement_ProEdition_ReturnsNull()
    {
        var context = BuildHttpContext(HonuaEdition.Pro);

        var result = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.FeatureServerEditsKey, "FeatureServer editing");

        result.Should().BeNull();
    }

    [UnitTest]
    [Protocol(TestProtocols.FeatureServer)]
    public void RequireEntitlement_EnterpriseEdition_ReturnsNull()
    {
        var context = BuildHttpContext(HonuaEdition.Enterprise);

        var result = LicenseGate.RequireEntitlement(
            context, FeatureCatalog.FeatureServerEditsKey, "FeatureServer editing");

        result.Should().BeNull();
    }

    [UnitTest]
    [Protocol(TestProtocols.FeatureServer)]
    public void IsEntitlementActive_CommunityEdition_IsFalse()
    {
        var context = BuildHttpContext(HonuaEdition.Community);

        LicenseGate.IsEntitlementActive(context.RequestServices, FeatureCatalog.FeatureServerEditsKey)
            .Should().BeFalse();
    }

    [UnitTest]
    [Protocol(TestProtocols.FeatureServer)]
    public void IsEntitlementActive_ProEdition_IsTrue()
    {
        var context = BuildHttpContext(HonuaEdition.Pro);

        LicenseGate.IsEntitlementActive(context.RequestServices, FeatureCatalog.FeatureServerEditsKey)
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
