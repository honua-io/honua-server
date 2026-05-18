// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Import;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Import;

public sealed class GeoservicesCredentialResolutionTests
{
    [Theory]
    [InlineData(GeoservicesAuthenticationModes.Token, "Token and OAuth GeoServices discovery requires accessToken or accessTokenSecretReference.")]
    [InlineData(GeoservicesAuthenticationModes.OAuth, "Token and OAuth GeoServices discovery requires accessToken or accessTokenSecretReference.")]
    [InlineData(GeoservicesAuthenticationModes.Basic, "Basic GeoServices credentials require username.")]
    public void ValidateDiscoveryCredentialRequest_WithModeOnlyCredentials_ReturnsValidationError(
        string mode,
        string expectedError)
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var error = GeoservicesCredentialResolution.ValidateDiscoveryCredentialRequest(
            new GeoservicesCredentialDescriptor { Mode = mode },
            services);

        error.Should().Be(expectedError);
    }

    [Theory]
    [InlineData(GeoservicesAuthenticationModes.Token, "Token and OAuth GeoServices imports require accessTokenSecretReference.")]
    [InlineData(GeoservicesAuthenticationModes.OAuth, "Token and OAuth GeoServices imports require accessTokenSecretReference.")]
    [InlineData(GeoservicesAuthenticationModes.Basic, "Basic GeoServices credentials require username.")]
    public void ValidateQueuedCredentialRequest_WithModeOnlyCredentials_ReturnsValidationError(
        string mode,
        string expectedError)
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var error = GeoservicesCredentialResolution.ValidateQueuedCredentialRequest(
            new GeoservicesCredentialDescriptor { Mode = mode },
            services);

        error.Should().Be(expectedError);
    }

    [Fact]
    public void ValidateDiscoveryCredentialRequest_WithAnonymousModeOnly_ReturnsSuccess()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var error = GeoservicesCredentialResolution.ValidateDiscoveryCredentialRequest(
            new GeoservicesCredentialDescriptor { Mode = GeoservicesAuthenticationModes.Anonymous },
            services);

        error.Should().BeNull();
    }

    [Fact]
    public void ValidateDiscoveryCredentialRequest_WithTokenMaterial_ReturnsSuccess()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var error = GeoservicesCredentialResolution.ValidateDiscoveryCredentialRequest(
            new GeoservicesCredentialDescriptor
            {
                Mode = GeoservicesAuthenticationModes.Token,
                AccessToken = "fixture-token"
            },
            services);

        error.Should().BeNull();
    }

    [Fact]
    public void ValidateQueuedCredentialRequest_WithTokenSecretReference_ReturnsSuccess()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var error = GeoservicesCredentialResolution.ValidateQueuedCredentialRequest(
            new GeoservicesCredentialDescriptor
            {
                Mode = GeoservicesAuthenticationModes.Token,
                AccessTokenSecretReference = "env:HONUA_FIXTURE_ARCGIS_TOKEN"
            },
            services);

        error.Should().BeNull();
    }
}
