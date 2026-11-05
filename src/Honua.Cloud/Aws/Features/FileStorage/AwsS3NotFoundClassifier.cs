// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Amazon.S3;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.FileStorage;

internal sealed class AwsS3NotFoundClassifier : ICloudNotFoundClassifier
{
    public bool IsNotFound(Exception exception) =>
        exception is AmazonS3Exception s3 &&
        (s3.StatusCode == HttpStatusCode.NotFound
            || string.Equals(s3.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase));
}
