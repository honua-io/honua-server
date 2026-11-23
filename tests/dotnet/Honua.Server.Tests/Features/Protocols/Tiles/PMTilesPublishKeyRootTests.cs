// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Tiles;

[Collection("Unit")]
public sealed class PMTilesPublishKeyRootTests
{
    [UnitTheory]
    [InlineData(CloudStorageProvider.AwsS3, "honua", "honua/pmtiles")]
    [InlineData(CloudStorageProvider.AwsS3, "/tenant/honua/", "tenant/honua/pmtiles")]
    [InlineData(CloudStorageProvider.AzureBlob, "blobs", "blobs/pmtiles")]
    [InlineData(CloudStorageProvider.Local, "ignored", "pmtiles")]
    [InlineData(CloudStorageProvider.AwsS3, " ", "pmtiles")]
    public void Resolve_PutsTheSelectedProvidersPrefixBeforeThePublishPrefix(
        CloudStorageProvider provider,
        string providerPrefix,
        string expected)
    {
        PMTilesPublishKeyRoot.Resolve(Options(provider, providerPrefix, "pmtiles")).Should().Be(expected);
    }

    [UnitTest]
    public void Resolve_TrimsSlashesFromThePublishPrefix()
    {
        PMTilesPublishKeyRoot.Resolve(Options(CloudStorageProvider.AwsS3, "honua", "/archives/")).Should().Be("honua/archives");
    }

    private static CloudStorageOptions Options(CloudStorageProvider provider, string providerPrefix, string publishPrefix)
        => new()
        {
            Provider = provider,
            AwsS3 = new AwsS3Options { KeyPrefix = providerPrefix },
            AzureBlob = new AzureBlobOptions { BlobPrefix = providerPrefix },
            PMTilesPublish = new PMTilesPublishOptions { KeyPrefix = publishPrefix }
        };
}
