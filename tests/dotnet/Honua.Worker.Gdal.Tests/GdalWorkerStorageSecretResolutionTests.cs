// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

public sealed class GdalWorkerStorageSecretResolutionTests
{
    [Fact]
    public void ResolveWorkerStoreReference_UsesVersionedContractProjection()
    {
        var options = new RasterOutputPublicationOptions { StoreReference = "gp-results" };

        GdalWorkerServiceCollectionExtensions.ResolveWorkerStoreReference(
            options,
            name => name == RasterOutputWorkerContract.StoreReferenceEnvironmentVariable
                ? "tenant-results"
                : null);

        options.StoreReference.Should().Be("tenant-results");
    }

    [Fact]
    public void ResolveWorkerSecretReferences_ResolvesOnlyInMemoryEnvironmentReferences()
    {
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options
            {
                BucketName = "gp-results",
                AccessKeyId = "env:HONUA_TEST_ACCESS_KEY",
                SecretAccessKey = "env:HONUA_TEST_SECRET_KEY"
            },
            AzureBlob = new AzureBlobOptions
            {
                ConnectionString = "env:HONUA_TEST_AZURE_CONNECTION",
                ContainerName = "gp-results"
            }
        };
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HONUA_TEST_ACCESS_KEY"] = "resolved-access",
            ["HONUA_TEST_SECRET_KEY"] = "resolved-secret",
            ["HONUA_TEST_AZURE_CONNECTION"] = "resolved-connection"
        };

        GdalWorkerServiceCollectionExtensions.ResolveWorkerSecretReferences(
            options,
            name => values.GetValueOrDefault(name));

        options.AwsS3.AccessKeyId.Should().Be("resolved-access");
        options.AwsS3.SecretAccessKey.Should().Be("resolved-secret");
        options.AzureBlob.ConnectionString.Should().Be("resolved-connection");
    }

    [Fact]
    public void ResolveWorkerSecretReferences_FailsClosedWhenReferenceIsUnavailable()
    {
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AzureBlob,
            AzureBlob = new AzureBlobOptions
            {
                ConnectionString = "env:MISSING_CONNECTION",
                ContainerName = "gp-results"
            }
        };

        var action = () => GdalWorkerServiceCollectionExtensions.ResolveWorkerSecretReferences(
            options,
            _ => null);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*unavailable to the GDAL worker*")
            .And.Message.Should().NotContain("env:MISSING_CONNECTION");
    }
}
