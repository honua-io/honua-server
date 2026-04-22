// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
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
    public void ValidateConfiguration_WithUnresolvableHostname_ReturnsFailure()
    {
        OutboundHttpUrlValidationResult result = OutboundHttpUrlValidator.ValidateConfiguration(
            "https://does-not-resolve.example",
            host => throw new System.Net.Sockets.SocketException());

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("unresolvable");
    }

    [UnitTest]
    public void ValidateConfiguration_WithEmptyResolverResult_ReturnsFailure()
    {
        OutboundHttpUrlValidationResult result = OutboundHttpUrlValidator.ValidateConfiguration(
            "https://no-records.example",
            host => System.Array.Empty<IPAddress>());

        result.IsValid.Should().BeFalse();
        result.ErrorMessage.Should().Contain("unresolvable");
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
}
