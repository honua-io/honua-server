// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Honua.Import;
using Honua.Server.Features.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

public sealed class GeoservicesServiceUrlValidationTests
{
    [UnitTest]
    public async Task ValidateAsync_HttpScheme_ReturnsFailure()
    {
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "http://example.com/arcgis/rest/services/Test/FeatureServer",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("HTTPS");
    }

    [UnitTest]
    public async Task ValidateAsync_EmbeddedCredentials_ReturnsFailure()
    {
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://user:pass@example.com/arcgis/rest/services/Test/FeatureServer",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("embedded credentials");
    }

    [UnitTest]
    public async Task ValidateAsync_LocalhostHostName_ReturnsFailure()
    {
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://localhost/arcgis/rest/services/Test/FeatureServer",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not allowed");
    }

    [UnitTest]
    public async Task ValidateAsync_PrivateDnsResolution_ReturnsFailure()
    {
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://example.com/arcgis/rest/services/Test/FeatureServer",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.10.10.10") }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not allowed");
    }

    [UnitTest]
    public async Task ValidateAsync_DnsFailure_ReturnsFailure()
    {
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://example.com/arcgis/rest/services/Test/FeatureServer",
            static (_, _) => throw new SocketException());

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not allowed");
    }

    [UnitTest]
    public async Task ValidateAsync_FeatureServerLayerUrl_ReturnsFailureWithoutResolution()
    {
        var resolverCalled = false;
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://example.com/arcgis/rest/services/Test/FeatureServer/0",
            (_, _) =>
            {
                resolverCalled = true;
                return Task.FromResult(Array.Empty<IPAddress>());
            });

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be(GeoservicesServiceUrlValidation.InvalidServiceRootMessage);
        resolverCalled.Should().BeFalse();
    }

    [UnitTest]
    public async Task ValidateAsync_MapServerLayerUrl_ReturnsFailureWithoutResolution()
    {
        var resolverCalled = false;
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://example.com/arcgis/rest/services/Test/MapServer/0",
            (_, _) =>
            {
                resolverCalled = true;
                return Task.FromResult(Array.Empty<IPAddress>());
            });

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be(GeoservicesServiceUrlValidation.InvalidServiceRootMessage);
        resolverCalled.Should().BeFalse();
    }

    [UnitTest]
    public async Task ValidateAsync_PublicDnsResolution_ReturnsSuccess()
    {
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://example.com/arcgis/rest/services/Test/FeatureServer",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [UnitTest]
    public async Task ValidateAsync_CloudMetadataLiteral_ReturnsFailure()
    {
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://169.254.169.254/arcgis/rest/services/Test/FeatureServer",
            static (_, _) => throw new InvalidOperationException("Literal IPs should not use DNS resolution."));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not allowed");
    }

    [UnitTest]
    public async Task ValidateAsync_Ipv6MulticastResolution_ReturnsFailure()
    {
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://example.com/arcgis/rest/services/Test/FeatureServer",
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("ff02::1") }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not allowed");
    }
}
