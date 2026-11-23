// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Amazon.S3;
using Azure;
using FluentAssertions;
using Honua.FileStorage;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.FileStorage;

[Collection("Unit")]
public sealed class ObjectStoreAccessDeniedTests
{
    [UnitTest]
    public void S3_Forbidden_IsAccessDenied()
    {
        var failure = new AmazonS3Exception("refused") { StatusCode = HttpStatusCode.Forbidden };

        AwsS3FileStorage.IsAccessDeniedFailure(failure).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public void S3_OtherStatuses_AreNotAccessDenied(HttpStatusCode status)
    {
        var failure = new AmazonS3Exception("refused") { StatusCode = status };

        AwsS3FileStorage.IsAccessDeniedFailure(failure).Should().BeFalse();
    }

    [UnitTest]
    public void AzureBlob_Forbidden_IsAccessDenied()
    {
        AzureBlobFileStorage.IsAccessDeniedFailure(new RequestFailedException(403, "refused")).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData(503)]
    [InlineData(409)]
    public void AzureBlob_OtherStatuses_AreNotAccessDenied(int status)
    {
        AzureBlobFileStorage.IsAccessDeniedFailure(new RequestFailedException(status, "refused")).Should().BeFalse();
    }

    [UnitTest]
    public void NonProviderFailures_AreNotAccessDenied()
    {
        var failure = new IOException("connection reset");

        AwsS3FileStorage.IsAccessDeniedFailure(failure).Should().BeFalse();
        AzureBlobFileStorage.IsAccessDeniedFailure(failure).Should().BeFalse();
    }
}
