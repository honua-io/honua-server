// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.FileStorage;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Geoprocessing;

public sealed class GeoprocessingOutputStagingProviderTests
{
    [UnitTheory]
    [InlineData("AwsS3")]
    [InlineData("AzureBlob")]
    public void ObjectStorageProvider_WithSharedFileStorage_RegistersTheObjectStorageStore(string fileStorageProvider)
    {
        using var services = BuildServices(ObjectStorage(fileStorageProvider));

        services.GetRequiredService<IOptions<GeoprocessingOutputStagingOptions>>().Value.Provider.Should().Be("object-storage");
        services.GetRequiredService<IGeoprocessingOutputObjectStore>()
            .Should().BeOfType<ObjectStorageGeoprocessingOutputObjectStore>();
    }

    [UnitTest]
    public void ObjectStorageProvider_NeedsNoLocalRootOrVolumeAttestation()
    {
        var settings = ObjectStorage("AwsS3");
        settings.Should().NotContainKey("Geoprocessing:OutputStaging:LocalRootPath");

        using var services = BuildServices(settings);

        var resolve = () => services.GetRequiredService<IOptions<GeoprocessingOutputStagingOptions>>().Value;
        resolve.Should().NotThrow();
    }

    [UnitTheory]
    [InlineData("Local")]
    [InlineData(null)]
    public void ObjectStorageProvider_WithLocalFileStorage_IsRefused(string? fileStorageProvider)
    {
        using var services = BuildServices(ObjectStorage(fileStorageProvider));

        var resolve = () => services.GetRequiredService<IOptions<GeoprocessingOutputStagingOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>().WithMessage("*requires FileStorage:Provider to be AwsS3 or AzureBlob*");
    }

    [UnitTheory]
    [InlineData("s3")]
    [InlineData("azure")]
    [InlineData("remote")]
    public void UnknownProvider_IsRefused(string provider)
    {
        var settings = ObjectStorage("AwsS3");
        settings["Geoprocessing:OutputStaging:Provider"] = provider;
        using var services = BuildServices(settings);

        var resolve = () => services.GetRequiredService<IOptions<GeoprocessingOutputStagingOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>().WithMessage("*must be 'local' or 'object-storage'*");
    }

    [UnitTheory]
    [InlineData("honua", "honua/gp/outputs", true)]
    [InlineData("honua", "gp/outputs", false)]
    [InlineData("", "gp/outputs", true)]
    public void ObjectStorageProvider_KeyPrefixMustSitUnderTheFileStorageKeyPrefix(
        string fileStorageKeyPrefix,
        string outputKeyPrefix,
        bool accepted)
    {
        var settings = ObjectStorage("AwsS3");
        settings["FileStorage:AwsS3:KeyPrefix"] = fileStorageKeyPrefix;
        settings["Geoprocessing:OutputStaging:KeyPrefix"] = outputKeyPrefix;
        using var services = BuildServices(settings);

        var resolve = () => services.GetRequiredService<IOptions<GeoprocessingOutputStagingOptions>>().Value;

        if (accepted)
        {
            resolve.Should().NotThrow();
        }
        else
        {
            resolve.Should().Throw<OptionsValidationException>().WithMessage("*must start with the file storage key prefix*");
        }
    }

    [UnitTest]
    public void LocalProvider_WithoutARoot_IsStillRefused()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["Geoprocessing:OutputStaging:Enabled"] = "true",
            ["Geoprocessing:OutputStaging:Provider"] = "local",
        });

        var resolve = () => services.GetRequiredService<IOptions<GeoprocessingOutputStagingOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>();
    }

    [UnitTest]
    public void Disabled_RegistersNoStore()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        services.GetService<IGeoprocessingOutputObjectStore>().Should().BeNull();
    }

    private static Dictionary<string, string?> ObjectStorage(string? fileStorageProvider)
        => new()
        {
            ["Geoprocessing:OutputStaging:Enabled"] = "true",
            ["Geoprocessing:OutputStaging:Provider"] = "object-storage",
            ["FileStorage:Provider"] = fileStorageProvider,
        };

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICloudFileStorage>(new SharedBucketCloudFileStorage(new SharedBucket()));
        services.AddGeoprocessingOutputStaging(configuration);
        return services.BuildServiceProvider();
    }
}
