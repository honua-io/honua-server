// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.ObjectStorage;
using Honua.FileStorage;
using Honua.TestKit.Attributes;

namespace Honua.Aws.UnitTests;

public sealed class AwsObjectStoreConnectorTests
{
    private static readonly ObjectStorageConnection BuiltIn = AwsObjectStoreClientFactoryTests.Customer with
    {
        BuiltIn = true,
        RoleArn = null,
        ExpectedBucketOwner = "123456789012",
    };

    [UnitTest]
    public async Task Browse_ListsOneCappedFolderLevelAndSniffsKindsFromRangedReads()
    {
        using var s3 = new FakeS3();
        using var factory = s3.CreateFactory();
        s3.Objects["layers/a.tif"] = Tiff("LAYOUT=COG");
        s3.Objects["layers/b.tif"] = Tiff("plain");
        s3.Objects["layers/c.pmtiles"] = Encoding.ASCII.GetBytes("PMTiles\u0003rest");
        s3.Objects["layers/d.parquet"] = Encoding.ASCII.GetBytes("PAR1rest");
        s3.Objects["layers/e.csv"] = Encoding.ASCII.GetBytes("id,name");
        s3.Folders.Add("layers/2026/");
        var connector = Connector(factory);

        var page = await connector.BrowseAsync(new ObjectStorageBrowseRequest { Connection = BuiltIn, Prefix = "layers/" }, CancellationToken.None);

        var listed = Assert.Single(s3.Lists);
        Assert.Equal("/", listed.Delimiter);
        Assert.Equal(AwsObjectStoreConnector.PageSize, listed.MaxKeys);
        Assert.Equal(["layers/2026/"], page.Folders);
        Assert.Equal(
            ["cog", "geotiff", "pmtiles", "geoparquet", "other"],
            page.Objects.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Kind));
        Assert.All(s3.Reads, read => Assert.Equal(ObjectStorageObjectKinds.SniffBytes - 1, read.ByteRange.End));
        Assert.Equal(BuiltIn.ExpectedBucketOwner, listed.ExpectedBucketOwner);
        Assert.All(s3.Reads, read => Assert.Equal(BuiltIn.ExpectedBucketOwner, read.ExpectedBucketOwner));
    }

    [UnitTest]
    public async Task Browse_SniffsAtMostTheCappedNumberOfObjects()
    {
        using var s3 = new FakeS3();
        using var factory = s3.CreateFactory();
        for (var index = 0; index < AwsObjectStoreConnector.MaxSniffedObjects + 10; index++)
        {
            s3.Objects[$"layers/{index:D3}.tif"] = Tiff("plain");
        }

        var page = await Connector(factory).BrowseAsync(new ObjectStorageBrowseRequest { Connection = BuiltIn, Prefix = "layers/" }, CancellationToken.None);

        Assert.Equal(AwsObjectStoreConnector.MaxSniffedObjects + 10, page.Objects.Count);
        Assert.Equal(AwsObjectStoreConnector.MaxSniffedObjects, s3.Reads.Count);
    }

    [UnitTest]
    public async Task RequesterPaysBucket_IsRefusedWithAClearMessage()
    {
        using var s3 = new FakeS3 { ListError = Denied(), Payer = "Requester" };
        using var factory = s3.CreateFactory();

        var refused = await Assert.ThrowsAsync<ImportObjectStoreException>(
            () => Connector(factory).BrowseAsync(new ObjectStorageBrowseRequest { Connection = BuiltIn, Prefix = "layers/" }, CancellationToken.None));

        Assert.Equal(ImportObjectStoreProblems.RequesterPays, refused.Code);
        Assert.Contains("Requester-pays", refused.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task Test_ReportsHealthyWhenTheRegionMatchesAndTheRoleCanList()
    {
        using var s3 = new FakeS3();
        using var factory = s3.CreateFactory();

        var result = await Connector(factory).TestAsync(BuiltIn, CancellationToken.None);

        Assert.True(result.IsHealthy, result.Message);
        Assert.Equal(1, Assert.Single(s3.Lists).MaxKeys);
    }

    [UnitTest]
    public async Task Test_ReportsARegionMismatch()
    {
        using var s3 = new FakeS3 { Region = "us-west-2" };
        using var factory = s3.CreateFactory();

        var result = await Connector(factory).TestAsync(BuiltIn, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Contains("us-west-2", result.Message, StringComparison.Ordinal);
    }

    [UnitTest]
    public async Task Browse_ListsAnUnreadableObjectAsOther()
    {
        using var s3 = new FakeS3();
        using var factory = s3.CreateFactory();
        s3.Objects["layers/a.pmtiles"] = Encoding.ASCII.GetBytes("PMTiles\u0003rest");
        s3.Objects["layers/b.tif"] = Tiff("LAYOUT=COG");
        s3.Unreadable.Add("layers/b.tif");

        var page = await Connector(factory).BrowseAsync(new ObjectStorageBrowseRequest { Connection = BuiltIn, Prefix = "layers/" }, CancellationToken.None);

        Assert.Equal(["pmtiles", "other"], page.Objects.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Kind));
    }

    [UnitTheory]
    [InlineData(null, "us-east-1")]
    [InlineData("", "us-east-1")]
    [InlineData("EU", "eu-west-1")]
    [InlineData("ap-southeast-2", "ap-southeast-2")]
    public void BucketRegion_MapsTheLegacyLocationAnswers(string? location, string expected)
        => Assert.Equal(expected, AwsObjectStoreConnector.BucketRegion(location));

    [UnitTest]
    public async Task Test_ReportsAnUnreachableServiceAsUnhealthy()
    {
        using var s3 = new FakeS3 { ListError = new AmazonClientException("network down") };
        using var factory = s3.CreateFactory();

        var result = await Connector(factory).TestAsync(BuiltIn, CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Contains("could not be reached", result.Message, StringComparison.Ordinal);
    }

    private static AwsObjectStoreConnector Connector(AwsObjectStoreClientFactory factory) => new(factory);

    private static byte[] Tiff(string ghost)
        => [.. "II*\0"u8.ToArray(), 8, 0, 0, 0, .. Encoding.ASCII.GetBytes("GDAL_STRUCTURAL_METADATA_SIZE=000140 bytes\n" + ghost)];

    private static AmazonS3Exception Denied()
        => new("Access Denied") { ErrorCode = "AccessDenied", StatusCode = HttpStatusCode.Forbidden };
}

internal sealed class FakeS3() : AmazonS3Client(
    new BasicAWSCredentials("unit", "unit"),
    new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast1 })
{
    public SortedDictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public List<string> Folders { get; } = [];

    public List<ListObjectsV2Request> Lists { get; } = [];

    public List<GetObjectRequest> Reads { get; } = [];

    public Exception? ListError { get; init; }

    public HashSet<string> Unreadable { get; } = [];

    public string Payer { get; init; } = "BucketOwner";

    public string Region { get; init; } = "eu-west-1";

    public override Task<GetBucketLocationResponse> GetBucketLocationAsync(
        GetBucketLocationRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new GetBucketLocationResponse { Location = S3Region.FindValue(Region) });

    public override Task<GetBucketRequestPaymentResponse> GetBucketRequestPaymentAsync(
        GetBucketRequestPaymentRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new GetBucketRequestPaymentResponse { Payer = Payer });

    public override Task<ListObjectsV2Response> ListObjectsV2Async(
        ListObjectsV2Request request,
        CancellationToken cancellationToken = default)
    {
        lock (Lists)
        {
            Lists.Add(request);
        }

        if (ListError is not null)
        {
            throw ListError;
        }

        return Task.FromResult(new ListObjectsV2Response
        {
            S3Objects = Objects.Select(pair => new S3Object { Key = pair.Key, Size = pair.Value.Length }).ToList(),
            CommonPrefixes = Folders,
            IsTruncated = false,
        });
    }

    public AwsObjectStoreClientFactory CreateFactory() => new(new AwsObjectStoreClientSeams { HostClient = this });

    public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
    {
        lock (Reads)
        {
            Reads.Add(request);
        }

        if (Unreadable.Contains(request.Key))
        {
            throw new AmazonS3Exception("Access Denied") { ErrorCode = "AccessDenied", StatusCode = HttpStatusCode.Forbidden };
        }

        var bytes = Objects[request.Key];
        var length = (int)Math.Min(bytes.Length, request.ByteRange.End + 1);
        return Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream(bytes, 0, length) });
    }
}
