// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.Infrastructure.Validation;

public sealed class OutboundHttpUrlValidatorTests
{
    [UnitTest]
    public void ValidateConfiguration_WithNonHttpsScheme_ReturnsFailure()
    {
        var result = OutboundHttpUrlValidator.ValidateConfiguration("http://example.com");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("HTTPS");
    }

    [UnitTest]
    public void ValidateConfiguration_WithEmbeddedCredentials_ReturnsFailure()
    {
        var result = OutboundHttpUrlValidator.ValidateConfiguration("https://user:pass@example.com");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("credentials");
    }

    [UnitTest]
    public void ValidateConfiguration_WithLoopbackHostname_ReturnsFailure()
    {
        var result = OutboundHttpUrlValidator.ValidateConfiguration("https://localhost:9090");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("private");
    }

    [UnitTest]
    public void ValidateConfiguration_WithLiteralPrivateIp_ReturnsFailure()
    {
        var result = OutboundHttpUrlValidator.ValidateConfiguration("https://10.0.0.1:9090");

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("private");
    }

    [UnitTest]
    public void ValidateConfiguration_WithHostnameResolvingToPrivateAddress_ReturnsFailure()
    {
        OutboundHttpUrlValidationResult result = OutboundHttpUrlValidator.ValidateConfiguration(
            "https://internal.example",
            host => [IPAddress.Parse("10.0.0.5")]);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("private");
    }

    [UnitTest]
    public void ValidateConfiguration_WithFailingResolver_FailsClosedAsResolutionUnavailable()
    {
        OutboundHttpUrlValidationResult result = OutboundHttpUrlValidator.ValidateConfiguration(
            "https://does-not-resolve.example",
            host => throw new System.Net.Sockets.SocketException());

        result.IsValid.Should().BeFalse("a host that cannot be vetted is still blocked");
        result.FailureReason.Should().Be(OutboundHttpUrlFailureReason.HostResolutionUnavailable);
        result.IsHostResolutionUnavailable.Should().BeTrue();
        result.ErrorMessage.Should().Contain("resolution");
    }

    [UnitTest]
    public void ValidateConfiguration_WithMalformedHostname_ReturnsDisallowedAddress()
    {
        OutboundHttpUrlValidationResult result = OutboundHttpUrlValidator.ValidateConfiguration(
            "https://malformed.example",
            host => throw new ArgumentException("host name too long", nameof(host)));

        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be(
            OutboundHttpUrlFailureReason.DisallowedAddress,
            "a malformed host name is a permanent property of the URL, not a resolver outage");
    }

    [UnitTest]
    public void ValidateConfiguration_WithEmptyResolverResult_ReturnsFailure()
    {
        OutboundHttpUrlValidationResult result = OutboundHttpUrlValidator.ValidateConfiguration(
            "https://no-records.example",
            host => System.Array.Empty<IPAddress>());

        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be(OutboundHttpUrlFailureReason.DisallowedAddress);
        result.ErrorMessage.Should().Contain("unresolvable");
    }

    [UnitTest]
    public async Task ValidateAsync_WithFailingResolver_FailsClosedAsResolutionUnavailable()
    {
        var result = await OutboundHttpUrlValidator.ValidateAsync(
            "https://does-not-resolve.example",
            (host, cancellationToken) => throw new System.Net.Sockets.SocketException());

        result.IsValid.Should().BeFalse("the destination is never contacted when it cannot be vetted");
        result.Uri.Should().BeNull();
        result.FailureReason.Should().Be(OutboundHttpUrlFailureReason.HostResolutionUnavailable);
    }

    [UnitTest]
    public async Task ValidateAsync_WithHostnameResolvingToPrivateAddress_ReturnsDisallowedAddress()
    {
        var result = await OutboundHttpUrlValidator.ValidateAsync(
            "https://internal.example",
            (host, cancellationToken) => Task.FromResult<IPAddress[]>([IPAddress.Parse("10.0.0.5")]));

        result.IsValid.Should().BeFalse();
        result.FailureReason.Should().Be(OutboundHttpUrlFailureReason.DisallowedAddress);
    }

    [UnitTest]
    public async Task ValidateAsync_WithHostnameResolvingToPublicAddress_ReturnsSuccess()
    {
        var result = await OutboundHttpUrlValidator.ValidateAsync(
            "https://public.example",
            (host, cancellationToken) => Task.FromResult<IPAddress[]>([IPAddress.Parse("8.8.8.8")]));

        result.IsValid.Should().BeTrue();
        result.FailureReason.Should().Be(OutboundHttpUrlFailureReason.None);
        result.Uri!.Host.Should().Be("public.example");
    }

    [UnitTest]
    public void ValidateConfiguration_WithHostnameResolvingToPublicAddress_ReturnsSuccess()
    {
        OutboundHttpUrlValidationResult result = OutboundHttpUrlValidator.ValidateConfiguration(
            "https://public.example",
            host => [IPAddress.Parse("8.8.8.8")]);

        result.IsValid.Should().BeTrue();
        result.Uri.Should().NotBeNull();
        result.Uri!.Host.Should().Be("public.example");
    }

    [UnitTest]
    public void ValidateConfiguration_WhenAnyResolvedAddressIsPrivate_ReturnsFailure()
    {
        OutboundHttpUrlValidationResult result = OutboundHttpUrlValidator.ValidateConfiguration(
            "https://mixed.example",
            host => [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("10.0.0.5")]);

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("private");
    }

    [UnitTest]
    public void ValidateConfiguration_WithLoopback_AllowPrivateNetworks_ReturnsSuccess()
    {
        var result = OutboundHttpUrlValidator.ValidateConfiguration(
            "http://localhost:9090",
            allowPrivateNetworks: true);

        result.IsValid.Should().BeTrue("the explicit private-network opt-in permits a loopback on-prem endpoint");
        result.Uri.Should().NotBeNull();
    }

    [UnitTest]
    public void ValidateConfiguration_WithLiteralPrivateIp_AllowPrivateNetworks_ReturnsSuccess()
    {
        var result = OutboundHttpUrlValidator.ValidateConfiguration(
            "http://10.0.0.1:9090",
            allowPrivateNetworks: true);

        result.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public void ValidateConfiguration_WithHttpScheme_WithoutOptIn_StillRejected()
    {
        var result = OutboundHttpUrlValidator.ValidateConfiguration("http://prometheus.internal:9090");

        result.IsValid.Should().BeFalse("http is only allowed under the private-network opt-in");
        result.ErrorMessage.Should().Contain("HTTPS");
    }

    [UnitTest]
    public void ValidateConfiguration_WithEmbeddedCredentials_AllowPrivateNetworks_StillRejected()
    {
        var result = OutboundHttpUrlValidator.ValidateConfiguration(
            "http://user:pass@10.0.0.1:9090",
            allowPrivateNetworks: true);

        result.IsValid.Should().BeFalse("embedded credentials are rejected regardless of the network opt-in");
        result.ErrorMessage.Should().Contain("credentials");
    }

    [UnitTest]
    public async Task ValidateAsync_WithPrivateAddress_AllowPrivateNetworks_ReturnsSuccess()
    {
        var result = await OutboundHttpUrlValidator.ValidateAsync(
            "http://192.168.1.10:9090",
            allowPrivateNetworks: true);

        result.IsValid.Should().BeTrue();
    }
}
