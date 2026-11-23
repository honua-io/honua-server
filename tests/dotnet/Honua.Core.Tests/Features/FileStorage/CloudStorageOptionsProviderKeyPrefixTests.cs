// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.FileStorage;

public class CloudStorageOptionsProviderKeyPrefixTests
{
    [UnitTheory]
    [InlineData(CloudStorageProvider.AwsS3, "s3-prefix")]
    [InlineData(CloudStorageProvider.AzureBlob, "blob-prefix")]
    [InlineData(CloudStorageProvider.Local, null)]
    public void ProviderKeyPrefix_ReturnsTheSelectedProvidersPrefixAsConfigured(CloudStorageProvider provider, string? expected)
    {
        var options = new CloudStorageOptions
        {
            Provider = provider,
            AwsS3 = new AwsS3Options { KeyPrefix = "s3-prefix" },
            AzureBlob = new AzureBlobOptions { BlobPrefix = "blob-prefix" }
        };

        Assert.Equal(expected, options.ProviderKeyPrefix());
    }

    [UnitTest]
    public void ProviderKeyPrefix_KeepsTheConfiguredFormForCallersToNormalise()
    {
        var options = new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = new AwsS3Options { KeyPrefix = "/tenant/honua/" }
        };

        Assert.Equal("/tenant/honua/", options.ProviderKeyPrefix());
    }

    [UnitTest]
    public void ProviderKeyPrefix_WithoutProviderSection_IsNull()
    {
        var options = new CloudStorageOptions { Provider = CloudStorageProvider.AzureBlob };

        Assert.Null(options.ProviderKeyPrefix());
    }
}
