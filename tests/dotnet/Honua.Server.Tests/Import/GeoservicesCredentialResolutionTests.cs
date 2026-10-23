// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Configuration;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Security;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Import;
using Honua.Migration;
using Honua.Import.FileImport;
using Honua.Import.RasterImport;
using Microsoft.Extensions.Configuration;
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
    public void ValidateQueuedCredentialRequest_WithPermittedTokenSecretReference_ReturnsSuccess()
    {
        using var services = CreateServices(allowedEnvironmentVariable: "HONUA_FIXTURE_ARCGIS_TOKEN");

        var error = GeoservicesCredentialResolution.ValidateQueuedCredentialRequest(
            new GeoservicesCredentialDescriptor
            {
                Mode = GeoservicesAuthenticationModes.Token,
                AccessTokenSecretReference = "env:HONUA_FIXTURE_ARCGIS_TOKEN"
            },
            services);

        error.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("HONUA_FIXTURE_OTHER_TOKEN")]
    public void ValidateQueuedCredentialRequest_WithSecretReferenceOutsideThePolicy_ReturnsError(string? allowedEnvironmentVariable)
    {
        using var services = CreateServices(allowedEnvironmentVariable);

        var error = GeoservicesCredentialResolution.ValidateQueuedCredentialRequest(
            new GeoservicesCredentialDescriptor
            {
                Mode = GeoservicesAuthenticationModes.Token,
                AccessTokenSecretReference = "env:HONUA_FIXTURE_ARCGIS_TOKEN"
            },
            services);

        error.Should().StartWith("AccessTokenSecretReference");
        error.Should().NotContain("HONUA_FIXTURE_ARCGIS_TOKEN");
    }

    [Fact]
    public void ValidateQueuedCredentialRequest_WithoutRequestSecretReferenceServices_ReturnsError()
    {
        using var services = new ServiceCollection().BuildServiceProvider();

        var error = GeoservicesCredentialResolution.ValidateQueuedCredentialRequest(
            new GeoservicesCredentialDescriptor
            {
                Mode = GeoservicesAuthenticationModes.Basic,
                Username = "fixture-user",
                PasswordSecretReference = "env:HONUA_FIXTURE_ARCGIS_PASSWORD"
            },
            services);

        error.Should().StartWith("PasswordSecretReference");
    }

    [Fact]
    public async Task ResolveSecretReferencesAsync_WithSecretReferenceOutsideThePolicy_DoesNotReadTheVariable()
    {
        const string variable = "HONUA_FIXTURE_UNLISTED_TOKEN";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "unlisted-value");
        try
        {
            using var services = CreateServices(allowedEnvironmentVariable: "HONUA_FIXTURE_ARCGIS_TOKEN");
            var request = new GeoservicesDiscoveryRequest
            {
                ServiceUrl = "https://services.example.com/arcgis/rest/services/Parcels/FeatureServer",
                Credentials = new GeoservicesCredentialDescriptor
                {
                    Mode = GeoservicesAuthenticationModes.Token,
                    AccessTokenSecretReference = $"env:{variable}"
                }
            };

            var act = () => GeoservicesCredentialResolution.ResolveSecretReferencesAsync(request, services, CancellationToken.None);

            var thrown = await act.Should().ThrowAsync<SecretNotFoundException>();
            thrown.Which.Message.Should().NotContain(variable).And.NotContain("unlisted-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    private static ServiceProvider CreateServices(string? allowedEnvironmentVariable)
    {
        var settings = new Dictionary<string, string?>();
        if (allowedEnvironmentVariable is not null)
        {
            settings["Security:RequestSecretReferences:AllowedEnvironmentVariables:0"] = allowedEnvironmentVariable;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConnectionSecretResolver>(new EnvironmentOnlySecretResolver())
            .AddRequestSecretReferenceResolution(configuration)
            .BuildServiceProvider();
    }

    private sealed class EnvironmentOnlySecretResolver : IConnectionSecretResolver
    {
        public string ProviderName => "env";

        public Task<string?> ResolveSecretAsync(string secretKey, CancellationToken cancellationToken = default)
            => Task.FromResult(Environment.GetEnvironmentVariable(secretKey["env:".Length..]));

        public bool CanResolve(string secretKey) => secretKey.StartsWith("env:", StringComparison.Ordinal);

        public Task<string> ResolveConnectionStringAsync(string connectionStringTemplate, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Request-supplied references must not use template resolution.");
    }
}
