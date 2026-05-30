// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.FileStorage;

[Collection("Unit")]
public sealed class FileStorageServiceExtensionsTests
{
    [UnitTest]
    public void AddCloudFileStorage_WhenLocalStorageSectionMissing_DefaultsLocalStorageOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:Provider"] = "Local"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCloudFileStorage(configuration);

        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CloudStorageOptions>>().Value;
        var storage = provider.GetRequiredService<ICloudFileStorage>();

        options.Provider.Should().Be(CloudStorageProvider.Local);
        options.LocalStorage.Should().NotBeNull();
        options.LocalStorage!.BasePath.Should().Be(Path.Combine(Path.GetTempPath(), "honua-storage"));
        options.LocalStorage.CreateDirectoryIfNotExists.Should().BeTrue();
        storage.Should().BeOfType<LocalFileStorage>();
    }
}
