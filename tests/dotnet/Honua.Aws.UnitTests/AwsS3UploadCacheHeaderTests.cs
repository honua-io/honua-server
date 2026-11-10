// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.S3.Model;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.TestKit.Attributes;

namespace Honua.Aws.UnitTests;

public sealed class AwsS3UploadCacheHeaderTests
{
    [UnitTheory]
    [InlineData(StoredObjectCachePolicy.NotShared, "private, no-store")]
    [InlineData(StoredObjectCachePolicy.PublicReplacedInPlace, "public, max-age=60, s-maxage=300")]
    public void ApplyResponseHeaders_EveryUpload_CarriesTheCacheControlOfItsPolicy(
        StoredObjectCachePolicy policy, string expected)
    {
        var headers = new PutObjectRequest().Headers;

        AwsS3FileStorage.ApplyResponseHeaders(headers, Upload(policy));

        Assert.Equal(expected, headers.CacheControl);
    }

    [UnitTest]
    public void ApplyResponseHeaders_UploadThatDoesNotSay_IsNotShared()
    {
        var headers = new InitiateMultipartUploadRequest().Headers;

        AwsS3FileStorage.ApplyResponseHeaders(headers, new FileUploadRequest
        {
            Content = Stream.Null,
            FileName = "tile.png",
            ContentType = "image/png",
        });

        Assert.Equal(StoredObjectCacheControl.NotShared, headers.CacheControl);
    }

    [UnitTest]
    public void ApplyResponseHeaders_StoredEncoding_IsDeclared()
    {
        var headers = new PutObjectRequest().Headers;

        AwsS3FileStorage.ApplyResponseHeaders(
            headers, Upload(StoredObjectCachePolicy.NotShared) with { ContentEncoding = "gzip" });

        Assert.Equal("gzip", headers.ContentEncoding);
    }

    private static FileUploadRequest Upload(StoredObjectCachePolicy policy) => new()
    {
        Content = Stream.Null,
        FileName = "archive.pmtiles",
        ContentType = "application/vnd.pmtiles",
        CachePolicy = policy,
    };
}
