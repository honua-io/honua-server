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

public sealed class GeoServerServiceUrlValidationTests
{
    [UnitTest]
    public async Task ValidateAsync_HttpScheme_ReturnsFailure_WhenUnsafeLocalUrlsAreDisabled()
    {
        var result = await GeoServerServiceUrlValidation.ValidateAsync(
            "http://example.com/geoserver/rest",
            allowUnsafeLocalUrls: false,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("HTTPS");
    }

    [UnitTest]
    public async Task ValidateAsync_EmbeddedCredentials_ReturnsFailure_WhenUnsafeLocalUrlsAreEnabled()
    {
        var result = await GeoServerServiceUrlValidation.ValidateAsync(
            "http://user:pass@localhost:8080/geoserver/rest",
            allowUnsafeLocalUrls: true,
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("embedded credentials");
    }

    [UnitTest]
    public async Task ValidateAsync_LocalHttpUrl_ReturnsSuccess_WhenUnsafeLocalUrlsAreEnabled()
    {
        var result = await GeoServerServiceUrlValidation.ValidateAsync(
            "http://localhost:8080/geoserver/rest",
            allowUnsafeLocalUrls: true,
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [UnitTest]
    public async Task ValidateAsync_LocalhostHostName_ReturnsFailure_WhenUnsafeLocalUrlsAreDisabled()
    {
        var result = await GeoServerServiceUrlValidation.ValidateAsync(
            "https://localhost/geoserver/rest",
            allowUnsafeLocalUrls: false,
            (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not allowed");
    }

    [UnitTest]
    public async Task ValidateAsync_DnsFailure_ReturnsFailure_WhenUnsafeLocalUrlsAreDisabled()
    {
        var result = await GeoServerServiceUrlValidation.ValidateAsync(
            "https://example.com/geoserver/rest",
            allowUnsafeLocalUrls: false,
            static (_, _) => throw new SocketException());

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not allowed");
    }

    [UnitTest]
    public async Task ValidateAsync_PublicDnsResolution_ReturnsSuccess_WhenUnsafeLocalUrlsAreDisabled()
    {
        var result = await GeoServerServiceUrlValidation.ValidateAsync(
            "https://example.com/geoserver/rest",
            allowUnsafeLocalUrls: false,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        result.IsValid.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
    }

    [UnitTest]
    public async Task ValidateAsync_CloudMetadataLiteral_ReturnsFailure_WhenUnsafeLocalUrlsAreDisabled()
    {
        var result = await GeoServerServiceUrlValidation.ValidateAsync(
            "https://169.254.169.254/geoserver/rest",
            allowUnsafeLocalUrls: false,
            static (_, _) => throw new InvalidOperationException("Literal IPs should not use DNS resolution."));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not allowed");
    }

    [UnitTest]
    public async Task ValidateAsync_Ipv6MulticastResolution_ReturnsFailure_WhenUnsafeLocalUrlsAreDisabled()
    {
        var result = await GeoServerServiceUrlValidation.ValidateAsync(
            "https://example.com/geoserver/rest",
            allowUnsafeLocalUrls: false,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("ff02::1") }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not allowed");
    }
}
