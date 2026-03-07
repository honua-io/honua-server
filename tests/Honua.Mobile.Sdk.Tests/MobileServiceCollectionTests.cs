// Copyright (c) 2026 Honua Project Contributors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.

using FluentAssertions;
using Honua.Core.Transport.Clients;
using Honua.Mobile.Sdk;
using Honua.Mobile.Sdk.Clients;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Honua.Mobile.Sdk.Tests;

public sealed class MobileServiceCollectionTests
{
    [Fact]
    public void AddHonuaMobile_DefaultRegistrationUsesRealFeatureClient()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHonuaMobile(
            options =>
            {
                options.ServerAddress = "https://mobile.example.com";
                options.OfflineDatabase = "mobile-tests.db";
            },
            db => db.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        using var serviceProvider = services.BuildServiceProvider();
        using var scope = serviceProvider.CreateScope();

        var mobileClient = scope.ServiceProvider.GetRequiredService<IFeatureServiceClient<MobileContext>>();
        var coreClient = scope.ServiceProvider.GetRequiredService<IFeatureServiceClient<object>>();

        mobileClient.Should().BeOfType<MobileFeatureServiceClient>();
        coreClient.Should().BeOfType<GrpcFeatureServiceClient<object>>();
        mobileClient.Should().NotBeOfType<MockMobileFeatureServiceClient>();
    }
}
