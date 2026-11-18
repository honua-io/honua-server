// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.ObjectStorage;
using Honua.Core.Features.Tiles.PMTiles;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.ObjectStorage;

public sealed class ObjectStorageConnectionResolverTests
{
    private const string Owner = "123456789012";

    private static readonly ObjectStorageConnection Stored = new()
    {
        Id = Guid.Parse("0c1d2e3f-4a5b-4c6d-8e7f-9a0b1c2d3e4f"),
        Name = "Customer",
        Bucket = "customer-imports",
        Prefix = "parcels/",
        Region = "us-west-2",
        RoleArn = "arn:aws:iam::123456789012:role/honua-connection-imports",
        ExternalId = "minted-external-id",
    };

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("cog", "II*\0\b\0\0\0GDAL_STRUCTURAL_METADATA_SIZE=000140 bytes\nLAYOUT=COG\n")]
    [InlineData("geotiff", "II*\0\b\0\0\0plain strip tiff")]
    [InlineData("geotiff", "MM\0*\0\0\0\b")]
    [InlineData("cog", "II+\0\b\0\0\0\0\0\0\0LAYOUT=COG")]
    [InlineData("pmtiles", "PMTiles\u0003")]
    [InlineData("geoparquet", "PAR1")]
    [InlineData("other", "{\"type\":\"FeatureCollection\"}")]
    [InlineData("other", "II")]
    public void Kind_IsDetectedFromTheHeader(string expected, string header)
        => ObjectStorageObjectKinds.Detect(Encoding.Latin1.GetBytes(header)).Should().Be(expected);

    [Trait("Tier", "Fast")]
    [Fact]
    public void BuiltIns_CoverOnlyListedFoldersAndThePublicPublishPrefix()
    {
        var builtIns = ObjectStorageBuiltInConnections.From(Storage(), EnabledBuiltIns("drop", "survey/2026"));

        builtIns.Select(connection => (connection.Bucket, connection.Prefix))
            .Should().Equal(("honua-data", "tenant-a/drop/"), ("honua-data", "tenant-a/survey/2026/"), ("honua-data", "tenant-a/pmtiles/"));
        builtIns.Should().OnlyContain(connection => connection.BuiltIn
            && connection.RoleArn == null
            && connection.ExternalId == null
            && connection.ExpectedBucketOwner == Owner);
        ObjectStorageBuiltInConnections.From(Storage(), EnabledBuiltIns("drop", "survey/2026")).Select(connection => connection.Id)
            .Should().Equal(builtIns.Select(connection => connection.Id), "built-in ids are derived from configuration");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void BuiltIns_AreOffByDefaultAndNeverCoverTheWholeRoot()
    {
        ObjectStorageBuiltInConnections.From(Storage(), new ObjectStorageBuiltInOptions()).Should().BeEmpty("built-ins are opt-in");
        var noFolders = EnabledBuiltIns();
        noFolders.PublicFiles = false;
        ObjectStorageBuiltInConnections.From(Storage(), noFolders).Should().BeEmpty("no folder means no data connection");
        var withoutOwner = EnabledBuiltIns("drop");
        withoutOwner.ExpectedBucketOwner = null;
        ObjectStorageBuiltInConnections.From(Storage(), withoutOwner).Should().BeEmpty("the bucket owner must be pinned");
    }

    [Trait("Tier", "Fast")]
    [Theory]
    [InlineData("exports")]
    [InlineData("imports/batch")]
    [InlineData("audit")]
    [InlineData("gp/outputs")]
    [InlineData("pmtiles/2026")]
    [InlineData("mapserver/export-tiles")]
    [InlineData("attachments")]
    public void BuiltIns_RefuseFoldersTheServerWrites(string folder)
    {
        var builtIn = EnabledBuiltIns(folder);
        builtIn.PublicFiles = false;

        ObjectStorageBuiltInConnections.From(Storage(), builtIn).Should().BeEmpty();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void BuiltIns_AreOffForEndpointOverridesStaticKeysAndOtherProviders()
    {
        var builtIn = EnabledBuiltIns("drop");

        ObjectStorageBuiltInConnections.From(new CloudStorageOptions { Provider = CloudStorageProvider.Local }, builtIn).Should().BeEmpty();
        ObjectStorageBuiltInConnections.From(Storage(s3 => s3 with { ServiceUrl = "http://minio:9000" }), builtIn).Should().BeEmpty();
        ObjectStorageBuiltInConnections.From(Storage(s3 => s3 with { AccessKeyId = "key", SecretAccessKey = "secret" }), builtIn)
            .Should().BeEmpty();
    }

    private static CloudStorageOptions Storage(Func<AwsS3Options, AwsS3Options>? change = null)
    {
        var s3 = new AwsS3Options { BucketName = "honua-data", Region = "us-east-1", KeyPrefix = "tenant-a" };
        return new CloudStorageOptions
        {
            Provider = CloudStorageProvider.AwsS3,
            AwsS3 = change is null ? s3 : change(s3),
            PMTilesPublish = new PMTilesPublishOptions { UrlStrategy = PMTilesUrlStrategy.PublicUrl, KeyPrefix = "pmtiles" },
        };
    }

    private static ObjectStorageBuiltInOptions EnabledBuiltIns(params string[] folders)
    {
        var options = new ObjectStorageBuiltInOptions { Enabled = true, PublicFiles = true, ExpectedBucketOwner = Owner };
        foreach (var folder in folders)
        {
            options.DataPrefixes.Add(folder);
        }

        return options;
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task Resolver_ResolvesAKeyUnderTheConnectionForImportOrExport()
    {
        var resolver = Resolver(Stored);

        var resolution = await resolver.ResolveObjectAsync(Stored.Id, "parcels/2026/lots.parquet", CancellationToken.None);

        resolution.Succeeded.Should().BeTrue();
        resolution.Connection.Should().Be(Stored);
        resolution.SourceUri.Should().Be("s3://customer-imports/parcels/2026/lots.parquet");
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task Resolver_RefusesUnknownConnectionsAndKeysOutsideThePrefix()
    {
        var resolver = Resolver(Stored);

        (await resolver.ResolveObjectAsync(Guid.NewGuid(), "parcels/a.geojson", CancellationToken.None))
            .Failure.Should().Be(ObjectStorageResolveFailure.ConnectionNotFound);
        (await resolver.ResolveObjectAsync(Stored.Id, "other/a.geojson", CancellationToken.None))
            .Failure.Should().Be(ObjectStorageResolveFailure.InvalidKey);
        (await resolver.ResolveObjectAsync(Stored.Id, "parcels/../other/a.geojson", CancellationToken.None))
            .Failure.Should().Be(ObjectStorageResolveFailure.InvalidKey);
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public void ConnectionText_NeverPrintsTheExternalId()
        => Stored.ToString().Should().NotContain(Stored.ExternalId!);

    private static ObjectStorageConnectionResolver Resolver(params ObjectStorageConnection[] stored)
        => new(new StaticMonitor<CloudStorageOptions>(new()), new StaticMonitor<ObjectStorageConnectionOptions>(new()), store: new ListStore(stored));

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class ListStore(ObjectStorageConnection[] stored) : IObjectStorageConnectionStore
    {
        public Task<bool> SaveSetupAsync(ObjectStorageConnectionSetup setup, int maxOpenForMinter, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<ObjectStorageConnectionSetup?> FindOpenSetupAsync(string externalId, CancellationToken cancellationToken)
            => Task.FromResult<ObjectStorageConnectionSetup?>(null);

        public Task<ObjectStorageCreateOutcome> CreateAsync(ObjectStorageConnection connection, CancellationToken cancellationToken)
            => Task.FromResult(ObjectStorageCreateOutcome.SetupRejected);

        public Task<ObjectStorageConnection?> GetAsync(Guid id, CancellationToken cancellationToken)
            => Task.FromResult(stored.FirstOrDefault(connection => connection.Id == id));

        public Task<IReadOnlyList<ObjectStorageConnection>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ObjectStorageConnection>>(stored);

        public Task<bool> UpdateAsync(ObjectStorageConnection connection, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
