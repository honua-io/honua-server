// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Api.Sdk;
using Honua.Api.Sdk.Clients;
using Honua.Api.Sdk.Extensions;
using Honua.Core.Transport.Clients;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Api.Sdk.Tests;

public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddHonuaApiClient_RegistersFeatureClientBindings()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddHonuaApiClient(options =>
        {
            options.BaseAddress = "https://api.example.com";
        });

        using var serviceProvider = services.BuildServiceProvider();
        var apiClient = serviceProvider.GetRequiredService<HonuaApiClient>();
        var featureClient = serviceProvider.GetRequiredService<IFeatureServiceClient<ServerContext>>();

        apiClient.Should().NotBeNull();
        featureClient.Should().BeSameAs(apiClient);
    }

    [Fact]
    public void CreateHttpClientHandler_UsesPlatformCertificateValidationByDefault()
    {
        using var handler = ServiceCollectionExtensions.CreateHttpClientHandler(new HonuaApiClientOptions
        {
            MaxConnectionsPerServer = 17
        });

        handler.MaxConnectionsPerServer.Should().Be(17);
        handler.ServerCertificateCustomValidationCallback.Should().BeNull();
    }
}
