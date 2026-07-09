// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Honua.Import;
using Honua.Migration;
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

    // PA-153: host allowlist tests (SSRF defence)

    [UnitTest]
    public async Task ValidateAsync_AllowlistConfigured_MatchingSuffix_ReturnsSuccess()
    {
        var allowedSuffixes = new[] { ".arcgisonline.com", "arcgis.example.com" };

        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://services.arcgisonline.com/arcgis/rest/services/Test/FeatureServer",
            allowedSuffixes,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        result.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public async Task ValidateAsync_AllowlistConfigured_RequiresHostLabelBoundary()
    {
        var allowedSuffixes = new[] { "allowed.example.com" };

        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://evilallowed.example.com/arcgis/rest/services/Test/FeatureServer",
            allowedSuffixes,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be(GeoservicesServiceUrlValidation.DisallowedHostMessage);
    }

    [UnitTest]
    public async Task ValidateAsync_AllowlistConfigured_NonMatchingSuffix_ReturnsFailure()
    {
        var allowedSuffixes = new[] { ".arcgisonline.com" };

        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://example.com/arcgis/rest/services/Test/FeatureServer",
            allowedSuffixes,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be(GeoservicesServiceUrlValidation.DisallowedHostMessage);
    }

    [UnitTest]
    public async Task ValidateAsync_AllowlistNull_AnyPublicHostPermitted()
    {
        // When no allowlist is configured, the original permissive behaviour is preserved.
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://example.com/arcgis/rest/services/Test/FeatureServer",
            allowedHostSuffixes: null,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        result.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public async Task ValidateAsync_AllowlistEmpty_RejectsAllHosts()
    {
        // An explicitly empty allowlist means "no hosts are allowed".
        var result = await GeoservicesServiceUrlValidation.ValidateAsync(
            "https://example.com/arcgis/rest/services/Test/FeatureServer",
            allowedHostSuffixes: [],
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Be(GeoservicesServiceUrlValidation.DisallowedHostMessage);
    }

    // PA-154: ValidateAndResolveAsync pinned-address helper

    [UnitTest]
    public async Task ValidateAndResolveAsync_PublicAddress_ReturnsFalseAndAddresses()
    {
        var publicIp = IPAddress.Parse("93.184.216.34");
        var uri = new Uri("https://example.com/arcgis/rest/services/Test/FeatureServer");

        var (isDisallowed, resolved) = await NetworkAddressValidator.ValidateAndResolveAsync(
            uri,
            (_, _) => Task.FromResult(new[] { publicIp }),
            CancellationToken.None);

        isDisallowed.Should().BeFalse();
        resolved.Should().ContainSingle().Which.Should().Be(publicIp);
    }

    [UnitTest]
    public async Task ValidateAndResolveAsync_PrivateAddress_ReturnsTrueAndEmptySet()
    {
        var privateIp = IPAddress.Parse("10.10.10.10");
        var uri = new Uri("https://internal.corp.example.com/arcgis/rest/services/Test/FeatureServer");

        var (isDisallowed, resolved) = await NetworkAddressValidator.ValidateAndResolveAsync(
            uri,
            (_, _) => Task.FromResult(new[] { privateIp }),
            CancellationToken.None);

        isDisallowed.Should().BeTrue();
        resolved.Should().BeEmpty();
    }

    [UnitTest]
    public async Task ValidateAndResolveAsync_MetadataLiteralAddress_ReturnsTrueAndEmpty()
    {
        // 169.254.169.254 is the cloud metadata endpoint for AWS/Azure/GCP.
        var uri = new Uri("https://169.254.169.254/latest/meta-data/");

        var (isDisallowed, resolved) = await NetworkAddressValidator.ValidateAndResolveAsync(
            uri,
            static (_, _) => throw new InvalidOperationException("Literal IPs should not use DNS resolution."),
            CancellationToken.None);

        isDisallowed.Should().BeTrue();
        resolved.Should().BeEmpty();
    }
}
