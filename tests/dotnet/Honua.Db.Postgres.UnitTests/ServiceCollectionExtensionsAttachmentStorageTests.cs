// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Attachments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Db.Postgres.Tests;

[Trait("Tier", "Fast")]
public sealed class ServiceCollectionExtensionsAttachmentStorageTests
{
    [Fact]
    public void AddPostgreSqlServices_FeatureStoreResolvesWithoutCloudFileStorage()
    {
        using var provider = BuildProvider(storage: null);
        using var scope = provider.CreateScope();

        var act = () => scope.ServiceProvider.GetRequiredService<IFeatureReader>();

        act.Should().NotThrow();
        scope.ServiceProvider.GetRequiredService<IFeatureAttachmentCleanup>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddPostgreSqlServices_AttachmentCleanupUsesRegisteredCloudFileStorage()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.DeleteAsync("blob", Arg.Any<CancellationToken>()).Returns(true);
        await using var provider = BuildProvider(storage);
        await using var scope = provider.CreateAsyncScope();

        await scope.ServiceProvider.GetRequiredService<IFeatureAttachmentCleanup>().DeleteBlobsAsync(
            [new DeletedAttachmentBlob(1, 2, "blob")],
            CancellationToken.None);

        await storage.Received(1).DeleteAsync("blob", Arg.Any<CancellationToken>());
    }

    private static ServiceProvider BuildProvider(ICloudFileStorage? storage)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=127.0.0.1;Port=1;Database=honua_test;Username=honua;Timeout=1",
                ["Security:ConnectionEncryption:MasterKey"] =
                    "unit-test-master-key-32-characters"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        if (storage is not null)
        {
            services.AddSingleton(storage);
        }

        services.AddPostgreSqlServices(configuration, TestCoreSchemaMigrations.Manifest);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
